using System;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyDuty.Modules.BaseModules;

public abstract unsafe class GrandCompanySupplyProvisionBase : Modules.DailyTask<ModuleTaskData<ClassJob>, ModuleTaskConfig<ClassJob>, ClassJob> {
	private static AgentGrandCompanySupply* SupplyAgent => AgentGrandCompanySupply.Instance();

	public override DateTime GetNextReset() 
		=> Time.NextGrandCompanyReset();

	public override void Reset() {
		Data.TaskData.Reset();
        
		base.Reset();
	}

	public override void Update() {
		// 代理人本體活著不保證 ItemArray 已配置：兩者生命週期不同步，
		// 以 null 建 Span 再索引會從位址 0 讀取並觸發無法攔截的 AccessViolation。
		// ItemArray 為 null 時整批跳過本幀更新（沿用上一次的判定），下一幀再試。
		if (SupplyAgent is not null && SupplyAgent->IsAgentActive() && SupplyAgent->ItemArray is not null) {
			Data.TaskData.Update(ref DataChanged, rowId => {
				var itemSpan = new Span<GrandCompanyItem>(SupplyAgent->ItemArray, SupplyAgent->NumItems);
				var adjustedIndex = (int)(rowId - 8);
				var agentData = itemSpan[adjustedIndex];

				return !agentData.IsTurnInAvailable;
			});
		}
        
		base.Update();
	}

	protected override ModuleStatus GetModuleStatus()
		=> IncompleteTaskCount == 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;

	protected override StatusMessage GetStatusMessage() 
		=> $"{IncompleteTaskCount} {Strings.AllowancesRemaining}";
}