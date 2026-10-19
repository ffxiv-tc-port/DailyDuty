using System;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using KamiLib.Classes;
using Lumina.Excel.Sheets;

namespace DailyDuty.Modules;

public class HuntMarksWeekly : HuntMarksBase {
	public override ModuleName ModuleName => ModuleName.HuntMarksWeekly;

	public override ModuleType ModuleType => ModuleType.Weekly;

	/// <summary>Only the elite bill names a single mark, so only it can offer the patrol.</summary>
	protected override bool SupportsTargetAssist => true;

	public override DateTime GetNextReset() => Time.NextWeeklyReset() + TimeSpan.FromMinutes(1);

	protected override void UpdateTaskLists() {
		var luminaUpdater = new LuminaTaskUpdater<MobHuntOrderType>(this, order => order.Type is 2);
		luminaUpdater.UpdateConfig(Config.TaskConfig);
		luminaUpdater.UpdateData(Data.TaskData);
	}

	protected override void DrawAssistSettings() {
		var config = System.HuntAssistConfig;
		var configChanged = false;

		ImGuiTweaks.Header(Strings.HuntAssistPatrolSettings);
		using var indent = ImRaii.PushIndent();

		configChanged |= ImGui.Checkbox(Strings.HuntAssistUseFlying, ref config.UseFlying);

		ImGui.TextUnformatted(Strings.HuntAssistDetectionRadius);
		ImGuiComponents.HelpMarker(Strings.HuntAssistDetectionRadiusHelp);

		var radius = config.DetectionRadius;
		ImGuiTweaks.SetFullWidth();
		if (ImGui.SliderFloat("##DetectionRadius", ref radius, HuntAssistConfig.MinimumDetectionRadius, HuntAssistConfig.MaximumDetectionRadius, "%.0f")) {
			config.DetectionRadius = radius;
		}

		// SliderFloat reports true every frame while dragging - only persist once it settles.
		if (ImGui.IsItemDeactivatedAfterEdit()) configChanged = true;

		if (configChanged) config.Save();
	}
}
