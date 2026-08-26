using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using KamiLib.Classes;

namespace DailyDuty.Modules;

public class JumboCactpotData : ModuleData {
	public List<int> Tickets = [];

	protected override void DrawModuleData() {
		ImGui.Text(Strings.ClaimedTickets);

		var validTickets = Tickets.Where(ticket => ticket is not 0).ToList();

		using var indent = ImRaii.PushIndent();
		if (validTickets.Count == 0) {
			ImGui.TextColored(KnownColor.Orange.Vector(), "No Tickets Claimed");
		}
		else {
			foreach (var ticket in validTickets) {
				ImGui.Text($"{ticket:0000}");
			}
		}
	}
}

public class JumboCactpotConfig : ModuleConfig {
	public bool ClickableLink = true;
	
	protected override void DrawModuleConfig() {
		ConfigChanged |= ImGui.Checkbox(Strings.ClickableLink, ref ClickableLink);
	}
}

public unsafe class JumboCactpot : BaseModules.Modules.Special<JumboCactpotData, JumboCactpotConfig>, IGoldSaucerMessageReceiver {
	public override ModuleName ModuleName => ModuleName.JumboCactpot;
	
	public override DateTime GetNextReset() {
		try {
			return Time.NextJumboCactpotReset();
		}
		catch (Time.DatacenterException) {
			return DateTime.UtcNow + TimeSpan.FromDays(1);
		}
	}

	public override bool HasClickableLink => Config.ClickableLink;
	
	public override PayloadId ClickableLinkPayloadId => PayloadId.GoldSaucerTeleport;

	private Hook<AgentInterface.Delegates.ReceiveEvent>? onReceiveEventHook;
	private int ticketData = -1;

	protected override ModuleStatus GetModuleStatus() 
		=> Data.Tickets.Count == 3 ? ModuleStatus.Complete : ModuleStatus.Incomplete;

	public override void Load() {
		base.Load();

		onReceiveEventHook ??= CreateReceiveEventHook();
		onReceiveEventHook?.Enable();
	}

	// 代理人模組在登入流程早期可能尚未建立，任一層取不到就放棄掛載 hook，
	// 讓本模組維持「未掛 hook」而不是裸解參考造成崩潰；下次 Load 會再試一次。
	private Hook<AgentInterface.Delegates.ReceiveEvent>? CreateReceiveEventHook() {
		var agentModule = AgentModule.Instance();
		if (agentModule is null) {
			Service.Log.Warning("[JumboCactpot] 取不到 AgentModule，本次不掛載 ReceiveEvent hook。");
			return null;
		}

		var agent = agentModule->GetAgentByInternalId(AgentId.LotteryWeekly);
		if (agent is null) {
			Service.Log.Warning("[JumboCactpot] 取不到 LotteryWeekly 代理人，本次不掛載 ReceiveEvent hook。");
			return null;
		}

		if (agent->VirtualTable is null) {
			Service.Log.Warning("[JumboCactpot] LotteryWeekly 代理人的虛擬表為空，本次不掛載 ReceiveEvent hook。");
			return null;
		}

		return Service.Hooker.HookFromAddress<AgentInterface.Delegates.ReceiveEvent>(agent->VirtualTable->ReceiveEvent, OnReceiveEvent);
	}

	public override void Unload() {
		base.Unload();
        
		onReceiveEventHook?.Disable();
	}

	public override void Dispose() {
		onReceiveEventHook?.Dispose();
	}

	public override void Reset() {
		Data.Tickets.Clear();
        
		base.Reset();
	}
	
	protected override StatusMessage GetStatusMessage() => new LinkedStatusMessage {
		LinkEnabled = Config.ClickableLink,
		Message = $"{3 - Data.Tickets.Count} {Strings.TicketsAvailable}",
		Payload = PayloadId.GoldSaucerTeleport,
	};
    
	public void GoldSaucerUpdate(GoldSaucerEventArgs data) {
		const int jumboCactpotBroker = 1010446;
		if (Service.TargetManager.Target?.BaseId != jumboCactpotBroker) return;
		Data.Tickets.Clear();

		for(var i = 0; i < 3; ++i) {
			var ticketValue = data.Data[i + 2];

			if (ticketValue != 10000) {
				Data.Tickets.Add(ticketValue);
				DataChanged = true;
			}
		}
	}
    
	private AtkValue* OnReceiveEvent(AgentInterface* agent, AtkValue* returnValue, AtkValue* args, uint argCount, ulong sender) {
		var result = onReceiveEventHook!.OriginalDisposeSafe(agent, returnValue, args, argCount, sender);
        
		HookSafety.ExecuteSafe(() => {
			var data = args->Int;

			switch (sender) {
				// Message is from JumboCactpot
				case 0 when data >= 0:
					ticketData = data;
					break;

				// Message is from SelectYesNo
				case 5:
					switch (data) {
						case -1:
						case 1:
							ticketData = -1;
							break;

						case 0 when ticketData >= 0:
							Data.Tickets.Add(ticketData);
							ticketData = -1;
							DataChanged = true;
							break;
					}
					break;
			}
		}, Service.Log);

		return result;
	}
}