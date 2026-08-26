using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using KamiLib.Classes;
using KamiLib.Window;

namespace DailyDuty.Windows;

/// <summary>
///     副本收藏品總表。
///
///     為什麼是獨立視窗而不是在原生任務列表上標記:在原生列表逐列畫金星試過三個版本都
///     畫不出來(還撞過一次 AccessViolation),使用者裁定放棄那條路。獨立視窗不碰原生節點,
///     而且能一次回答三個原生列表回答不了的問題——哪些副本還缺、缺什麼、已經收了什麼。
///
///     版面判準(使用者的規則):「隨時掃視」的放列上,「起疑才查」的放 tooltip / 展開。
///     → 副本名 + 進度 <c>已取得/總數</c> 放列上;每一件道具的名字要展開才看。
///     → **「不知道」本身要在列上看得見**:查不到解鎖狀態的件數在列上直接寫成 <c>?N</c>,
///       絕不畫成 0,也絕不併進「已取得/{總數}」的分子。為什麼不知道才放 tooltip。
/// </summary>
public class CollectableWindow : Window {
    private string searchFilter = string.Empty;
    private bool onlyIncomplete = true;

    public CollectableWindow() : base($"DailyDuty - {Strings.CollectableWindowTitle}", new Vector2(520.0f, 460.0f)) {
        AdditionalInfoTooltip = Strings.CollectableWindowHelp;
    }

    protected override void DrawContents() {
        var controller = System.CollectableController;

        if (!controller.HasData) {
            ImGuiTweaks.CenteredWarning(Strings.CollectableWindowNoData);
            return;
        }

        if (!Service.ClientState.IsLoggedIn) {
            ImGuiTweaks.CenteredWarning(Strings.CollectableWindowNotLoggedIn);
            return;
        }

        DrawFilters();

        var duties = controller.GetDutyCollectables();

        var visible = duties
            .Where(duty => !onlyIncomplete || duty.MissingCount > 0 || duty.UnknownCount > 0)
            .Where(duty => searchFilter.Length is 0 || duty.DutyName.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 「有幾個副本還沒收齊」是使用者最常掃的一個數字,放在列表上方而不是藏在別處。
        var incompleteCount = duties.Count(duty => duty.MissingCount > 0);

        // ⚠️ 清單的可見條件是 MissingCount > 0 **或** UnknownCount > 0,但上面這個計數只看前者。
        // 只有「無法判定」的副本因此會出現在清單裡、卻不被算進摘要行 —— 摘要行會在清單明明
        // 有東西時說「0 個副本還有沒取得的收藏品」,跟底部提示行原本那個毛病是同一個形狀:
        // 把「不知道」靜靜吃掉。這裡把它獨立數出來,讓「不知道」在摘要行上就看得見。
        var unknownOnlyCount = duties.Count(duty => duty is { MissingCount: 0, UnknownCount: > 0 });

        var summaryText = string.Format(Strings.CollectableWindowSummary, incompleteCount, duties.Count, controller.KnownDutyCount);

        if (unknownOnlyCount > 0) {
            summaryText += string.Format(Strings.CollectableWindowSummaryUnknown, unknownOnlyCount);
        }

        ImGui.TextColored(KnownColor.Gray.Vector(), summaryText);

        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(Strings.CollectableWindowSummaryTooltip);
        }

        DrawCacheStatus(controller);

        ImGui.Separator();

        using var child = ImRaii.Child("collectable_list", ImGui.GetContentRegionAvail());
        if (!child) return;

        if (visible.Count is 0) {
            ImGui.TextColored(KnownColor.Gray.Vector(), Strings.CollectableWindowNoMatch);
            return;
        }

        foreach (var duty in visible) {
            DrawDuty(duty);
        }
    }

    /// <summary>
    ///     重新整理按鈕 + 資料時間。
    ///
    ///     整份表算一次要逐副本查解鎖狀態,而收藏品極少變動,所以快取沒有時間到期 ——
    ///     這顆按鈕是使用者剛拿到東西時讓畫面跟上的唯一手段。
    ///     🔴 快取不會自己過期,所以**「這份資料多舊」必須看得見**:資料時間直接畫在
    ///        按鈕旁邊,不是藏進 tooltip。tooltip 藏的是「為什麼不自動重算」。
    /// </summary>
    private static void DrawCacheStatus(CollectableController controller) {
        if (ImGui.Button(Strings.CollectableWindowRefresh)) {
            controller.RefreshDutyCollectables();
        }

        if (ImGui.IsItemHovered()) {
            ImGui.SetTooltip(Strings.CollectableWindowRefreshTooltip);
        }

        if (controller.SummaryCacheTimestamp is { } stamp) {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(KnownColor.Gray.Vector(), string.Format(Strings.CollectableWindowDataTime, stamp.ToString("HH:mm")));
        }
    }

    private void DrawFilters() {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
        ImGui.InputTextWithHint("##collectable_search", Strings.CollectableWindowSearchHint, ref searchFilter, 128);

        ImGui.SameLine();
        ImGui.Checkbox(Strings.CollectableWindowOnlyIncomplete, ref onlyIncomplete);

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(Strings.CollectableWindowTypeFilterHelp);
    }

    private static void DrawDuty(DutyCollectableInfo duty) {
        using var id = ImRaii.PushId((int) duty.CfcId);

        var complete = duty is { MissingCount: 0, UnknownCount: 0 };
        var labelColor = duty.MissingCount > 0 ? KnownColor.Orange.Vector()
            : complete ? KnownColor.LimeGreen.Vector()
            : KnownColor.Gray.Vector();

        // 進度放在名字前面才對得齊,一整排掃下去看得出來哪些沒收齊。
        var label = $"{duty.AcquiredCount}/{duty.Total}";
        if (duty.UnknownCount > 0) label += $" ?{duty.UnknownCount}";

        var labelColorScope = ImRaii.PushColor(ImGuiCol.Text, labelColor);
        using var tree = ImRaii.TreeNode($"{label}  Lv.{duty.Level}  {duty.DutyName}");
        labelColorScope.Dispose();

        // 列上看得見「有幾件不知道」,但**為什麼**不知道放 tooltip。
        if (duty.UnknownCount > 0 && ImGui.IsItemHovered()) {
            ImGui.SetTooltip(Strings.CollectableWindowUnknownTooltip);
        }

        if (!tree) return;

        using var indent = ImRaii.PushIndent();

        foreach (var item in duty.Items) {
            var (stateLabel, stateColor) = item.State switch {
                CollectableState.Missing => (Strings.CollectableStateMissing, KnownColor.Orange.Vector()),
                CollectableState.Acquired => (Strings.CollectableStateAcquired, KnownColor.LimeGreen.Vector()),
                _ => (Strings.CollectableStateUnknown, KnownColor.Gray.Vector()),
            };

            ImGui.TextColored(stateColor, stateLabel);
            ImGui.SameLine();
            ImGui.TextColored(KnownColor.Gray.Vector(), CollectableController.GetTypeName(item.Type));
            ImGui.SameLine();
            ImGui.TextUnformatted(item.Name);
        }

        ImGuiHelpers.ScaledDummy(3.0f);
    }
}
