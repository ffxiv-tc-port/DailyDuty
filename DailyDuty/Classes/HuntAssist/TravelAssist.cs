using System.Drawing;
using System.Numerics;
using DailyDuty.Localization;
using DailyDuty.Modules.BaseModules;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using KamiLib.Classes;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// The "take me there" block a module puts under its data: the current trip's status, then
/// either a cancel button or the button that starts this module's trip.
///
/// Movement only - nothing here targets the destination, talks to it, or opens a window.
/// </summary>
public static class TravelAssist {
	/// <summary>Draws the block. Safe to call every frame; does nothing until the button is pressed.</summary>
	/// <param name="destinationObjectId">Level Object id - an ENpcResident or an EObj row.</param>
	public static void Draw(uint destinationObjectId, string buttonText, string tooltipText) {
		var controller = System.HuntAssistController;

		ImGuiTweaks.Header(Strings.TravelAssistSectionTitle);
		using var indent = ImRaii.PushIndent();

		if (controller.StatusText.Length > 0) {
			var statusColor = controller.IsRunning ? KnownColor.Orange.Vector() : KnownColor.Gray.Vector();
			ImGui.TextColored(statusColor, controller.StatusText);
		}

		var buttonSize = new Vector2(ImGui.GetContentRegionAvail().X, 23.0f * ImGuiHelpers.GlobalScale);

		// One controller for the whole plugin - a running trip may belong to another module.
		if (controller.IsRunning) {
			if (ImGui.Button(Strings.HuntAssistCancel, buttonSize)) controller.Cancel();

			return;
		}

		if (HuntBoards.Resolve(destinationObjectId) is not { } destination) {
			ImGui.TextColored(KnownColor.Orange.Vector(), Strings.TravelAssistNoDestinationData);
			return;
		}

		if (ImGui.Button(buttonText, buttonSize)) {
			controller.GoToLocation(destination, Strings.TravelAssistStatusTeleporting);
		}

		if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltipText);
	}

	/// <summary>按下「帶我去」，給沒有按鈕的呼叫端（狀態 IPC）用。只能從遊戲主執行緒呼叫。</summary>
	/// <returns>false＝沒有出發；原因已印到聊天視窗，或寫在 <see cref="HuntAssistController.StatusText"/>。</returns>
	public static bool Start(uint destinationObjectId) {
		if (HuntBoards.Resolve(destinationObjectId) is not { } destination) {
			HuntAssistController.PrintMessage(Strings.TravelAssistNoDestinationData);
			return false;
		}

		return System.HuntAssistController.GoToLocation(destination, Strings.TravelAssistStatusTeleporting);
	}

	/// <summary>
	/// 聊天通知／待辦清單連結被點了：走這個模組的「帶我去」；此刻沒有可走的目的地才照舊只傳送。
	/// 只做這一趟，不接著做別的事。只能從遊戲主執行緒呼叫（連結與待辦清單的點擊都在主執行緒）。
	/// </summary>
	public static void StartFromLink(ModuleName moduleName, uint fallbackAetheryteId) {
		var controller = System.HuntAssistController;
		var module = FindActiveModule(moduleName);
		var destinationObjectId = module?.TravelDestinationObjectId ?? 0;
		var destinationKnown = destinationObjectId is not 0 && HuntBoards.Resolve(destinationObjectId) is not null;

		var choice = ChooseLinkAction(controller.IsRunning, destinationObjectId, destinationKnown);
		Service.Log.Information($"[TravelAssist] 連結 {moduleName}：{choice}（目的地 {destinationObjectId}）。");

		switch (choice) {
			case LinkAction.AlreadyRunning:
				HuntAssistController.PrintMessage(Strings.HuntAssistAlreadyRunning);
				return;

			case LinkAction.Travel:
				module!.StartTravel();
				return;

			case LinkAction.TeleportOnlyNoData:
				HuntAssistController.PrintMessage(Strings.TravelLinkTeleportOnly);
				System.Teleporter.Teleport(fallbackAetheryteId);
				return;

			default:
				System.Teleporter.Teleport(fallbackAetheryteId);
				return;
		}
	}

	public enum LinkAction {
		/// <summary>已經有一趟在跑：不傳送、不換目的地，免得跟那一趟搶。</summary>
		AlreadyRunning,
		Travel,
		/// <summary>模組沒載入或此刻沒有目的地（例如已經做完）：和改版前一樣只傳送。</summary>
		TeleportOnly,
		TeleportOnlyNoData,
	}

	internal static LinkAction ChooseLinkAction(bool tripRunning, uint destinationObjectId, bool destinationKnown) {
		if (tripRunning) return LinkAction.AlreadyRunning;
		if (destinationObjectId is 0) return LinkAction.TeleportOnly;
		return destinationKnown ? LinkAction.Travel : LinkAction.TeleportOnlyNoData;
	}

	private static Module? FindActiveModule(ModuleName moduleName) {
		foreach (var module in System.ModuleController.Modules) {
			if (module.ModuleName == moduleName) return module is { IsLoaded: true, IsEnabled: true } ? module : null;
		}

		return null;
	}
}
