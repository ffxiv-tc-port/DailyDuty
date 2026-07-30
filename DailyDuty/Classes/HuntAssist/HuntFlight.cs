using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// Mount and flight helpers for the patrol.
///
/// Everything here is "best effort with a ground fallback": if flying is not available, not
/// unlocked, or simply does not work out, the patrol walks instead. Nothing blocks.
/// </summary>
public static class HuntFlight {
	/// <summary>GeneralAction 9 - Mount Roulette.</summary>
	private const uint MountRouletteAction = 9;

	/// <summary>GeneralAction 2 - Jump. Jumping while mounted in a flyable zone takes off.</summary>
	private const uint JumpAction = 2;

	/// <summary>
	/// TerritoryIntendedUse values that can permit flight (open world and its two variants).
	/// Same set AutoDuty uses; all 47 hunt zones are value 1.
	/// </summary>
	private static readonly uint[] FlyableIntendedUses = [1, 47, 49];

	/// <summary>
	/// Latches once IsAetherCurrentZoneComplete has been seen to throw. That call is a
	/// signature-resolved member function, so if it ever fails to resolve on a new patch we
	/// stop asking and keep the patrol on the ground rather than throwing every frame.
	/// </summary>
	private static bool aetherCurrentLookupBroken;

	public static bool IsMounted => Service.Condition[ConditionFlag.Mounted];

	public static bool IsFlying => Service.Condition[ConditionFlag.InFlight];

	private static bool IsCasting => Service.Condition[ConditionFlag.Casting];

	/// <summary>
	/// True when the current zone allows flight AND this character has unlocked it there.
	/// Both halves matter: the zone flag alone would try to fly in zones where the player has
	/// not finished the aether currents.
	/// </summary>
	public static unsafe bool IsFlyingAvailable() {
		try {
			var territoryId = Service.ClientState.TerritoryType;
			if (territoryId is 0) return false;

			if (!Service.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory)) return false;
			if (Array.IndexOf(FlyableIntendedUses, territory.TerritoryIntendedUse.RowId) < 0) return false;

			var compFlgSetId = territory.AetherCurrentCompFlgSet.RowId;
			if (compFlgSetId is 0) return false;
			if (aetherCurrentLookupBroken) return false;

			var playerState = PlayerState.Instance();
			if (playerState is null) return false;

			return playerState->IsAetherCurrentZoneComplete(compFlgSetId);
		}
		catch (Exception ex) {
			aetherCurrentLookupBroken = true;
			Service.Log.Warning(ex, "[HuntAssist] Could not read the flight unlock state, staying on the ground.");
			return false;
		}
	}

	/// <summary>
	/// Asks for a mount. Returns false when now is not the moment (in combat, already casting,
	/// action on cooldown, ...) - the caller should simply try again next tick until it gives
	/// up. Never forces anything.
	/// </summary>
	public static unsafe bool TryMount() {
		try {
			if (IsMounted) return true;
			if (IsCasting) return false;
			if (Service.Condition[ConditionFlag.InCombat]) return false;

			var actionManager = ActionManager.Instance();
			if (actionManager is null) return false;

			// Status 0 means "usable right now". Anything else (no mount, cannot mount here,
			// on cooldown) means we must not press it.
			if (actionManager->GetActionStatus(ActionType.GeneralAction, MountRouletteAction) is not 0) return false;

			return actionManager->UseAction(ActionType.GeneralAction, MountRouletteAction);
		}
		catch (Exception ex) {
			Service.Log.Warning(ex, "[HuntAssist] Mount request failed");
			return false;
		}
	}

	/// <summary>Takes off from a mounted-but-grounded state by jumping, the way the game does.</summary>
	public static unsafe bool TryTakeOff() {
		try {
			if (IsFlying) return true;
			if (!IsMounted) return false;
			if (IsCasting) return false;

			var actionManager = ActionManager.Instance();
			if (actionManager is null) return false;

			return actionManager->UseAction(ActionType.GeneralAction, JumpAction);
		}
		catch (Exception ex) {
			Service.Log.Warning(ex, "[HuntAssist] Take-off request failed");
			return false;
		}
	}
}
