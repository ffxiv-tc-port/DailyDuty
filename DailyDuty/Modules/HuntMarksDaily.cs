using System;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Modules.BaseModules;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace DailyDuty.Modules;

public class HuntMarksDaily : HuntMarksBase {
	public override ModuleName ModuleName => ModuleName.HuntMarksDaily;

	public override ModuleType ModuleType => ModuleType.Daily;

	public override DateTime GetNextReset() => Time.NextDailyReset() + TimeSpan.FromMinutes(1);

	protected override void UpdateTaskLists() {
		var luminaUpdater = new LuminaTaskUpdater<MobHuntOrderType>(this, order => order.Type is 1);
		luminaUpdater.UpdateConfig(Config.TaskConfig);
		luminaUpdater.UpdateData(Data.TaskData);
	}

	/// <summary>每日通緝令只有「自動接取」用得到；其餘討伐輔助設定在每週通緝令頁（同一份設定）。</summary>
	protected override void DrawAssistSettings() {
		var config = System.HuntAssistConfig;

		if (ImGui.Checkbox(Strings.HuntAutoAcceptToggle, ref config.AutoAcceptBill)) config.Save();
		if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.HuntAutoAcceptToggleHelp);
	}
}