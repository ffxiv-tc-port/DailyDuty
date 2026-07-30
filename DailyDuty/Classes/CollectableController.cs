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

    // 任務列表逐行標示:同 DutyRoulette 的 populate hook 模式。
    private Hook<AtkComponentListItemPopulator.PopulateDelegate>? onDutyListPopulate;
    private readonly List<uint> markedIndexes = [];
    private readonly Dictionary<uint, bool> missingCache = [];
    private Dictionary<string, uint>? nameToCfc;
    private int debugDumpRemaining;

    // 金星圖示的 SeString payload,標示時接在該列「原始位元組」前面,
    // 保留原名裡可能存在的其他 payload。
    private static readonly byte[] StarPrefix =
        new SeStringBuilder().AddIcon(BitmapFontIcon.GoldStar).Encode();

    public CollectableController() {
        dutyCollectables = LoadEmbeddedData();

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", OnContentsFinderSetup);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnContentsFinderFinalize);

        System.ContentsFinderController.OnAttach += AttachNodes;
        System.ContentsFinderController.OnDetach += DetachNodes;
        System.ContentsFinderController.OnUpdate += OnContentsFinderUpdate;
    }

    public void Dispose() {
        Service.AddonLifecycle.UnregisterListener(OnContentsFinderSetup, OnContentsFinderFinalize);

        System.ContentsFinderController.OnAttach -= AttachNodes;
        System.ContentsFinderController.OnDetach -= DetachNodes;
        System.ContentsFinderController.OnUpdate -= OnContentsFinderUpdate;

        onDutyListPopulate?.Dispose();

        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
    }

    private void OnContentsFinderSetup(AddonEvent type, AddonArgs args) {
        if (dutyCollectables is null) return;

        // 解鎖狀態在開窗時重抓一次(同場學到新收藏品的過期程度可接受)。
        missingCache.Clear();
        debugDumpRemaining = 40;

        var addon = args.GetAddon<AddonContentsFinder>();
        var populateMethod = addon->DutyList->GetItemRendererByNodeId(6)->Populator.Populate;

        onDutyListPopulate = Service.Hooker.HookFromAddress<AtkComponentListItemPopulator.PopulateDelegate>(populateMethod, OnPopulateHook);
        onDutyListPopulate?.Enable();

        // 首開時列表在 setup 期間就 populate 完、早於本 hook 掛上——強制它用
        // 目前的陣列資料重跑一次 populate,金星標示第一次開窗就會套上,
        // 不用開關兩次。
        HookSafety.ExecuteSafe(() => {
            var stage = AtkStage.Instance();
            addon->AtkUnitBase.OnRequestedUpdate(stage->GetNumberArrayData(), stage->GetStringArrayData());
        }, Service.Log);
    }

    private void OnContentsFinderFinalize(AddonEvent type, AddonArgs args) {
        onDutyListPopulate?.Dispose();
        onDutyListPopulate = null;
        markedIndexes.Clear();
    }

    private void OnPopulateHook(AtkUnitBase* unitBase, AtkComponentListItemPopulator.ListItemInfo* listItemInfo, AtkResNode** nodeList) => HookSafety.ExecuteSafe(() => {
        var index = listItemInfo->ListItem->Renderer->OwnerNode->NodeId;
        var dutyNameTextNode = (AtkTextNode*) nodeList[3];
        var levelTextNode = (AtkTextNode*) nodeList[4];

        var shouldMark = false;
        var matched = false;
        var dutyName = string.Empty;
        byte[]? rawName = null;
        if (System.CollectableConfig is { Enabled: true, MarkDutyList: true }) {
            // 原始位元組可能含 payload(鎖頭圖示等),Utf8String.ToString() 會把
            // payload 混進字串害比對失敗——用 SeString 解析取純文字再比對。
            var rawSpan = listItemInfo->ListItem->StringValues[0].AsSpan();
            rawName = rawSpan.ToArray();
            dutyName = SeString.Parse(rawSpan).TextValue.Trim();
            nameToCfc ??= BuildNameMap();
            if (nameToCfc.TryGetValue(dutyName, out var cfcId)) {
                matched = true;
                shouldMark = HasMissing(cfcId);
            }
        }

        if (debugDumpRemaining > 0) {
            debugDumpRemaining--;
            var rawHex = rawName is null ? "" : Convert.ToHexString(rawName, 0, Math.Min(rawName.Length, 12));
            Service.Log.Debug($"[Collectable] row={index} name='{dutyName}' matched={matched} mark={shouldMark} raw12={rawHex}");
        }

        // 先讓原生 populate 填好整列(它每次都會重寫文字),再疊我們的標示——
        // 圖示前綴才不會在列被回收重用時累積或殘留。
        onDutyListPopulate!.Original(unitBase, listItemInfo, nodeList);

        if (shouldMark) {
            dutyNameTextNode->TextColor = MarkColor;

            // 金星 + 原始位元組 + null 終止符(原生 SetText 讀到 null 為止)。
            var buf = new byte[StarPrefix.Length + rawName!.Length + 1];
            StarPrefix.CopyTo(buf, 0);
            rawName.CopyTo(buf, StarPrefix.Length);
            dutyNameTextNode->SetText(buf);

            if (!markedIndexes.Contains(index)) {
                markedIndexes.Add(index);
            }
        }
        else if (markedIndexes.Contains(index)) {
            dutyNameTextNode->TextColor = levelTextNode->TextColor;
            markedIndexes.Remove(index);
        }
    }, Service.Log);

    private static FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor MarkColor
        => new Vector4(1.0f, 0.83f, 0.29f, 1.0f).ToByteColor();

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
