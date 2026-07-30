using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using DailyDuty.Localization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiLib.Classes;
using KamiLib.Extensions;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace DailyDuty.Classes;

/// <summary>
///     Shows a hint in the Duty Finder (ContentsFinder) window listing collectables
///     (mounts, minions, orchestrion rolls, Triple Triad cards, etc.) that drop from the
///     currently selected duty but have not yet been obtained by this character.
/// </summary>
public unsafe class CollectableController : IDisposable {
    private const string EmbeddedResourceName = "DailyDuty.Resources.DutyCollectables.json";

    // Entry shape: [itemId, type, ...relatedItemIds]
    // type 4 (timeworn orchestrion roll) is unlocked by checking the resulting orchestrion
    // roll item(s) in relatedItemIds instead of the itemId itself. All other types check
    // the itemId directly.
    private readonly record struct CollectableEntry(uint ItemId, byte Type, uint[] RelatedItemIds);

    private readonly Dictionary<uint, List<CollectableEntry>>? dutyCollectables;

    private TextNode? infoTextNode;

    private bool hasLastSelection;
    private ContentsId.ContentsType lastContentType;
    private uint lastContentId;

    // ────────────────────────────────────────────────────────────────────────────────
    // 任務列表逐行金星標示
    //
    // 🔴 v7.20.0.16 曾改成「每幀走 AtkComponentTreeList.Items 對帳」,實機崩潰
    //    (AccessViolationException,鑑識報告:_logs/forensics-20260731-collectable-mark-crash.md)。
    //    死因是 `Items[i]->Renderer->RowTemplateNodeList[3]` 讀出 0x9004ebbee4e4915e
    //    ——一個 non-canonical 值,代表 renderer 那塊記憶體已經被回收重用。
    //    null 檢查、界檢查、try/catch 三層全部通過/無效:AVE 在 .NET Core 是
    //    corrupted-state exception,任何 managed catch 都攔不到,行程當場終止。
    //
    // 因此本檔的紅線:**只解參考遊戲在當下親手交給我們、而且它自己下一步就要用的指標。**
    // 那個時機只有一個——populate detour。除此之外一律只讀純量(int/bool),
    // 而且**不跨幀保存任何原生指標**。
    // ────────────────────────────────────────────────────────────────────────────────

    // ContentsFinder 的副本列樣板 renderer 節點 id(與 DutyRoulette 模組相同)。
    private const uint DutyRowRendererNodeId = 6;

    // 列樣板的節點索引:3 = 副本名稱,4 = 等級(原生 populate 填的就是這兩個)。
    private const int NameNodeIndex = 3;
    private const int LevelNodeIndex = 4;

    private const int DebugDumpLimit = 40;

    // 強制重跑 populate 後的冷卻幀數:避免「重跑 → 列數又變 → 再重跑」互相追逐。
    private const int ForcedUpdateCooldownFrames = 15;

    private Hook<AtkComponentListItemPopulator.PopulateDelegate>? onDutyListPopulate;
    private readonly Dictionary<uint, bool> missingCache = [];
    private Dictionary<string, uint>? nameToCfc;
    private int debugDumpRemaining;

    // 開窗時從 renderer 讀一次的樣板欄位數。之後 detour 只看這個 int,不再碰 renderer。
    // 0 = 沒讀到 → 整個標示功能停用(fail-closed,絕不猜)。
    private int templateNodeCount;

    // 收斂狀態機(全部是純量,沒有任何原生指標)。
    private bool repopulatePending;
    private bool inForcedUpdate;
    private int forcedUpdateCooldown;
    private int lastListLength = int.MinValue;
    private bool lastLayoutRefreshPending;

    // 金星圖示的 SeString payload,標示時接在該列「原始位元組」前面,
    // 保留原名裡可能存在的其他 payload。
    private static readonly byte[] StarPrefix =
        new SeStringBuilder().AddIcon(BitmapFontIcon.GoldStar).Encode();

    private static readonly FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor MarkColor
        = new Vector4(1.0f, 0.83f, 0.29f, 1.0f).ToByteColor();

    public CollectableController() {
        dutyCollectables = LoadEmbeddedData();

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", OnContentsFinderSetup);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnContentsFinderFinalize);

        System.ContentsFinderController.OnAttach += AttachNodes;
        System.ContentsFinderController.OnDetach += DetachNodes;
        System.ContentsFinderController.OnRefresh += OnContentsFinderRefresh;
        System.ContentsFinderController.OnUpdate += OnContentsFinderUpdate;
    }

    public void Dispose() {
        Service.AddonLifecycle.UnregisterListener(OnContentsFinderSetup, OnContentsFinderFinalize);

        System.ContentsFinderController.OnAttach -= AttachNodes;
        System.ContentsFinderController.OnDetach -= DetachNodes;
        System.ContentsFinderController.OnRefresh -= OnContentsFinderRefresh;
        System.ContentsFinderController.OnUpdate -= OnContentsFinderUpdate;

        onDutyListPopulate?.Dispose();
        onDutyListPopulate = null;

        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
    }

    #region 指標合理性

    // user-mode x64 的合法範圍。上界這一條才是關鍵:v7.20.0.16 崩潰時讀到的
    // 0x9004ebbee4e4915e 是 non-canonical,只有上界檔得住;`< 0x10000` 只擋得掉
    // 明顯的 null 附近偏移。兩者都只是純算術比較,不解參考、不做投機探測。
    private const ulong MinPlausiblePointer = 0x10000UL;
    private const ulong MaxPlausiblePointer = 0x00007FFFFFFFFFFFUL;

    private static bool IsPlausible(void* pointer) {
        var value = (ulong) (nuint) pointer;
        return value is >= MinPlausiblePointer and <= MaxPlausiblePointer;
    }

    /// <summary>
    ///     把列樣板節點轉成 <see cref="AtkTextNode" />,但先確認它真的是文字節點。
    ///     萬一 hook 被掛到了別的列樣板(例如分類標題列),索引 3/4 就不是名稱/等級,
    ///     這道型別檢查會讓我們安靜放棄,而不是把非文字節點當 AtkTextNode 解參考。
    /// </summary>
    private static AtkTextNode* AsTextNode(AtkResNode* node) {
        if (!IsPlausible(node)) return null;
        if (node->Type != NodeType.Text) return null;

        return (AtkTextNode*) node;
    }

    private static bool SameColor(FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor left, FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor right)
        => left.R == right.R && left.G == right.G && left.B == right.B && left.A == right.A;

    #endregion

    #region 開窗 / 關窗

    private void OnContentsFinderSetup(AddonEvent type, AddonArgs args) => HookSafety.ExecuteSafe(() => {
        ResetListState();

        if (dutyCollectables is null) return;

        // 解鎖狀態在開窗時重抓一次(同場學到新收藏品的過期程度可接受)。
        missingCache.Clear();
        debugDumpRemaining = DebugDumpLimit;

        var addon = args.GetAddon<AddonContentsFinder>();
        if (!IsPlausible(addon)) return;

        var dutyList = addon->DutyList;
        if (!IsPlausible(dutyList)) return;

        var renderer = dutyList->GetItemRendererByNodeId(DutyRowRendererNodeId);
        if (!IsPlausible(renderer)) {
            Service.Log.Warning($"[CollectableController] ContentsFinder 找不到列樣板 renderer(node id {DutyRowRendererNodeId}),列表標示停用。");
            return;
        }

        // 這是本檔唯一一次讀 renderer,而且是在 addon 剛 setup、由 Dalamud 交給我們的當下。
        // 讀到的欄位數之後只以 int 形式使用,不保存 renderer 指標。
        var nodeCount = renderer->RowTemplateNodeCount;
        if (nodeCount <= LevelNodeIndex) {
            Service.Log.Warning($"[CollectableController] 列樣板只有 {nodeCount} 個節點(需要 > {LevelNodeIndex}),列表標示停用。");
            return;
        }

        var populateMethod = (nint) renderer->Populator.Populate;
        if ((ulong) populateMethod < MinPlausiblePointer) {
            Service.Log.Warning("[CollectableController] 列樣板 populate 函式位址無效,列表標示停用。");
            return;
        }

        templateNodeCount = nodeCount;

        onDutyListPopulate = Service.Hooker.HookFromAddress<AtkComponentListItemPopulator.PopulateDelegate>(populateMethod, OnPopulateHook);
        onDutyListPopulate?.Enable();

        // 首開時列在 hook 掛上之前就已經 populate 完了。
        // ⚠️ 不去「自己補畫」那些列——那正是 v7.20.0.16 的崩潰來源。
        // 改成請遊戲自己重跑一次 populate,標示會經由 hook 補上。
        // 這件事延後到 PostUpdate 做:PostSetup 當下 addon 的陣列資料還不一定就緒。
        repopulatePending = true;
    }, Service.Log);

    private void OnContentsFinderFinalize(AddonEvent type, AddonArgs args) => HookSafety.ExecuteSafe(ResetListState, Service.Log);

    private void ResetListState() {
        onDutyListPopulate?.Dispose();
        onDutyListPopulate = null;

        templateNodeCount = 0;
        repopulatePending = false;
        inForcedUpdate = false;
        forcedUpdateCooldown = 0;
        lastListLength = int.MinValue;
        lastLayoutRefreshPending = false;
    }

    #endregion

    #region populate detour —— 唯一會碰到列節點的地方

    /// <summary>
    ///     遊戲正在填某一列的內容,並且把該列的節點陣列與資料項直接交給我們。
    ///     這是整個功能裡唯一一個「原生指標由遊戲當場提供、而且它自己下一步就要用同一組指標」
    ///     的時機,所以也是唯一允許碰列節點的地方。
    /// </summary>
    private void OnPopulateHook(AtkUnitBase* unitBase, AtkComponentListItemPopulator.ListItemInfo* listItemInfo, AtkResNode** nodeList) {
        var hook = onDutyListPopulate;
        if (hook is null) return;

        // 原生 populate 一律先跑完:就算我們底下整段失敗,列的內容也是完整的。
        try {
            hook.Original(unitBase, listItemInfo, nodeList);
        }
        catch (Exception exception) {
            Service.Log.Error(exception, "[CollectableController] 原生 populate 呼叫失敗。");
            return;
        }

        // 這裡刻意用 try/catch 而不是 HookSafety.ExecuteSafe:主體要用到指標區域變數。
        // 語意等價(HookSafety 本身就是 try/catch + log)。
        // ⚠️ 這一層只擋得住 managed 例外(Lumina 查表、索引、解析);對指標錯誤無效,
        //    指標安全完全靠上面的來源限制與下面的逐項檢查。
        try {
            ApplyMarkToPopulatedRow(listItemInfo, nodeList);
        }
        catch (Exception exception) {
            Service.Log.Error(exception, "[CollectableController] 套用列表標示時發生例外。");
        }
    }

    private void ApplyMarkToPopulatedRow(AtkComponentListItemPopulator.ListItemInfo* listItemInfo, AtkResNode** nodeList) {
        if (dutyCollectables is null) return;

        // 開窗時沒能確認樣板欄位數 → 整列不碰(fail-closed)。
        if (templateNodeCount <= LevelNodeIndex) return;

        if (!IsPlausible(listItemInfo) || !IsPlausible(nodeList)) return;

        var item = listItemInfo->ListItem;
        if (!IsPlausible(item)) return;

        var nameNode = AsTextNode(nodeList[NameNodeIndex]);
        if (nameNode is null) return;

        var levelNode = AsTextNode(nodeList[LevelNodeIndex]);

        // StringValues[0] 是這一列的名稱本體(原生 populate 也是拿它填文字節點)。
        if (item->StringValues.LongCount <= 0) return;

        var rawName = item->StringValues[0];
        if (!rawName.HasValue || !IsPlausible(rawName.Value)) return;

        var marking = System.CollectableConfig is { Enabled: true, MarkDutyList: true };

        var shouldMark = false;
        var dutyName = string.Empty;
        var matched = false;

        if (marking) {
            // 原始位元組可能含 payload(鎖頭圖示等),Utf8String.ToString() 會把 payload
            // 混進字串害比對失敗——用 SeString 解析取純文字再比對。
            dutyName = SeString.Parse(rawName.AsSpan()).TextValue.Trim();
            nameToCfc ??= BuildNameMap();

            if (nameToCfc.TryGetValue(dutyName, out var cfcId)) {
                matched = true;
                shouldMark = HasMissing(cfcId);
            }
        }

        if (debugDumpRemaining > 0) {
            debugDumpRemaining--;
            Service.Log.Debug($"[Collectable] name='{dutyName}' matched={matched} mark={shouldMark}");
        }

        if (shouldMark) {
            // 金星 + 原始位元組 + null 終止符(原生 SetText 讀到 null 為止)。
            var raw = rawName.AsSpan();
            var buffer = new byte[StarPrefix.Length + raw.Length + 1];
            StarPrefix.CopyTo(buffer, 0);
            raw.CopyTo(buffer.AsSpan(StarPrefix.Length));
            buffer[^1] = 0;

            nameNode->SetText(buffer);
            nameNode->TextColor = MarkColor;
            return;
        }

        // 原生 populate 會重寫文字(星號因此自然消失)但**不會**重設顏色。
        // 列被回收給別的副本、或使用者關掉了這個功能時,金色會殘留。
        //
        // 判斷「這一列是不是我們染的」直接看節點現在的顏色,不維護任何索引表——
        // 舊版用 renderer 的 NodeId 當 key,列被回收重用時會錯位。節點自己就是狀態。
        // 只碰金色的列,也就不會踩到 DutyRoulette 模組染的輪盤列。
        if (levelNode is not null && SameColor(nameNode->TextColor, MarkColor)) {
            nameNode->TextColor = levelNode->TextColor;
        }
    }

    #endregion

    #region 收斂 —— 只讀純量,請遊戲自己重跑 populate

    /// <summary>
    ///     金星只在 populate 時套上,所以任何「沒有重跑 populate」的路徑都會留下沒星號的列:
    ///     首開(列早於 hook 掛上就填好了)、展開/收合分類、資料重載。
    ///
    ///     這裡不去自己走列表補畫,而是偵測到列表形狀變了就請遊戲重跑一次 populate。
    ///     🔑 偵測**只讀純量欄位**(int / bool),不解參考 Items 裡的任何元素,
    ///        也不保存任何原生指標——addon 指標由 AddonLifecycle 當幀交付,用完即棄。
    /// </summary>
    private void ConvergeDutyListMarks(AddonContentsFinder* addon) {
        if (dutyCollectables is null || onDutyListPopulate is null) return;
        if (inForcedUpdate) return;
        if (!IsPlausible(addon)) return;

        var dutyList = addon->DutyList;
        if (!IsPlausible(dutyList)) return;

        if (forcedUpdateCooldown > 0) forcedUpdateCooldown--;

        // ListLength = 目前可見的列數。展開/收合分類、切換副本類型、資料重載都會改到它。
        var listLength = dutyList->AtkComponentList.ListLength;
        if (listLength != lastListLength) {
            lastListLength = listLength;
            repopulatePending = true;
        }

        // 遊戲要重排樹狀清單時會把這個旗標設起來(展開/收合)。
        // 只在 false → true 的邊緣觸發,萬一它長期為 true 也不會每幀重跑。
        var layoutRefreshPending = dutyList->LayoutRefreshPending;
        if (layoutRefreshPending && !lastLayoutRefreshPending) repopulatePending = true;
        lastLayoutRefreshPending = layoutRefreshPending;

        if (!repopulatePending || forcedUpdateCooldown > 0) return;

        repopulatePending = false;
        forcedUpdateCooldown = ForcedUpdateCooldownFrames;

        ForceRepopulate(addon);
    }

    /// <summary>
    ///     叫 addon 用目前的陣列資料重跑一次自己的更新,它會連帶重跑列 populate,
    ///     我們的 hook 就會把標示補上。整個過程由遊戲主導,我們不碰任何列節點。
    /// </summary>
    private void ForceRepopulate(AddonContentsFinder* addon) {
        inForcedUpdate = true;

        try {
            var stage = AtkStage.Instance();
            if (!IsPlausible(stage)) return;

            addon->AtkUnitBase.OnRequestedUpdate(stage->GetNumberArrayData(), stage->GetStringArrayData());
        }
        catch (Exception exception) {
            Service.Log.Error(exception, "[CollectableController] 請求列表重整失敗。");
        }
        finally {
            inForcedUpdate = false;
        }
    }

    private void OnContentsFinderRefresh(AddonContentsFinder* addon) {
        // PostRefresh / PostRequestedUpdate:資料換過了,下一幀補一次 populate。
        // 我們自己觸發的那次不算,否則會互相追逐。
        if (inForcedUpdate) return;

        repopulatePending = true;
    }

    #endregion

    private Dictionary<string, uint> BuildNameMap() {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (dutyCollectables is null) return map;

        var sheet = Service.DataManager.GetExcelSheet<ContentFinderCondition>();
        foreach (var cfcId in dutyCollectables.Keys) {
            var row = sheet.GetRowOrDefault(cfcId);
            if (row is null) continue;

            var name = row.Value.Name.ExtractText();
            if (!string.IsNullOrEmpty(name)) {
                map.TryAdd(name, cfcId);
            }
        }

        return map;
    }

    /// <summary>設定變更後呼叫:類型開關會影響「有無未取得」的判定與底部摘要。</summary>
    public void InvalidateCache() {
        missingCache.Clear();
        hasLastSelection = false;

        // 列表標示也要立刻跟著變(包含把功能關掉時把金色收回去)。
        repopulatePending = true;
    }

    private bool HasMissing(uint cfcId) {
        if (missingCache.TryGetValue(cfcId, out var cached)) return cached;
        if (dutyCollectables is null || !dutyCollectables.TryGetValue(cfcId, out var entries)) return false;

        var missing = entries.Any(entry => ShouldShowType(entry.Type) && !IsAcquired(entry));
        missingCache[cfcId] = missing;
        return missing;
    }

    private static Dictionary<uint, List<CollectableEntry>>? LoadEmbeddedData() {
        try {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);

            if (stream is null) {
                Service.Log.Warning($"[CollectableController] Embedded resource '{EmbeddedResourceName}' was not found, duty collectable hints will be disabled.");
                return null;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var raw = JsonConvert.DeserializeObject<Dictionary<string, List<List<uint>>>>(json);
            if (raw is null) {
                Service.Log.Warning("[CollectableController] Embedded duty collectable data was empty, hints will be disabled.");
                return null;
            }

            var result = new Dictionary<uint, List<CollectableEntry>>();

            foreach (var (key, entries) in raw) {
                if (!uint.TryParse(key, out var cfcId)) continue;

                var parsedEntries = new List<CollectableEntry>();

                foreach (var entry in entries) {
                    if (entry.Count < 2) continue;

                    var itemId = entry[0];
                    var type = (byte) entry[1];
                    var relatedItemIds = entry.Count > 2 ? entry.Skip(2).ToArray() : [];

                    parsedEntries.Add(new CollectableEntry(itemId, type, relatedItemIds));
                }

                result[cfcId] = parsedEntries;
            }

            return result;
        }
        catch (Exception e) {
            Service.Log.Warning(e, "[CollectableController] Failed to load embedded duty collectable data, hints will be disabled.");
            return null;
        }
    }

    private void AttachNodes(AddonContentsFinder* addon) {
        if (dutyCollectables is null) return;

        // Placed in the free space at the bottom of the window, beside the "Open DailyDuty"
        // button that DutyRoulette already attaches there (Position(50, 622) Size(130, 28)) -
        // this strip is empty space below the native duty list/detail panel and does not
        // overlap any native ContentsFinder elements.
        // 單行摘要 + 滑鼠停留 tooltip 顯示完整名單。自建 AtkTextNode 的 LineSpacing
        // 預設為 0,MultiLine 會把所有行疊印在同一個 Y(實測 2026-07-30),所以
        // 節點本體絕不多行,明細一律走 tooltip。
        infoTextNode = new TextNode {
            NodeId = 1001,
            Position = new Vector2(16.0f, 598.0f),
            Size = new Vector2(620.0f, 20.0f),
            TextFlags = TextFlags.AutoAdjustNodeSize,
            AlignmentType = AlignmentType.TopLeft,
            FontSize = 12,
            EnableEventFlags = true,
            IsVisible = false,
        };
        System.NativeController.AttachNode(infoTextNode, addon->RootNode);

        hasLastSelection = false;
    }

    private void DetachNodes(AddonContentsFinder* addon) {
        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
    }

    private void OnContentsFinderUpdate(AddonContentsFinder* addon) {
        // 先跑收斂:底部摘要沒話說的時候,列表標示也還是要繼續補上。
        ConvergeDutyListMarks(addon);

        if (infoTextNode is null || dutyCollectables is null) return;

        if (!System.CollectableConfig.Enabled) {
            infoTextNode.IsVisible = false;
            return;
        }

        var agent = AgentContentsFinder.Instance();
        if (agent is null) return;

        var selectedDuty = agent->SelectedDuty;

        // Only recompute when the highlighted/described duty actually changes.
        if (hasLastSelection && selectedDuty.ContentType == lastContentType && selectedDuty.Id == lastContentId) return;

        hasLastSelection = true;
        lastContentType = selectedDuty.ContentType;
        lastContentId = selectedDuty.Id;

        if (selectedDuty.ContentType != ContentsId.ContentsType.Regular || !dutyCollectables.TryGetValue(selectedDuty.Id, out var entries)) {
            infoTextNode.IsVisible = false;
            return;
        }

        RefreshDisplay(entries);
    }

    private void RefreshDisplay(List<CollectableEntry> entries) {
        if (infoTextNode is null) return;

        var itemSheet = Service.DataManager.GetExcelSheet<Item>();
        var missingByType = new SortedDictionary<byte, List<string>>();

        foreach (var entry in entries) {
            if (!ShouldShowType(entry.Type)) continue;
            if (IsAcquired(entry)) continue;

            var itemRow = itemSheet.GetRowOrDefault(entry.ItemId);
            if (itemRow is null) continue;

            var name = itemRow.Value.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            if (!missingByType.TryGetValue(entry.Type, out var names)) {
                names = [];
                missingByType[entry.Type] = names;
            }

            if (!names.Contains(name)) {
                names.Add(name);
            }
        }

        if (missingByType.Count == 0) {
            infoTextNode.IsVisible = false;
            return;
        }

        // 節點本體:一行數量摘要;完整名單放 tooltip(見 AttachNodes 的說明)。
        var summary = new StringBuilder();
        summary.Append(Strings.CollectableHintHeader);
        var detail = new StringBuilder();
        var first = true;
        foreach (var (type, names) in missingByType) {
            if (!first) summary.Append('、');
            first = false;
            summary.Append(GetTypeName(type));
            summary.Append('×');
            summary.Append(names.Count);

            if (detail.Length != 0) detail.Append('\n');
            detail.Append(GetTypeName(type));
            detail.Append(':');
            detail.Append(string.Join('、', names));
        }
        summary.Append(' ');
        summary.Append(Strings.CollectableHintTooltip);

        infoTextNode.Text = summary.ToString();
        infoTextNode.Tooltip = detail.ToString();
        infoTextNode.IsVisible = true;
    }

    private static bool IsAcquired(CollectableEntry entry) {
        if (entry.Type == 4) {
            return entry.RelatedItemIds.Length != 0 && entry.RelatedItemIds.All(IsItemUnlocked);
        }

        return IsItemUnlocked(entry.ItemId);
    }

    private static bool IsItemUnlocked(uint itemId) {
        var itemRow = ExdModule.GetItemRowById(itemId);
        if (itemRow is null) return true; // No data to show anything is missing, don't claim it's missing.

        return UIState.Instance()->IsItemActionUnlocked(itemRow) == 1;
    }

    private static bool ShouldShowType(byte type) => type switch {
        1 => System.CollectableConfig.ShowMounts,
        2 => System.CollectableConfig.ShowMinions,
        3 => System.CollectableConfig.ShowOrchestrionRolls,
        4 => System.CollectableConfig.ShowTimewornOrchestrionRolls,
        5 => System.CollectableConfig.ShowTripleTriadCards,
        6 => System.CollectableConfig.ShowChocoboBarding,
        7 => System.CollectableConfig.ShowOther,
        _ => false,
    };

    private static string GetTypeName(byte type) => type switch {
        1 => Strings.CollectableTypeMount,
        2 => Strings.CollectableTypeMinion,
        3 => Strings.CollectableTypeOrchestrionRoll,
        4 => Strings.CollectableTypeTimewornOrchestrionRoll,
        5 => Strings.CollectableTypeTripleTriadCard,
        6 => Strings.CollectableTypeChocoboBarding,
        7 => Strings.CollectableTypeOther,
        _ => string.Empty,
    };
}
