using System;
using System.Linq;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyDuty.Modules;

public class MaskedCarnivaleConfig : ModuleTaskConfig<Addon> {
	public bool ClickableLink = true;
	
	protected override void DrawModuleConfig() {
		ConfigChanged |= ImGui.Checkbox(Strings.ClickableLink, ref ClickableLink);
		
		ImGuiHelpers.ScaledDummy(5.0f);
		
		base.DrawModuleConfig();
	}
}

public unsafe class MaskedCarnivale : BaseModules.Modules.WeeklyTask<ModuleTaskData<Addon>, MaskedCarnivaleConfig, Addon> {
	public override ModuleName ModuleName => ModuleName.MaskedCarnivale;

	public override bool HasClickableLink => Config.ClickableLink;
    
	public override PayloadId ClickableLinkPayloadId => PayloadId.UldahTeleport;
    
	public override bool HasTooltip => true;
    
	public override void Load() {
		base.Load();
        
		Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "AOZContentResult", AozContentResultPostSetup);
	}

	public override void Unload() {
		base.Unload();
        
		Service.AddonLifecycle.UnregisterListener(AozContentResultPostSetup);
	}

	protected override void UpdateTaskLists() {
		var luminaTaskUpdater = new LuminaTaskUpdater<Addon>(this, addon => addon.RowId is 12449 or 12448 or 12447);
		luminaTaskUpdater.UpdateConfig(Config.TaskConfig);
		luminaTaskUpdater.UpdateData(Data.TaskData);
	}

	// AgentAozContentBriefing.Instance() 由 [Agent(AgentId.AozContentBriefing)] 產生，展開後是
	// AgentModule.Instance() == null ? null : (AgentAozContentBriefing*)agentModule->GetAgentByInternalId(...)
	// ——兩層都合法回 null（登入前／登出後是常態）。
	// 原本這段對它呼叫了 2 + 3 次，每次都重走一遍 Framework → UIModule → AgentModule → 代理人陣列：
	// 判空的那次與實際解參考的那幾次是**不同的解析結果**，守衛涵蓋不到使用點（§61 假守衛第 4 形）。
	// 收成一個區域變數後，守衛與使用點指的是同一個指標；行為不變（中間沒有任何讓出點，
	// 代理人指標在同一次 Update 內不會變動），且每輪少走 4 次三層鏈。
	public override void Update() {
		var agent = AgentAozContentBriefing.Instance();
		if (agent is not null && agent->IsAgentActive()) {
			foreach (var task in Data.TaskData) {
				var status = task.RowId switch {
					12449 => agent->IsWeeklyChallengeComplete(AozWeeklyChallenge.Novice),
					12448 => agent->IsWeeklyChallengeComplete(AozWeeklyChallenge.Moderate),
					12447 => agent->IsWeeklyChallengeComplete(AozWeeklyChallenge.Advanced),
					_ => throw new ArgumentOutOfRangeException(),
				};

				if (task.Complete != status) {
					task.Complete = status;
					DataChanged = true;
				}
			}
		}
        
		base.Update();
	}

	private void AozContentResultPostSetup(AddonEvent eventType, AddonArgs addonInfo) {
		var addon = (AtkUnitBase*) addonInfo.Addon.Address;
        
		if (addon->AtkValues[112] is not { Type: ValueType.UInt, UInt: var completionIndex }) throw new Exception("Type Mismatch Exception");
		if (addon->AtkValues[114] is not { Type: ValueType.Bool, Byte: var completionStatus }) throw new Exception("Type Mismatch Exception");
        
		var addonId = completionIndex switch {
			0 => 12449,
			1 => 12448,
			2 => 12447,

			_ => throw new ArgumentOutOfRangeException(),
		};

		var task = Data.TaskData.FirstOrDefault(task => task.RowId == addonId);

		if (task is not null && task.Complete != (completionStatus != 0)) {
			task.Complete = (completionStatus != 0);
			DataChanged = true;
		}
	}

	public override void Reset() {
		foreach (var task in Data.TaskData) {
			task.Complete = false;
		}
        
		base.Reset();
	}

	protected override ModuleStatus GetModuleStatus() 
		=> IncompleteTaskCount == 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;

	protected override StatusMessage GetStatusMessage() => new LinkedStatusMessage {
		LinkEnabled = Config.ClickableLink,
		Message = $"{IncompleteTaskCount} {Strings.ChallengesRemaining}",
		Payload = PayloadId.UldahTeleport,
	};
}