using System;
using System.Linq;
using DailyDuty.Classes;
using DailyDuty.Classes.HuntAssist;
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

	public override uint TravelDestinationObjectId
		=> IncompleteTaskCount is 0 ? 0 : HuntBoards.MaskedCarnivaleAttendantObjectId;

	public override void DrawData() {
		base.DrawData();

		if (TravelDestinationObjectId is 0) return;

		TravelAssist.Draw(HuntBoards.MaskedCarnivaleAttendantObjectId, Strings.TravelAssistGoToMaskedCarnivale, Strings.TravelAssistMaskedCarnivaleTooltip);
	}

	protected override void UpdateTaskLists() {
		var luminaTaskUpdater = new LuminaTaskUpdater<Addon>(this, 12447, 12448, 12449);
		luminaTaskUpdater.UpdateConfig(Config.TaskConfig);
		luminaTaskUpdater.UpdateData(Data.TaskData);
	}

	// AgentAozContentBriefing.Instance() 由 [Agent] 產生器產生，兩層都合法回 null（登入前／登出後是常態）。
	// 每次呼叫都重走一遍 Framework → UIModule → AgentModule → 代理人陣列，所以收成一個區域變數：
	// 守衛與使用點指的才是同一個指標（同一次 Update 內沒有讓出點，代理人指標不會變動）。
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

	protected override ModuleStatus GetModuleStatus() {
		if (!HasTrackedTasks()) return ModuleStatus.Unknown;

		return IncompleteTaskCount == 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;
	}

	protected override StatusMessage GetStatusMessage() => new LinkedStatusMessage {
		LinkEnabled = Config.ClickableLink,
		Message = HasTrackedTasks() ? $"{IncompleteTaskCount} {Strings.ChallengesRemaining}" : Strings.TasksNoneTracked,
		Payload = PayloadId.UldahTeleport,
	};
}