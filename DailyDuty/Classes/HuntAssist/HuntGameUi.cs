using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// 自動接取通緝令與自動開打碰到的遊戲介面。
/// 契約：框架執行緒限定；回傳的 <see cref="IGameObject"/> 與視窗指標只在同一次呼叫的同一幀內使用，不跨幀保存，
/// 每幀照名字／編號重找。會叫的遊戲函式先判函式指標有沒有解析到，沒有就回「做不到」；
/// 單例位址解不出時 CS 擲 <see cref="InvalidOperationException"/>，一律接住當「做不到」。
/// </summary>
internal static unsafe class HuntGameUi {
	/// <summary>通緝令視窗的名稱（一個資料片一扇）；取自台服執行檔的字串表。</summary>
	private static readonly string[] BillWindowNames = ["Mobhunt", "Mobhunt2", "Mobhunt3", "Mobhunt4", "Mobhunt5", "Mobhunt6"];

	/// <summary>通緝令視窗裡「接受」鈕的節點（六扇視窗的 ULD 都是這個編號、128x28 的按鈕元件）。</summary>
	private const uint AcceptButtonNodeId = 21;

	/// <summary>找物件表裡離玩家最近、編號在 <paramref name="boardObjectIds"/> 內的告示板。</summary>
	public static IGameObject? FindBoard(IReadOnlyCollection<uint> boardObjectIds, Vector3 playerPosition, float maxDistance) {
		IGameObject? best = null;
		var bestDistance = maxDistance;

		foreach (var gameObject in Service.ObjectTable) {
			if (gameObject.ObjectKind is not ObjectKind.EventObj) continue;
			if (!Contains(boardObjectIds, gameObject.BaseId)) continue;

			var distance = Vector3.Distance(playerPosition, gameObject.Position);
			if (distance > bestDistance) continue;

			bestDistance = distance;
			best = gameObject;
		}

		return best;
	}

	private static bool Contains(IReadOnlyCollection<uint> ids, uint id) {
		foreach (var candidate in ids) {
			if (candidate == id) return true;
		}

		return false;
	}

	/// <returns>遊戲自己的互動距離判定；null＝問不到。</returns>
	public static bool? InInteractRange(IGameObject player, IGameObject target) {
		if (EventFramework.MemberFunctionPointers.CheckInteractRange is null) return null;

		try {
			var framework = EventFramework.Instance();
			if (framework is null) return null;

			return framework->CheckInteractRange((GameObjectStruct*) player.Address, (GameObjectStruct*) target.Address, 1, false);
		}
		catch (InvalidOperationException) {
			return null;
		}
	}

	/// <summary>選取並和告示板互動。<c>InteractWithObject</c> 的回傳值沒有語意，成敗看選單有沒有打開。</summary>
	public static bool Interact(IGameObject board) {
		if (TargetSystem.MemberFunctionPointers.InteractWithObject is null) return false;

		try {
			var targetSystem = TargetSystem.Instance();
			if (targetSystem is null) return false;

			Service.TargetManager.Target = board;
			targetSystem->InteractWithObject((GameObjectStruct*) board.Address, false);
			return true;
		}
		catch (InvalidOperationException) {
			return false;
		}
	}

	private static AtkUnitBase* GetReadyAddon(string name) {
		var addon = (AtkUnitBase*) Service.GameGui.GetAddonByName(name).Address;
		return addon is not null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded ? addon : null;
	}

	/// <summary>讀開著的 SelectString 的選項文字（去掉 payload）。沒開著回 false。</summary>
	public static bool TryReadSelectString(List<string> entries) {
		entries.Clear();
		var addon = GetReadyAddon("SelectString");
		if (addon is null) return false;

		var popup = &((AddonSelectString*) addon)->PopupMenu.PopupMenu;
		if (popup->EntryNames is null) return true;

		for (var index = 0; index < popup->EntryCount; index++) {
			var text = popup->EntryNames[index].Value;
			entries.Add(text is null ? string.Empty : MemoryHelper.ReadSeStringNullTerminated((nint) text).TextValue);
		}

		return true;
	}

	/// <summary>選 SelectString 的第 <paramref name="index"/> 項；視窗要在這一幀重新找到且可用才送。</summary>
	public static bool SelectStringOption(int index) {
		if (AtkUnitBase.MemberFunctionPointers.FireCallback is null) return false;

		var addon = GetReadyAddon("SelectString");
		if (addon is null) return false;

		var value = new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = index };
		addon->FireCallback(1, &value, true);
		return true;
	}

	public static bool IsSelectYesnoOpen() => GetReadyAddon("SelectYesno") is not null;

	/// <summary>開著的通緝令視窗；<paramref name="name"/> 是它的名稱。</summary>
	private static AtkUnitBase* FindBillWindow(out string name) {
		foreach (var candidate in BillWindowNames) {
			var addon = GetReadyAddon(candidate);
			if (addon is null) continue;

			name = candidate;
			return addon;
		}

		name = string.Empty;
		return null;
	}

	public static bool IsBillWindowOpen() => FindBillWindow(out _) is not null;

	/// <summary>
	/// 通緝令視窗裡的「接受」鈕：節點是按鈕元件、可按、文字與 <paramref name="expectedText"/> 逐字相同才算可用。
	/// <paramref name="actualText"/> 給 log（讀到什麼就寫什麼，找不到是空字串）。
	/// </summary>
	public static bool IsAcceptButtonUsable(string expectedText, out string windowName, out string actualText) {
		actualText = string.Empty;
		var addon = FindBillWindow(out windowName);
		if (addon is null) return false;

		var button = FindAcceptButton(addon);
		if (button is null) return false;

		// Utf8String.ToString() 不剝 payload，比對要用去掉 payload 的純文字。
		if (button->ButtonTextNode is not null && button->ButtonTextNode->NodeText.StringPtr.HasValue) actualText = MemoryHelper.ReadSeString(&button->ButtonTextNode->NodeText).TextValue;
		if (!button->IsEnabled) return false;

		return expectedText.Length > 0 && string.Equals(actualText.ReplaceLineEndings(string.Empty), expectedText.ReplaceLineEndings(string.Empty), StringComparison.Ordinal);
	}

	/// <summary>用按鈕自己登記的點擊事件按下「接受」（與滑鼠點擊走同一條路）；事件不屬於這扇視窗就不按。</summary>
	public static bool ClickAcceptButton() {
		var addon = FindBillWindow(out _);
		if (addon is null) return false;

		var button = FindAcceptButton(addon);
		if (button is null || !button->IsEnabled) return false;

		var ownerNode = button->AtkComponentBase.OwnerNode;
		if (ownerNode is null) return false;

		for (var atkEvent = ownerNode->AtkResNode.AtkEventManager.Event; atkEvent is not null; atkEvent = atkEvent->NextEvent) {
			if (atkEvent->State.EventType is not AtkEventType.ButtonClick) continue;
			if (atkEvent->Listener != (AtkEventListener*) addon) return false;

			addon->ReceiveEvent(atkEvent->State.EventType, (int) atkEvent->Param, atkEvent);
			return true;
		}

		return false;
	}

	/// <summary>照節點編號在視窗自己的節點表裡找，確認是按鈕元件才回；不叫遊戲的查找函式。</summary>
	private static AtkComponentButton* FindAcceptButton(AtkUnitBase* addon) {
		var uld = &addon->UldManager;
		if (uld->NodeList is null) return null;

		for (var index = 0; index < uld->NodeListCount; index++) {
			var node = uld->NodeList[index];
			if (node is null || node->NodeId != AcceptButtonNodeId) continue;
			if ((ushort) node->Type < 1000) return null;

			var component = ((AtkComponentNode*) node)->Component;
			if (component is null) return null;

			var info = (AtkUldComponentInfo*) component->UldManager.Objects;
			if (info is null || info->ComponentType is not ComponentType.Button) return null;

			return (AtkComponentButton*) component;
		}

		return null;
	}

	/// <returns>遊戲對自動攻擊（技能 7）的射程與視線判定；null＝問不到。</returns>
	public static bool? InAutoAttackRange(IGameObject player, IGameObject target) {
		if (ActionManager.MemberFunctionPointers.GetActionInRangeOrLoS is null) return null;

		return ActionManager.GetActionInRangeOrLoS(7, (GameObjectStruct*) player.Address, (GameObjectStruct*) target.Address) is 0;
	}

	/// <returns>null＝讀不到。</returns>
	public static bool? IsAutoAttacking() {
		try {
			var uiState = UIState.Instance();
			if (uiState is null) return null;

			return uiState->WeaponState.AutoAttackState.IsAutoAttacking;
		}
		catch (InvalidOperationException) {
			return null;
		}
	}

	/// <summary>一般動作 23（跳下）此刻遊戲允許使用；問不到當不允許。</summary>
	public static bool CanDismount() {
		if (ActionManager.MemberFunctionPointers.GetActionStatus is null) return false;

		try {
			var actionManager = ActionManager.Instance();
			return actionManager is not null && actionManager->GetActionStatus(ActionType.GeneralAction, 23) is 0;
		}
		catch (InvalidOperationException) {
			return false;
		}
	}

	/// <summary>送一般動作 23（跳下）。不用 9（隨機坐騎）：狀態旗標慢一拍時重送 9 會再騎上去。</summary>
	public static bool Dismount() {
		if (ActionManager.MemberFunctionPointers.UseAction is null) return false;

		try {
			var actionManager = ActionManager.Instance();
			return actionManager is not null && actionManager->UseAction(ActionType.GeneralAction, 23);
		}
		catch (InvalidOperationException) {
			return false;
		}
	}

	/// <summary>選取目標並開自動攻擊。一般動作 1 是切換，所以已經開著就不送。</summary>
	public static bool StartAutoAttack(IGameObject target) {
		if (ActionManager.MemberFunctionPointers.UseAction is null) return false;

		try {
			var actionManager = ActionManager.Instance();
			if (actionManager is null) return false;

			var attacking = IsAutoAttacking();
			if (attacking is null) return false;

			Service.TargetManager.Target = target;
			return attacking.Value || actionManager->UseAction(ActionType.GeneralAction, 1);
		}
		catch (InvalidOperationException) {
			return false;
		}
	}
}
