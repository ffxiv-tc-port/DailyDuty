using System;
using System.Diagnostics;
using System.Numerics;
using DailyDuty.Localization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Extensions;

namespace DailyDuty.Classes.HuntAssist;

public enum HuntAssistStep {
	Idle,
	Teleporting,
	AethernetHop,
	WaitingForAethernet,
	Walking,
	Finished,
	Stopped,
}

/// <summary>
/// Drives the "take me to the hunt board" shortcut.
///
/// Design rules this deliberately follows:
///  * user-triggered only - nothing here starts on its own,
///  * one click does one leg of the trip and then stops,
///  * interruptible at any moment (Cancel, combat, duty, logout, or the player simply walking
///    off - vnavmesh drops the path on user input when its own option is enabled),
///  * every external plugin is a soft dependency: Lifestream missing falls back to the game's
///    own teleport, vnavmesh missing falls back to a map flag.
///
/// It never attacks, never targets, and never touches memory it does not own.
/// </summary>
public sealed class HuntAssistController {
	private static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(90);
	private static readonly TimeSpan AethernetTimeout = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan NavmeshBuildTimeout = TimeSpan.FromSeconds(90);
	private static readonly TimeSpan WalkTimeout = TimeSpan.FromMinutes(5);

	/// <summary>Grace period after issuing a move before "vnavmesh is idle" means anything.</summary>
	private static readonly TimeSpan MoveStartGrace = TimeSpan.FromSeconds(3);

	/// <summary>How close counts as "at the board" - the board's interaction range is about 4.5y.</summary>
	private const float ArrivalRadius = 6.0f;

	/// <summary>Only hop the aethernet when it saves at least this much walking.</summary>
	private const float AethernetWorthwhileMargin = 30.0f;

	public NavmeshIpc Navmesh { get; } = new();

	public LifestreamIpc Lifestream { get; } = new();

	public HuntAssistStep Step { get; private set; } = HuntAssistStep.Idle;

	public string StatusText { get; private set; } = string.Empty;

	public bool IsRunning => Step is HuntAssistStep.Teleporting or HuntAssistStep.AethernetHop or HuntAssistStep.WaitingForAethernet or HuntAssistStep.Walking;

	/// <summary>The weekly bill row the running action belongs to, so the UI can highlight it.</summary>
	public uint ActiveOrderTypeRowId { get; private set; }

	private HuntBoardLocation? target;
	private readonly Stopwatch stepClock = new();
	private bool teleportIssued;
	private bool moveIssued;

	/// <summary>
	/// Starts the trip to a hunt board. Returns false (and says why via
	/// <see cref="StatusText"/>) when it could not start.
	/// </summary>
	public bool GoToHuntBoard(HuntBoardLocation board, uint orderTypeRowId) {
		if (IsRunning) {
			StatusText = Strings.HuntAssistAlreadyRunning;
			return false;
		}

		if (!Service.ClientState.IsLoggedIn || Service.ClientState.LocalPlayer is null) return false;

		if (Service.Condition.IsBoundByDuty()) {
			Fail(Strings.HuntAssistStoppedInDuty);
			return false;
		}

		target = board;
		ActiveOrderTypeRowId = orderTypeRowId;
		teleportIssued = false;
		moveIssued = false;
		Step = HuntAssistStep.Teleporting;
		StatusText = Strings.HuntAssistStatusTeleporting;
		stepClock.Restart();

		if (!Lifestream.IsInstalled) {
			PrintMessage(Strings.HuntAssistLifestreamMissing);
		}

		return true;
	}

	/// <summary>Stops everything this controller started. Safe to call at any time.</summary>
	public void Cancel() {
		if (!IsRunning) return;

		StopMovement();
		Step = HuntAssistStep.Stopped;
		StatusText = Strings.HuntAssistStatusCancelled;
		stepClock.Reset();
		target = null;
		ActiveOrderTypeRowId = 0;
	}

	/// <summary>Ticked from the framework update. Cheap no-op when nothing is running.</summary>
	public void Update() {
		if (!IsRunning) return;

		if (!Service.ClientState.IsLoggedIn) {
			Cancel();
			return;
		}

		if (Service.Condition.IsBoundByDuty()) {
			StopMovement();
			Fail(Strings.HuntAssistStoppedInDuty);
			return;
		}

		if (Service.ClientState.LocalPlayer is not { } player) return;
		if (target is not { } board) {
			Cancel();
			return;
		}

		switch (Step) {
			case HuntAssistStep.Teleporting:
				UpdateTeleport(board);
				break;

			case HuntAssistStep.AethernetHop:
				UpdateAethernetHop(board, player.Position);
				break;

			case HuntAssistStep.WaitingForAethernet:
				UpdateWaitingForAethernet();
				break;

			case HuntAssistStep.Walking:
				UpdateWalking(board, player.Position);
				break;
		}
	}

	private void UpdateTeleport(HuntBoardLocation board) {
		// Arrived (or we were already there to begin with). The teleport can legitimately land
		// in a neighbouring city zone - the Maelstrom board is in Limsa's Upper Decks but the
		// aetheryte is in the Lower Decks - so accept either territory here and let the
		// aethernet hop carry us the rest of the way.
		var territory = Service.ClientState.TerritoryType;
		if ((territory == board.TerritoryId || territory == board.PrimaryTerritoryId) && !Service.Condition[ConditionFlag.BetweenAreas]) {
			EnterStep(HuntAssistStep.AethernetHop, Strings.HuntAssistStatusAethernet);
			return;
		}

		if (!teleportIssued) {
			if (Service.Condition[ConditionFlag.InCombat]) {
				Fail(Strings.HuntAssistStoppedInCombat);
				return;
			}

			if (board.PrimaryAetheryteId is 0) {
				Fail(Strings.HuntAssistNoBoardData);
				return;
			}

			var started = Lifestream.IsInstalled
				? Lifestream.Teleport(board.PrimaryAetheryteId)
				: TeleportWithGameFunction(board.PrimaryAetheryteId);

			if (!started) {
				Fail(Strings.HuntAssistTeleportFailed);
				return;
			}

			teleportIssued = true;
			stepClock.Restart();
			return;
		}

		if (stepClock.Elapsed > TeleportTimeout) {
			Fail(Strings.HuntAssistStoppedTimeout);
		}
	}

	private void UpdateAethernetHop(HuntBoardLocation board, Vector3 playerPosition) {
		// Only Lifestream can drive the aethernet menu; without it we just walk further.
		if (!Lifestream.IsInstalled || board.NearestAetheryteId is 0) {
			EnterStep(HuntAssistStep.Walking, Strings.HuntAssistStatusWalking);
			return;
		}

		if (Lifestream.IsBusy) {
			if (stepClock.Elapsed > AethernetTimeout) {
				EnterStep(HuntAssistStep.Walking, Strings.HuntAssistStatusWalking);
			}

			return;
		}

		// The aethernet menu only opens in range of an aetheryte or shard. Asking for the
		// active one is a real state check, not a guess about where the teleport dropped us.
		var activeAetheryte = Lifestream.ActiveAetheryte;
		if (activeAetheryte is 0 || activeAetheryte == board.NearestAetheryteId) {
			EnterStep(HuntAssistStep.Walking, Strings.HuntAssistStatusWalking);
			return;
		}

		// Distances only compare inside one territory. When the board is in another city zone
		// the hop is the only way in, so take it unconditionally.
		if (Service.ClientState.TerritoryType == board.TerritoryId) {
			// Do not hop when we are already closer to the board than the shard is.
			var currentDistance = Vector2.Distance(
				new Vector2(playerPosition.X, playerPosition.Z),
				new Vector2(board.Position.X, board.Position.Z));

			if (currentDistance <= board.NearestAetheryteDistance + AethernetWorthwhileMargin) {
				EnterStep(HuntAssistStep.Walking, Strings.HuntAssistStatusWalking);
				return;
			}
		}

		if (!Lifestream.AethernetTeleport(board.NearestAetheryteId)) {
			EnterStep(HuntAssistStep.Walking, Strings.HuntAssistStatusWalking);
			return;
		}

		EnterStep(HuntAssistStep.WaitingForAethernet, Strings.HuntAssistStatusAethernet);
	}

	private void UpdateWaitingForAethernet() {
		if (Lifestream.IsBusy) {
			if (stepClock.Elapsed > AethernetTimeout) {
				Lifestream.Abort();
				EnterStep(HuntAssistStep.Walking, Strings.HuntAssistStatusWalking);
			}

			return;
		}

		// Give the hop a beat to actually begin before deciding it already finished.
		if (stepClock.Elapsed < MoveStartGrace) return;

		EnterStep(HuntAssistStep.Walking, Strings.HuntAssistStatusWalking);
	}

	private void UpdateWalking(HuntBoardLocation board, Vector3 playerPosition) {
		// A navmesh only covers one territory, and world coordinates are only comparable inside
		// one. If the aethernet hop did not (or could not) put us in the board's zone, say so
		// and flag it on the map rather than walking into a wall.
		if (Service.ClientState.TerritoryType != board.TerritoryId) {
			PlaceMapFlag(board);
			Finish(Strings.HuntAssistWrongZone);
			return;
		}

		var distance = Vector3.Distance(playerPosition, board.Position);

		if (distance <= ArrivalRadius) {
			StopMovement();
			Finish(Strings.HuntAssistStatusArrived);
			return;
		}

		if (!Navmesh.IsInstalled) {
			PlaceMapFlag(board);
			Finish(Strings.HuntAssistNavmeshMissing);
			return;
		}

		if (!moveIssued) {
			// The mesh for a freshly entered zone can still be building.
			if (!Navmesh.IsReady) {
				if (stepClock.Elapsed > NavmeshBuildTimeout) {
					PlaceMapFlag(board);
					Finish(Strings.HuntAssistNavmeshNotReady);
				}

				return;
			}

			// The Level sheet position sits on the board model, which may be slightly off-mesh.
			var destination = Navmesh.PointOnFloor(board.Position)
			                  ?? Navmesh.NearestPoint(board.Position, 10.0f, 10.0f)
			                  ?? board.Position;

			if (!Navmesh.MoveTo(destination)) {
				PlaceMapFlag(board);
				Finish(Strings.HuntAssistNavmeshNotReady);
				return;
			}

			moveIssued = true;
			stepClock.Restart();
			return;
		}

		if (Service.Condition[ConditionFlag.InCombat]) {
			StopMovement();
			Fail(Strings.HuntAssistStoppedInCombat);
			return;
		}

		if (stepClock.Elapsed > WalkTimeout) {
			StopMovement();
			Fail(Strings.HuntAssistStoppedTimeout);
			return;
		}

		// vnavmesh going idle means it either arrived or the player took the controls back
		// (its "cancel path on user input" option). Either way we stop rather than re-issue.
		if (stepClock.Elapsed < MoveStartGrace) return;
		if (Navmesh.PathfindInProgress || Navmesh.IsPathRunning) return;

		Finish(Strings.HuntAssistStatusStopped);
	}

	private void EnterStep(HuntAssistStep step, string status) {
		Step = step;
		StatusText = status;
		stepClock.Restart();
	}

	private void Finish(string status) {
		Step = HuntAssistStep.Finished;
		StatusText = status;
		stepClock.Reset();
		target = null;
		ActiveOrderTypeRowId = 0;
		PrintMessage(status);
	}

	private void Fail(string status) {
		Step = HuntAssistStep.Stopped;
		StatusText = status;
		stepClock.Reset();
		target = null;
		ActiveOrderTypeRowId = 0;
		PrintMessage(status);
	}

	private void StopMovement() {
		Navmesh.Stop();
		if (Lifestream.IsInstalled && Lifestream.IsBusy) Lifestream.Abort();
	}

	/// <summary>Fallback teleport for when Lifestream is not installed - the game's own Telepo.</summary>
	private static bool TeleportWithGameFunction(uint aetheryteId) {
		try {
			System.Teleporter.Teleport(aetheryteId);
			return true;
		}
		catch (Exception ex) {
			Service.Log.Error(ex, "[HuntAssist] Native teleport failed");
			return false;
		}
	}

	/// <summary>Degradation path when vnavmesh is unavailable: flag it on the map instead.</summary>
	private static unsafe void PlaceMapFlag(HuntBoardLocation board) {
		try {
			var agentMap = AgentMap.Instance();
			if (agentMap is null) return;

			agentMap->SetFlagMapMarker(board.TerritoryId, board.MapId, board.Position);
			agentMap->OpenMap(board.MapId, board.TerritoryId);
		}
		catch (Exception ex) {
			Service.Log.Error(ex, "[HuntAssist] Failed to place map flag");
		}
	}

	private static void PrintMessage(string message) {
		if (message.Length is 0) return;

		Service.Chat.Print(new XivChatEntry {
			Type = XivChatType.Debug,
			Message = new SeStringBuilder()
				.AddUiForeground("[DailyDuty] ", 45)
				.AddText(message)
				.Build(),
		});
	}
}
