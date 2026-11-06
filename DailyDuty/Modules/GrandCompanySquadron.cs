using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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

		if (addon is null || addon->AtkValues is null) return;
		var values = addon->AtkValuesSpan;

		// 名稱比對的兩端都要剝 payload:AtkValue 端用 CStringPointer.ExtractText()(ToString() 不剝),表端整欄無巨集。
		// 先看上游的索引 4,比不中再掃其餘字串值:整串等於某個任務名稱才算,不做子字串比對以免串錯任務。
		var missions = Service.DataManager.GetExcelSheet<GcArmyExpedition>()
			.Select(row => (Key: NormalizeMissionName(row.Name.ExtractText()), Row: row))
			.Where(entry => entry.Key.Length > 0)
			.ToList();

		var nameIndex = FindMissionNameIndex(values, missions, out var missionInfo);
		if (missionInfo is not { } mission) {
			LogResultValues(values, "找不到任務名稱");
			return;
		}

		if (values.Length <= 2 || values[2].Type is not ValueType.Int) {
			LogResultValues(values, $"任務名稱在索引 {nameIndex}，但索引 2 不是成功旗標");
			return;
		}

		if (nameIndex != 4) {
			LogResultValues(values, $"任務名稱在索引 {nameIndex}，不在預期的索引 4");
		}

		if (mission.GcArmyExpeditionType.RowId is 3 && values[2].Int == 1) {
			Data.MissionCompleted = true;
			DataChanged = true;
		}
	}

	private static string NormalizeMissionName(string text)
		=> Alphanumeric().Replace(text.ToLowerInvariant(), string.Empty);

	private static int FindMissionNameIndex(Span<AtkValue> values, List<(string Key, GcArmyExpedition Row)> missions, out GcArmyExpedition? mission) {
		if (values.Length > 4 && TryMatchMission(values[4], missions, out mission)) return 4;

		for (var index = 0; index < values.Length; index++) {
			if (index != 4 && TryMatchMission(values[index], missions, out mission)) return index;
		}

		mission = null;
		return -1;
	}

	private static bool TryMatchMission(in AtkValue value, List<(string Key, GcArmyExpedition Row)> missions, out GcArmyExpedition? mission) {
		mission = null;
		if ((value.Type & ValueType.TypeMask) is not ValueType.String || !value.String.HasValue) return false;

		var text = NormalizeMissionName(value.String.ExtractText());
		if (text.Length == 0) return false;

		foreach (var entry in missions) {
			if (entry.Key != text) continue;
			mission = entry.Row;
			return true;
		}

		return false;
	}

	// 只在結算視窗關閉時呼叫(一次一行),讓使用者回報的 log 直接帶出台服這個視窗的實際索引與格式。
	private static void LogResultValues(Span<AtkValue> values, string reason) {
		var builder = new StringBuilder();
		for (var index = 0; index < values.Length; index++) {
			ref var value = ref values[index];
			builder.Append(" [").Append(index).Append("] ").Append(value.Type).Append('=');

			switch (value.Type & ValueType.TypeMask) {
				case ValueType.Bool: builder.Append(value.Byte); break;
				case ValueType.Int: builder.Append(value.Int); break;
				case ValueType.UInt: builder.Append(value.UInt); break;
				case ValueType.Int64: builder.Append(value.Int64); break;
				case ValueType.UInt64: builder.Append(value.UInt64); break;
				case ValueType.Float: builder.Append(value.Float.ToString(CultureInfo.InvariantCulture)); break;
				case ValueType.String when value.String.HasValue:
					var span = value.String.AsReadOnlySeStringSpan();
					var text = span.ExtractText();
					builder.Append('"').Append(text).Append('"');
					var macro = span.ToMacroString();
					if (macro != text) builder.Append(" macro=\"").Append(macro).Append('"');
					break;
				default: builder.Append('-'); break;
			}
		}

		Service.Log.Information($"[GrandCompanySquadron] 軍隊分隊任務結算視窗{reason}；AtkValues 共 {values.Length} 個:{builder}");
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