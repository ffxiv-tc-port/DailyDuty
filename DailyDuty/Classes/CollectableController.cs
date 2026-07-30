using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using DailyDuty.Localization;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
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

    // 任務列表逐行標示。
    //
    // ⚠️ 這裡刻意「不」用 populate hook。標示只在 populate 時套用的話,任何沒有重跑
    // populate 的路徑都會留下沒有星號的列——首開(列在 hook 掛上前就填好了)、展開
    // 分類、換區後重開,全都是同一個根因的不同觸發方式。改成每幀對帳:以列表資料
    // 為準,把「該有星號」的狀態收斂回去,不管中間經過了什麼路徑。
    private readonly Dictionary<uint, bool> missingCache = [];
    private Dictionary<string, uint>? nameToCfc;

    // 列樣板的節點索引:3 = 副本名稱,4 = 等級(原生 populate 填的就是這兩個)。
    private const int NameNodeIndex = 3;
    private const int LevelNodeIndex = 4;

    // 每列的判定快取。用字串指標當 key,但每次命中都拿實際位元組再比對一次——
    // 列表重建時遊戲會重用同一塊緩衝區,光看指標會把別的副本的判定沿用下去。
    private readonly Dictionary<nint, (byte[] Raw, bool Mark)> rowMarkCache = [];

    // Renderers already written this frame - see the note in ReconcileDutyListMarks.
    private readonly HashSet<nint> handledRenderers = [];

    // 金星圖示的 SeString payload,標示時接在該列「原始位元組」前面,
    // 保留原名裡可能存在的其他 payload。
    private static readonly byte[] StarPrefix =
        new SeStringBuilder().AddIcon(BitmapFontIcon.GoldStar).Encode();

    public CollectableController() {
        dutyCollectables = LoadEmbeddedData();

        // ContentsFinderController already listens for PostSetup/PreFinalize/PostUpdate,
        // so there is no need for a second set of AddonLifecycle registrations.
        System.ContentsFinderController.OnAttach += AttachNodes;
        System.ContentsFinderController.OnDetach += DetachNodes;
        System.ContentsFinderController.OnUpdate += OnContentsFinderUpdate;
    }

    public void Dispose() {
        System.ContentsFinderController.OnAttach -= AttachNodes;
        System.ContentsFinderController.OnDetach -= DetachNodes;
        System.ContentsFinderController.OnUpdate -= OnContentsFinderUpdate;

        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
    }

    private static readonly FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor MarkColor
        = new Vector4(1.0f, 0.83f, 0.29f, 1.0f).ToByteColor();

    /// <summary>
    ///     Walks the duty list every frame and makes the gold-star marks match what the list
    ///     data says they should be.
    ///
    ///     This is a reconciler, not an event handler: it is idempotent, so running it every
    ///     frame is harmless, and it converges no matter how the list got into its current
    ///     state - first open, expanding a category, changing zone, or rows being recycled by
    ///     scrolling. That is why there is no populate hook and no forced re-populate.
    ///
    ///     Cost: the outer loop is one null check per item (a few hundred at most, and only
    ///     while the window is open). Everything expensive is behind `Renderer is null`, which
    ///     only the rows actually bound to a visible slot get past - roughly 15-25 of them.
    ///     Those do one dictionary lookup plus a byte compare, and only touch the UI when the
    ///     row is not already in the state it should be in.
    /// </summary>
    private void ReconcileDutyListMarks(AddonContentsFinder* addon) {
        if (dutyCollectables is null) return;

        var list = addon->DutyList;
        if (list is null) return;

        var marking = System.CollectableConfig is { Enabled: true, MarkDutyList: true };
        ref var items = ref list->Items;

        handledRenderers.Clear();

        for (var index = 0; index < items.Count; index++) {
            var item = items[index].Value;
            if (item is null) continue;

            // Null renderer means this row is not currently bound to a visual slot.
            var renderer = item->Renderer;
            if (renderer is null) continue;

            // One write per renderer per frame. Rows get recycled as the list scrolls, and if
            // an off-screen item were ever left holding a stale Renderer pointer, two items
            // would otherwise fight over the same row's text. Items are in list order, so the
            // first claimant is the row actually being shown.
            if (!handledRenderers.Add((nint) renderer)) continue;

            if (renderer->RowTemplateNodeCount <= LevelNodeIndex) continue;

            var nodeList = renderer->RowTemplateNodeList;
            if (nodeList is null) continue;

            var nameNode = (AtkTextNode*) nodeList[NameNodeIndex];
            var levelNode = (AtkTextNode*) nodeList[LevelNodeIndex];
            if (nameNode is null) continue;

            // StringValues[0] is the row's real name, straight from the list data - the text
            // node is only a rendering of it and may already carry our own star prefix.
            if (item->StringValues.Count is 0) continue;
            var rawName = item->StringValues[0];
            if (!rawName.HasValue) continue;

            ApplyRowMark(nameNode, levelNode, rawName, marking && ShouldMarkRow(rawName));
        }
    }

    private bool ShouldMarkRow(CStringPointer rawName) {
        var span = rawName.AsSpan();
        var key = (nint) rawName.Value;

        if (rowMarkCache.TryGetValue(key, out var cached) && span.SequenceEqual(cached.Raw)) {
            return cached.Mark;
        }

        // 原始位元組可能含 payload(鎖頭圖示等),Utf8String.ToString() 會把 payload
        // 混進字串害比對失敗——用 SeString 解析取純文字再比對。
        var dutyName = SeString.Parse(span).TextValue.Trim();
        nameToCfc ??= BuildNameMap();

        var mark = nameToCfc.TryGetValue(dutyName, out var cfcId) && HasMissing(cfcId);
        rowMarkCache[key] = (span.ToArray(), mark);
        return mark;
    }

    /// <summary>
    ///     Brings one row to the desired state. Strictly idempotent: a row that already has the
    ///     star is left alone (so stars never stack), and a row that should not have one is
    ///     rebuilt from the raw name.
    /// </summary>
    private static void ApplyRowMark(AtkTextNode* nameNode, AtkTextNode* levelNode, CStringPointer rawName, bool shouldMark) {
        var hasStar = nameNode->NodeText.AsSpan().StartsWith(StarPrefix);
        if (shouldMark == hasStar) {
            // Text is already right; only the colour can still be out of date.
            if (shouldMark && !SameColor(nameNode->TextColor, MarkColor)) nameNode->TextColor = MarkColor;
            return;
        }

        var raw = rawName.AsSpan();

        if (shouldMark) {
            // 金星 + 原始位元組 + null 終止符(原生 SetText 讀到 null 為止)。
            var buffer = new byte[StarPrefix.Length + raw.Length + 1];
            StarPrefix.CopyTo(buffer, 0);
            raw.CopyTo(buffer.AsSpan(StarPrefix.Length));
            buffer[^1] = 0;

            nameNode->SetText(buffer);
            nameNode->TextColor = MarkColor;
        }
        else {
            var buffer = new byte[raw.Length + 1];
            raw.CopyTo(buffer);
            buffer[^1] = 0;

            nameNode->SetText(buffer);
            if (levelNode is not null) nameNode->TextColor = levelNode->TextColor;
        }
    }

    private static bool SameColor(FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor left, FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor right)
        => left.R == right.R && left.G == right.G && left.B == right.B && left.A == right.A;

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
        rowMarkCache.Clear();
        hasLastSelection = false;
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
        // Unlock state is re-read whenever the window opens; learning a collectable while the
        // window is already open is an acceptable staleness window. Row decisions go with it,
        // since they are derived from it.
        missingCache.Clear();
        rowMarkCache.Clear();

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
        // Those string pointers belong to the addon we are about to lose.
        rowMarkCache.Clear();

        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
    }

    private void OnContentsFinderUpdate(AddonContentsFinder* addon) {
        // Runs before the summary-node early-outs: the row marks must keep converging even
        // when the bottom summary has nothing to say.
        ReconcileDutyListMarks(addon);

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
