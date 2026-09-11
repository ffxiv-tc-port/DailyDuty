using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiLib.Classes;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DailyDuty.Modules;

public class GrandCompanySquadronConfig : ModuleConfig;

public class GrandCompanySquadronData : ModuleData {
	public bool MissionCompleted;
	public bool MissionStarted;
	public DateTime MissionCompleteTime = DateTime.MinValue;
	public TimeSpan TimeUntilMissionComplete = TimeSpan.MinValue;

	protected override void DrawModuleData() {
		DrawDataTable(
			(Strings.MissionCompleted, MissionCompleted.ToString()),
			(Strings.MissionStarted, MissionStarted.ToString()),
			(Strings.MissionCompleteTime, MissionCompleteTime.ToLocalTime().ToString(CultureInfo.CurrentCulture)),
			(Strings.TimeUntilMissionComplete, TimeUntilMissionComplete.FormatTimespan())
		);
	}
}

public unsafe partial class GrandCompanySquadron : BaseModules.Modules.Weekly<GrandCompanySquadronData, GrandCompanySquadronConfig> {
	public override ModuleName ModuleName => ModuleName.GrandCompanySquadron;

	private Hook<AgentGcArmyExpedition.Delegates.ReceiveEvent>? onReceiveEventHook;

	[GeneratedRegex("[^\\p{L}\\p{N}]")]
	private static partial Regex Alphanumeric();

	public override void Load() {
		base.Load();

		Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "GcArmyExpeditionResult", GcArmyExpeditionResultPreFinalize);
                
		// AgentGcArmyExpedition.Instance() 走 CS 的 [Agent] 產生器(agentModule == null ? null : ...),
		// AgentModule 還沒配起來時是合法地回 null;裸接 ->VirtualTable 等於從位址 0 讀 vtable 指標,
		// 是 try/catch 與 HookSafety 都攔不到的 AccessViolation。同檔 Update() 已經是判空寫法。
		// 這裡跳過不是永久放棄:Load() 每次登入都會跑,而 hook 是 ??= 掛的,下次登入會自動重試。
		var gcAgent = AgentGcArmyExpedition.Instance();
		if (gcAgent is null || gcAgent->VirtualTable is null) {
			Service.Log.Information("[GrandCompanySquadron] AgentGcArmyExpedition 尚未就緒，本次跳過 ReceiveEvent hook 掛載；下次登入會自動重試。");
		}
		else {
			onReceiveEventHook ??= Service.Hooker.HookFromAddress<AgentGcArmyExpedition.Delegates.ReceiveEvent>(gcAgent->VirtualTable->ReceiveEvent, OnReceiveEvent);
		}

		onReceiveEventHook?.Enable();
	}

	// The mission is no longer in progress when the window closes
	private void GcArmyExpeditionResultPreFinalize(AddonEvent eventType, AddonArgs addonInfo) {
		var addon = (AtkUnitBase*) addonInfo.Addon.Address;
                
		Data.MissionStarted = false;
		DataChanged = true;

		if (addon->AtkValues[4].Type is not ValueType.String) throw new Exception("Type Mismatch Exception");
		if (addon->AtkValues[2].Type is not ValueType.Int) throw new Exception("Type Mismatch Exception");
                
		// 🔴 GetValueAsString() 對 ValueType.String 走的是 CStringPointer.ToString():
		//    把整段位元組當 UTF-8 直接解碼,完全不剝 SeString payload;任務名一旦帶連結／圖示 payload,
		//    解出來就會混進 U+FFFD 與控制位元組。而比對的另一端 GcArmyExpedition.Name.ToString()
		//    在本 pin 逐字就是 Lumina 的 ExtractText()(payload 已剝掉)⇒ 兩端基準不同。
		//    ⚠️ 同一行的 Alphanumeric() 正規式只濾掉非「字母或數字」,
		//    payload 位元組裡任何剛好是字母或數字的位元組都會留下來 ⇒ 它擋不住這件事。
		//    比不中的後果是靜默的:missionInfo 為 null → 週常「部隊小隊任務」永遠不會被標成已完成。
		//    改用 Dalamud 的 CStringPointer.ExtractText(),與另一端走同一支 Lumina 解析器。
		// ⚠️ 沒有 payload 的純文字任務名兩種讀法逐字相同,所以行為不變;風險面也沒變大
		//    (兩者都經過同一個 CStringPointer.AsSpan(),指標為 null 時回空 span、不解參)。
		var missionText = Alphanumeric().Replace(addon->AtkValues[4].String.ExtractText().ToLower(), string.Empty);
		var missionSuccessful = addon->AtkValues[2].Int == 1;

		var missionInfo = Service.DataManager.GetExcelSheet<GcArmyExpedition>()
			.FirstOrDefault(mission => Alphanumeric().Replace(mission.Name.ToString().ToLower(), string.Empty) == missionText);

		if (missionInfo is { GcArmyExpeditionType.RowId: 3 } && missionSuccessful) {
			Data.MissionCompleted = true;
			DataChanged = true;
		}
	}

	public override void Unload() {
		base.Unload();
                
		Service.AddonLifecycle.UnregisterListener(GcArmyExpeditionResultPreFinalize);
                
		onReceiveEventHook?.Disable();
	}

	public override void Dispose() {
		onReceiveEventHook?.Dispose();
	}

	public override void Reset() {
		Data.MissionCompleted = false;
                
		base.Reset();
	}


	public override void Update() {
		var gcAgent = AgentGcArmyExpedition.Instance();
		
		if (gcAgent is not null && gcAgent->IsAgentActive() && gcAgent->SelectedTab == 2) {
			// IsAgentActive() 只代表代理人本體活著，不保證 ExpeditionData 已配置：
			// 兩者生命週期不同步，裸讀會直接觸發無法攔截的 AccessViolation。
			// 每次重取、顯式判空、同幀即用；為 null 時安靜跳過本幀的讀取，下一幀再試。
			var expeditionData = gcAgent->ExpeditionData;
			if (expeditionData is not null) {
				Data.MissionCompleted = TryUpdateData(Data.MissionCompleted, expeditionData->MissionInfo[0].Available == 0);
			}
		}

		if (Data.MissionCompleteTime > DateTime.UtcNow) {
			Data.TimeUntilMissionComplete = Data.MissionCompleteTime - DateTime.UtcNow;
		}
		else {
			Data.TimeUntilMissionComplete = TimeSpan.Zero;
		}
                
		base.Update();
	}
            
	private AtkValue* OnReceiveEvent(AgentGcArmyExpedition* thisPtr, AtkValue* returnValue, AtkValue* args, uint argCount, ulong sender) {
		var result = onReceiveEventHook!.OriginalDisposeSafe(thisPtr, returnValue, args, argCount, sender);
                
		HookSafety.ExecuteSafe(() => {
			if (sender == 1 && args[0].Int == 0) {
				Data.MissionStarted = true;
				var missionCompleteDateTime = DateTime.UtcNow + TimeSpan.FromHours(18);
				Data.MissionCompleteTime = new DateTime(
					missionCompleteDateTime.Year,
					missionCompleteDateTime.Month,
					missionCompleteDateTime.Day,
					missionCompleteDateTime.Hour,
					missionCompleteDateTime.Minute,
					missionCompleteDateTime.Second,
					Data.NextReset.Millisecond,
					Data.NextReset.Microsecond
				);
				DataChanged = true;
			}
		}, Service.Log);

		return result;
	}

	protected override ModuleStatus GetModuleStatus() {
		if (Data.MissionStarted && Data.TimeUntilMissionComplete != TimeSpan.Zero) return ModuleStatus.InProgress;
                
		return Data.MissionCompleted ? ModuleStatus.Complete : ModuleStatus.Incomplete;
	}

	protected override StatusMessage GetStatusMessage() 
		=> Data.MissionStarted && Data.TimeUntilMissionComplete == TimeSpan.Zero ? Strings.MissionCompleted : Strings.MissionAvailable;
}