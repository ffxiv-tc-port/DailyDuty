using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using DailyDuty.Localization;
using DailyDuty.Models;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace DailyDuty.Classes;

/// <summary>
///     副本收藏品(坐騎、寵物、樂譜、幻卡……)資料的單一來源。
///
///     提供兩個顯示端:
///     ① 任務搜尋器底部的單行提示 + 停留 tooltip(原生 <see cref="TextNode" />)。
///     ② 獨立視窗 <c>CollectableWindow</c>,一次看完所有副本。
///
///     ────────────────────────────────────────────────────────────────────────────
///     🔴 v7.20.0.11 ~ v7.20.0.20 曾嘗試在**原生任務列表**逐列畫金星(hook 列樣板的
///        populate、並主動請遊戲重跑排版)。實機從頭到尾畫不出來,而且那條路一路上
///        踩過一次 AccessViolation 崩潰(v7.20.0.16)。使用者裁定放棄該作法,整段
///        已於本版移除:不再 hook 任何原生函式,也不再對 ContentsFinder 送任何
///        重整請求。要找回舊實作請看 git 歷史(commit c20413c6 之前)。
///
///     現在本檔對原生層只做兩件事:掛一個自己的 TextNode、讀 AgentContentsFinder
///     的「目前選取的副本」純量。**不 hook、不寫回原生節點、不跨幀保存原生指標。**
///     ────────────────────────────────────────────────────────────────────────────
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

    // 全副本總表的快取。**沒有時間到期**:整份表要逐副本查解鎖狀態,而收藏品是
    // 極少變動的東西,照時間反覆重算純粹是白燒。改成算過就留著,由這三件事失效:
    //   ① 使用者在總表視窗按「重新整理」 ② 收藏品類型設定變更 ③ 登入/登出(換角)。
    // ⚠️ 舊版這裡有一個 2 秒節流常數,只有本路徑用過;任務搜尋器底部的**單副本**
    //    路徑走的是 hasLastSelection 選取變更閘門,與時間無關,所以那個常數隨這次
    //    改動一起變成死碼、已刪除。不要以為底部提示曾經吃過它。
    private List<DutyCollectableInfo>? summaryCache;

    /// <summary>快取建立時刻(當地時間)。null = 還沒算過。顯示端要讓使用者看得見資料多舊。</summary>
    private DateTime? summaryCacheTime;

    public CollectableController() {
        dutyCollectables = LoadEmbeddedData();

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

    /// <summary>有沒有成功載入內嵌的副本收藏品資料。false = 整個功能沒有資料可用。</summary>
    public bool HasData => dutyCollectables is not null;

    /// <summary>內嵌資料涵蓋幾個副本。用來讓使用者知道「查無此副本」是資料沒收錄,不是這個副本沒東西掉。</summary>
    public int KnownDutyCount => dutyCollectables?.Count ?? 0;

    #region 對外查詢

    /// <summary>
    ///     取得所有已收錄副本的完整收藏品清單(含已取得的項目)。
    ///     ⚠️ 只能在遊戲主執行緒呼叫:內部會讀 <see cref="UIState" /> 的解鎖狀態。
    /// </summary>
    public IReadOnlyList<DutyCollectableInfo> GetDutyCollectables()
        => summaryCache ??= BuildStamped();

    /// <summary>
    ///     丟掉快取並當場重算。總表視窗的「重新整理」按鈕用的就是這個 ——
    ///     使用者剛拿到東西想立刻看到更新時,唯一的手段。
    ///     ⚠️ 與 <see cref="GetDutyCollectables" /> 一樣只能在遊戲主執行緒呼叫。
    /// </summary>
    public IReadOnlyList<DutyCollectableInfo> RefreshDutyCollectables() {
        summaryCache = BuildStamped();
        return summaryCache;
    }

    /// <summary>快取建立的時刻,null 代表這一輪還沒算過。顯示端拿它畫「資料時間」。</summary>
    public DateTime? SummaryCacheTimestamp => summaryCacheTime;

    private List<DutyCollectableInfo> BuildStamped() {
        var built = BuildAllDutyInfo();
        summaryCacheTime = DateTime.Now;
        return built;
    }

    /// <summary>
    ///     設定變更、以及登入/登出時呼叫:類型開關會影響清單內容與底部摘要,
    ///     而解鎖狀態是**逐角色**的 —— 不清掉的話 A 角的快取會拿去騙 B 角。
    /// </summary>
    public void InvalidateCache() {
        summaryCache = null;
        summaryCacheTime = null;
        hasLastSelection = false;
    }

    #endregion

    #region 資料組裝

    private List<DutyCollectableInfo> BuildAllDutyInfo() {
        var result = new List<DutyCollectableInfo>();
        if (dutyCollectables is null) return result;

        foreach (var cfcId in dutyCollectables.Keys) {
            if (BuildDutyInfo(cfcId) is { } info) {
                result.Add(info);
            }
        }

        // 等級 → CFC id。CFC id 大致就是實裝順序,同等級的副本因此仍然照資料片排。
        result.Sort((left, right) => left.Level != right.Level
            ? left.Level.CompareTo(right.Level)
            : left.CfcId.CompareTo(right.CfcId));

        return result;
    }

    /// <summary>
    ///     組出單一副本的**完整**清單。回 null 代表這個副本沒有(啟用類型中的)任何收藏品。
    /// </summary>
    private DutyCollectableInfo? BuildDutyInfo(uint cfcId) {
        if (dutyCollectables is null || !dutyCollectables.TryGetValue(cfcId, out var entries)) return null;

        var cfcRow = Service.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfcId);
        if (cfcRow is null) return null;

        var dutyName = cfcRow.Value.Name.ExtractText();
        if (string.IsNullOrEmpty(dutyName)) return null;

        var itemSheet = Service.DataManager.GetExcelSheet<Item>();
        var items = new List<CollectableItemInfo>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries) {
            if (!ShouldShowType(entry.Type)) continue;

            var itemRow = itemSheet.GetRowOrDefault(entry.ItemId);
            if (itemRow is null) continue;

            var name = itemRow.Value.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            // 同名項目只留一筆(掉落表可能從多張表收進同一件東西)。
            if (!seenNames.Add(name)) continue;

            items.Add(new CollectableItemInfo(entry.ItemId, entry.Type, name, GetState(entry)));
        }

        if (items.Count is 0) return null;

        // 未取得 → 無法判定 → 已取得;同狀態內照類型排。使用者最常找的是第一段。
        items.Sort((left, right) => {
            var leftOrder = StateSortOrder(left.State);
            var rightOrder = StateSortOrder(right.State);

            if (leftOrder != rightOrder) return leftOrder.CompareTo(rightOrder);
            if (left.Type != right.Type) return left.Type.CompareTo(right.Type);

            return string.Compare(left.Name, right.Name, StringComparison.CurrentCulture);
        });

        return new DutyCollectableInfo {
            CfcId = cfcId,
            DutyName = dutyName,
            Level = cfcRow.Value.ClassJobLevelRequired,
            Items = items,
        };
    }

    private static int StateSortOrder(CollectableState state) => state switch {
        CollectableState.Missing => 0,
        CollectableState.Unknown => 1,
        _ => 2,
    };

    /// <summary>
    ///     查不到資料時回 <see cref="CollectableState.Unknown" />,**不會**猜成已取得或未取得。
    ///     舊版把「查不到」當成已取得,結果是使用者永遠不會知道那一筆沒被算到。
    /// </summary>
    private static CollectableState GetState(CollectableEntry entry) {
        if (entry.Type is 4) {
            if (entry.RelatedItemIds.Length is 0) return CollectableState.Unknown;

            var acquired = true;

            foreach (var relatedId in entry.RelatedItemIds) {
                switch (GetItemState(relatedId)) {
                    case CollectableState.Unknown: return CollectableState.Unknown;
                    case CollectableState.Missing: acquired = false; break;
                }
            }

            return acquired ? CollectableState.Acquired : CollectableState.Missing;
        }

        return GetItemState(entry.ItemId);
    }

    private static CollectableState GetItemState(uint itemId) {
        var uiState = UIState.Instance();
        if (uiState is null) return CollectableState.Unknown;

        var itemRow = ExdModule.GetItemRowById(itemId);
        if (itemRow is null) return CollectableState.Unknown;

        return uiState->IsItemActionUnlocked(itemRow) == 1 ? CollectableState.Acquired : CollectableState.Missing;
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

            Service.Log.Information($"[Collectable] 已載入 {result.Count} 個副本的收藏品資料。");
            return result;
        }
        catch (Exception e) {
            Service.Log.Warning(e, "[CollectableController] Failed to load embedded duty collectable data, hints will be disabled.");
            return null;
        }
    }

    #endregion

    #region 任務搜尋器底部提示

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

        if (selectedDuty.ContentType != ContentsId.ContentsType.Regular) {
            infoTextNode.IsVisible = false;
            return;
        }

        RefreshDisplay(BuildDutyInfo(selectedDuty.Id));
    }

    /// <summary>
    ///     底部提示。有未取得時的那一行**與舊版逐字相同**(使用者明說這行做得好)。
    ///     新增的是「全部取得時不再整段消失」——改成告訴使用者這個副本會出哪些收藏品,
    ///     以及 tooltip 裡連已取得的項目一起列出來。
    /// </summary>
    private void RefreshDisplay(DutyCollectableInfo? info) {
        if (infoTextNode is null) return;

        if (info is null) {
            infoTextNode.IsVisible = false;
            return;
        }

        // 摘要用的類型計數只算「未取得」(舊行為);全部取得時改算全部,才有東西可講。
        var countedState = info.MissingCount > 0 ? CollectableState.Missing : (CollectableState?) null;

        var summary = new StringBuilder();
        summary.Append(info.MissingCount > 0 ? Strings.CollectableHintHeader : Strings.CollectableHintAllHeader);

        var first = true;
        foreach (var (type, count) in CountByType(info, countedState)) {
            if (!first) summary.Append('、');
            first = false;
            summary.Append(GetTypeName(type));
            summary.Append('×');
            summary.Append(count);
        }

        // 三態。MissingCount 是 0 有**兩種**情況,不能都說成「已全部取得」:
        //   ① 真的全部確認取得       → (已全部取得)
        //   ② 有項目查不到解鎖狀態   → (無法判定 N 項)
        // 舊碼只看 MissingCount,把整排「無法判定」印成「已全部取得」(實機截圖回報)。
        // 「不知道」必須在這一行上看得見——tooltip 藏的是「為什麼」,不是「有沒有問題」。
        // ⚠️ MissingCount > 0 的那一行完全不經過這裡,逐字維持舊行為。
        if (info.MissingCount is 0) {
            summary.Append(info.UnknownCount > 0
                ? string.Format(Strings.CollectableHintUnknownOnly, info.UnknownCount)
                : Strings.CollectableHintAllObtained);
        }

        summary.Append(' ');
        summary.Append(Strings.CollectableHintTooltip);

        infoTextNode.Text = summary.ToString();
        infoTextNode.Tooltip = BuildDetailText(info);
        infoTextNode.IsVisible = true;
    }

    /// <summary>類型 → 件數。<paramref name="state" /> 為 null 表示不分狀態全算。</summary>
    private static SortedDictionary<byte, int> CountByType(DutyCollectableInfo info, CollectableState? state) {
        var counts = new SortedDictionary<byte, int>();

        foreach (var item in info.Items) {
            if (state is { } wanted && item.State != wanted) continue;

            counts.TryGetValue(item.Type, out var current);
            counts[item.Type] = current + 1;
        }

        return counts;
    }

    /// <summary>
    ///     tooltip 明細:未取得的排前面,已取得的也列出來(使用者明確要求要看得到已收藏的),
    ///     查不到狀態的獨立成「無法判定」一段——不知道要看得見,不能混進另外兩段。
    /// </summary>
    private static string BuildDetailText(DutyCollectableInfo info) {
        var detail = new StringBuilder();

        AppendStateSection(detail, info, CollectableState.Missing, Strings.CollectableStateMissing);
        AppendStateSection(detail, info, CollectableState.Unknown, Strings.CollectableStateUnknown);
        AppendStateSection(detail, info, CollectableState.Acquired, Strings.CollectableStateAcquired);

        return detail.ToString();
    }

    private static void AppendStateSection(StringBuilder detail, DutyCollectableInfo info, CollectableState state, string stateLabel) {
        var byType = new SortedDictionary<byte, List<string>>();

        foreach (var item in info.Items) {
            if (item.State != state) continue;

            if (!byType.TryGetValue(item.Type, out var names)) {
                names = [];
                byType[item.Type] = names;
            }

            names.Add(item.Name);
        }

        foreach (var (type, names) in byType) {
            if (detail.Length != 0) detail.Append('\n');

            detail.Append(stateLabel);
            detail.Append(' ');
            detail.Append(GetTypeName(type));
            detail.Append('：');
            detail.Append(string.Join('、', names));
        }
    }

    #endregion

    public static bool ShouldShowType(byte type) => type switch {
        1 => System.CollectableConfig.ShowMounts,
        2 => System.CollectableConfig.ShowMinions,
        3 => System.CollectableConfig.ShowOrchestrionRolls,
        4 => System.CollectableConfig.ShowTimewornOrchestrionRolls,
        5 => System.CollectableConfig.ShowTripleTriadCards,
        6 => System.CollectableConfig.ShowChocoboBarding,
        7 => System.CollectableConfig.ShowOther,
        _ => false,
    };

    public static string GetTypeName(byte type) => type switch {
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
