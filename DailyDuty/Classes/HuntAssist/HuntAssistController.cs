using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using DailyDuty.Localization;
using DailyDuty.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiLib.Extensions;

namespace DailyDuty.Classes.HuntAssist;

public enum HuntAssistStep {
	Idle,
	Teleporting,
	TeleportingToZone,
	AethernetHop,
	WaitingForAethernet,
	Walking,
	PreparingPatrol,
	MountingForPatrol,
	Patrolling,
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

	/// <summary>How long to wait for the spawn point data to become usable.</summary>
	private static readonly TimeSpan SpawnDataTimeout = TimeSpan.FromSeconds(10);

	/// <summary>How long to keep trying to mount before giving up and walking.</summary>
	private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(12);

	/// <summary>Gap between mount/take-off attempts, so we do not spam the action every frame.</summary>
	private static readonly TimeSpan MountRetryInterval = TimeSpan.FromSeconds(1.5);

	private static readonly TimeSpan WaypointTimeout = TimeSpan.FromSeconds(120);

	/// <summary>Below this much travel, an idle navmesh means "no path", not "user took over".</summary>
	private const float UnreachableMoveThreshold = 3.0f;

	public NavmeshIpc Navmesh { get; } = new();

	public LifestreamIpc Lifestream { get; } = new();

	public HuntAssistStep Step { get; private set; } = HuntAssistStep.Idle;

	public string StatusText { get; private set; } = string.Empty;

	public bool IsRunning => Step is HuntAssistStep.Teleporting or HuntAssistStep.TeleportingToZone or HuntAssistStep.AethernetHop or HuntAssistStep.WaitingForAethernet or HuntAssistStep.Walking or HuntAssistStep.PreparingPatrol or HuntAssistStep.MountingForPatrol or HuntAssistStep.Patrolling;

	/// <summary>The weekly bill row the running action belongs to, so the UI can highlight it.</summary>
	public uint ActiveOrderTypeRowId { get; private set; }

	private HuntBoardLocation? target;
	private HuntTargetInfo? zoneTarget;
	private HuntTargetInfo? patrolTarget;
	private readonly List<Vector3> patrolRemaining = [];
	private int patrolTotal;
	private Vector3? patrolWaypoint;
	private bool patrolFlying;
	private readonly Stopwatch mountClock = new();
	private readonly Stopwatch mountRetryClock = new();
	private Vector3 moveStartPosition;
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

	/// <summary>
	/// Teleports to the zone the currently-held bill's mark lives in. The aetheryte is not
	/// simply the nearest one - <see cref="HuntTargets"/> picks whichever makes the following
	/// patrol shortest, so this step already sets up the next one.
	/// </summary>
	public bool GoToTargetZone(HuntTargetInfo targetInfo, uint orderTypeRowId) {
		if (IsRunning) {
			StatusText = Strings.HuntAssistAlreadyRunning;
			return false;
		}

		if (!Service.ClientState.IsLoggedIn || Service.ClientState.LocalPlayer is null) return false;

		if (Service.Condition.IsBoundByDuty()) {
			Fail(Strings.HuntAssistStoppedInDuty);
			return false;
		}

		if (targetInfo.AetheryteId is 0) {
			// Nothing to teleport to (some zones have no aetheryte at all) - flag it instead.
			PlaceMapFlag(targetInfo.TerritoryId, targetInfo.MapId, Vector3.Zero, false);
			StatusText = Strings.HuntAssistNoAetheryte;
			PrintMessage(StatusText);
			return false;
		}

		if (Service.ClientState.TerritoryType == targetInfo.TerritoryId) {
			StatusText = Strings.HuntAssistAlreadyInZone;
			PrintMessage(StatusText);
			return false;
		}

		if (Service.Condition[ConditionFlag.InCombat]) {
			Fail(Strings.HuntAssistStoppedInCombat);
			return false;
		}

		var started = Lifestream.IsInstalled
			? Lifestream.Teleport(targetInfo.AetheryteId)
			: TeleportWithGameFunction(targetInfo.AetheryteId);

		if (!started) {
			Fail(Strings.HuntAssistTeleportFailed);
			return false;
		}

		zoneTarget = targetInfo;
		target = null;
		ActiveOrderTypeRowId = orderTypeRowId;
		teleportIssued = true;
		moveIssued = false;
		Step = HuntAssistStep.TeleportingToZone;
		StatusText = Strings.HuntAssistStatusTeleportingZone;
		stepClock.Restart();
		return true;
	}

	/// <summary>
	/// Walks the zone's known spawn points for the mark's rank, in a short greedy route from
	/// wherever the player is standing, and stops as soon as the mark shows up.
	///
	/// It never attacks and never targets: finding the mark ends the run and hands control
	/// back. Any manual movement also ends it - clicking patrol again re-plans from the new
	/// position.
	/// </summary>
	public bool StartPatrol(HuntTargetInfo targetInfo, uint orderTypeRowId) {
		if (IsRunning) {
			StatusText = Strings.HuntAssistAlreadyRunning;
			return false;
		}

		if (!Service.ClientState.IsLoggedIn || Service.ClientState.LocalPlayer is not { } player) return false;

		if (Service.Condition.IsBoundByDuty()) {
			Fail(Strings.HuntAssistStoppedInDuty);
			return false;
		}

		if (Service.ClientState.TerritoryType != targetInfo.TerritoryId) {
			StatusText = Strings.HuntAssistPatrolWrongZone;
			PrintMessage(StatusText);
			return false;
		}

		if (!Navmesh.IsInstalled) {
			StatusText = Strings.HuntAssistPatrolNeedsNavmesh;
			PrintMessage(StatusText);
			return false;
		}

		// The point list is deliberately NOT resolved here. Building it needs the zone's Map
		// row, which can still be settling right after a zone change, and nothing about "not
		// ready yet" should look like "there is nothing to patrol". Prepare, with a timeout.
		patrolRemaining.Clear();
		patrolTotal = 0;
		patrolWaypoint = null;
		patrolTarget = targetInfo;
		target = null;
		zoneTarget = null;
		ActiveOrderTypeRowId = orderTypeRowId;
		moveIssued = false;
		patrolFlying = false;

		Step = HuntAssistStep.PreparingPatrol;
		StatusText = Strings.HuntAssistStatusPreparing;
		stepClock.Restart();
		return true;
	}

	/// <summary>
	/// Waits until the spawn points can actually be placed, then starts the run.
	///
	/// The distinction this step exists to protect: zero points is never "finished". Either the
	/// data is not ready (retry, then say so), or the zone genuinely has none (say that
	/// instead) - neither is a completed patrol.
	/// </summary>
	private void UpdatePreparingPatrol() {
		if (patrolTarget is not { } targetInfo) {
			Cancel();
			return;
		}

		if (!HuntSpawnPoints.TryGetSpawnPoints(targetInfo.TerritoryId, targetInfo.Rank, out var spawnPoints)) {
			if (stepClock.Elapsed > SpawnDataTimeout) Fail(Strings.HuntAssistSpawnDataNotReady);
			return;
		}

		if (spawnPoints.Count is 0) {
			Fail(Strings.HuntAssistNoSpawnData);
			return;
		}

		patrolRemaining.Clear();
		patrolRemaining.AddRange(spawnPoints);
		patrolTotal = patrolRemaining.Count;

		patrolFlying = System.HuntAssistConfig.UseFlying && HuntFlight.IsFlyingAvailable();

		// Coverage is only counted once we are actually under way - never during the mount and
		// take-off, and never before the points exist.
		if (patrolFlying && !HuntFlight.IsFlying) {
			Step = HuntAssistStep.MountingForPatrol;
			StatusText = Strings.HuntAssistStatusMounting;
			mountClock.Restart();
			mountRetryClock.Reset();
			stepClock.Restart();
			return;
		}

		BeginPatrolStep();
	}

	private string PatrolStatusText
		=> $"{Strings.HuntAssistStatusPatrolling} ({patrolTotal - patrolRemaining.Count}/{patrolTotal})";

	/// <summary>
	/// Marks every remaining spawn point the player can currently see as checked.
	///
	/// This is what makes the patrol "covering" rather than "visiting": one pass can sweep up
	/// several points at once, and a point never has to be flown to exactly - being within
	/// detection range of it is the whole test.
	/// </summary>
	private void ConsumeCoveredPoints(Vector3 playerPosition) {
		var radius = Math.Clamp(
			System.HuntAssistConfig.DetectionRadius,
			HuntAssistConfig.MinimumDetectionRadius,
			HuntAssistConfig.MaximumDetectionRadius);

		var radiusSquared = radius * radius;
		var playerXZ = new Vector2(playerPosition.X, playerPosition.Z);

		for (var index = patrolRemaining.Count - 1; index >= 0; index--) {
			// Horizontal distance only. Spawn points carry no height at all (their Y is 0, not
			// the terrain height), so a 3D comparison would measure against a coordinate that
			// does not exist and never match. The conservative default radius is what absorbs
			// the altitude gained while flying.
			var point = patrolRemaining[index];
			if (Vector2.DistanceSquared(playerXZ, new Vector2(point.X, point.Z)) > radiusSquared) continue;

			patrolRemaining.RemoveAt(index);
		}

		if (patrolWaypoint is { } waypoint && !patrolRemaining.Contains(waypoint)) {
			patrolWaypoint = null;
			moveIssued = false;
		}
	}

	/// <summary>Nearest remaining point to the player. Null when everything is covered.</summary>
	private Vector3? NextWaypoint(Vector3 playerPosition) {
		Vector3? best = null;
		var bestDistance = float.MaxValue;
		var playerXZ = new Vector2(playerPosition.X, playerPosition.Z);

		foreach (var point in patrolRemaining) {
			// Horizontal only, for the same reason as ConsumeCoveredPoints.
			var distance = Vector2.DistanceSquared(playerXZ, new Vector2(point.X, point.Z));
			if (distance >= bestDistance) continue;

			bestDistance = distance;
			best = point;
		}

		return best;
	}

	/// <summary>Stops everything this controller started. Safe to call at any time.</summary>
	public void Cancel() {
		if (!IsRunning) return;

		StopMovement();
		Step = HuntAssistStep.Stopped;
		StatusText = Strings.HuntAssistStatusCancelled;
		stepClock.Reset();
		target = null;
		zoneTarget = null;
		patrolTarget = null;
		patrolRemaining.Clear();
		patrolTotal = 0;
		patrolWaypoint = null;
		patrolFlying = false;
		mountClock.Reset();
		mountRetryClock.Reset();
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

		if (Step is HuntAssistStep.TeleportingToZone) {
			UpdateTeleportToZone();
			return;
		}

		if (Step is HuntAssistStep.PreparingPatrol) {
			UpdatePreparingPatrol();
			return;
		}

		if (Step is HuntAssistStep.MountingForPatrol) {
			UpdateMountingForPatrol();
			return;
		}

		if (Step is HuntAssistStep.Patrolling) {
			if (Service.ClientState.LocalPlayer is { } patrolPlayer) UpdatePatrol(patrolPlayer.Position);
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

	private void UpdateTeleportToZone() {
		if (zoneTarget is not { } targetInfo) {
			Cancel();
			return;
		}

		if (Service.ClientState.TerritoryType == targetInfo.TerritoryId && !Service.Condition[ConditionFlag.BetweenAreas]) {
			Finish(Strings.HuntAssistStatusArrivedZone);
			return;
		}

		if (stepClock.Elapsed > TeleportTimeout) {
			Fail(Strings.HuntAssistStoppedTimeout);
		}
	}

	/// <summary>
	/// Gets the character airborne before the patrol starts. Entirely optional - every exit
	/// from here continues the patrol, on foot if need be.
	/// </summary>
	private void UpdateMountingForPatrol() {
		if (patrolTarget is null) {
			Cancel();
			return;
		}

		if (HuntFlight.IsFlying) {
			BeginPatrolStep();
			return;
		}

		// Never fight the game for a mount: combat, a broken lookup or simply running out of
		// patience all mean "walk instead".
		if (Service.Condition[ConditionFlag.InCombat] || mountClock.Elapsed > MountTimeout) {
			patrolFlying = false;
			BeginPatrolStep();
			return;
		}

		if (mountRetryClock.IsRunning && mountRetryClock.Elapsed < MountRetryInterval) return;

		if (HuntFlight.IsMounted) {
			HuntFlight.TryTakeOff();
		}
		else {
			HuntFlight.TryMount();
		}

		mountRetryClock.Restart();
	}

	private void BeginPatrolStep() {
		Step = HuntAssistStep.Patrolling;
		StatusText = PatrolStatusText;
		moveIssued = false;
		patrolWaypoint = null;
		stepClock.Restart();
	}

	private void UpdatePatrol(Vector3 playerPosition) {
		if (patrolTarget is not { } targetInfo) {
			Cancel();
			return;
		}

		if (Service.ClientState.TerritoryType != targetInfo.TerritoryId) {
			StopMovement();
			Fail(Strings.HuntAssistPatrolWrongZone);
			return;
		}

		// Finding the mark is the whole point - stop the moment it is in range. We only read
		// the object table; nothing is targeted and nothing is attacked.
		if (FindTargetObject(targetInfo) is { } found) {
			StopMovement();
			PlaceMapFlag(targetInfo.TerritoryId, targetInfo.MapId, found.Position, true);
			Finish($"{Strings.HuntAssistTargetFound} {targetInfo.Name}");
			return;
		}

		if (Service.Condition[ConditionFlag.InCombat]) {
			StopMovement();
			Fail(Strings.HuntAssistStoppedInCombat);
			return;
		}

		// Sweep up everything now within detection range, including points we were not even
		// heading for. This is what lets one flight leg clear several spawn points.
		var remainingBefore = patrolRemaining.Count;
		ConsumeCoveredPoints(playerPosition);
		if (patrolRemaining.Count != remainingBefore) StatusText = PatrolStatusText;

		if (patrolRemaining.Count is 0) {
			StopMovement();

			// Guard the distinction one last time: we only "finished" if there was something
			// to finish. Zero out of zero is a data problem, not a completed patrol.
			Finish(patrolTotal > 0 ? Strings.HuntAssistPatrolComplete : Strings.HuntAssistSpawnDataNotReady);
			return;
		}

		if (!moveIssued) {
			// Between legs too, not just at the start: if we should be flying but have landed
			// (a forced dismount, a low ceiling, the player hopping off), get airborne again
			// before the next hop. Giving up in there clears patrolFlying, so this cannot loop.
			if (patrolFlying && !HuntFlight.IsFlying) {
				Step = HuntAssistStep.MountingForPatrol;
				StatusText = Strings.HuntAssistStatusMounting;
				mountClock.Restart();
				mountRetryClock.Reset();
				return;
			}

			if (NextWaypoint(playerPosition) is not { } waypoint) {
				StopMovement();
				Finish(patrolTotal > 0 ? Strings.HuntAssistPatrolComplete : Strings.HuntAssistSpawnDataNotReady);
				return;
			}

			// Spawn points carry no height, so seed the query with the player's own Y - they
			// are standing in this zone, which is a far better guess than 0.
			var probe = new Vector3(waypoint.X, playerPosition.Y, waypoint.Z);
			var destination = Navmesh.NearestPoint(probe, 20.0f, 500.0f)
			                  ?? Navmesh.PointOnFloor(probe, 20.0f);

			// A spawn point we cannot path to is skipped, not fatal.
			if (destination is null || !Navmesh.MoveTo(destination.Value, patrolFlying && HuntFlight.IsFlying)) {
				patrolRemaining.Remove(waypoint);
				return;
			}

			patrolWaypoint = waypoint;
			moveIssued = true;
			moveStartPosition = playerPosition;
			stepClock.Restart();
			return;
		}

		if (stepClock.Elapsed > WaypointTimeout) {
			if (patrolWaypoint is { } stale) patrolRemaining.Remove(stale);
			patrolWaypoint = null;
			moveIssued = false;
			return;
		}

		if (stepClock.Elapsed < MoveStartGrace) return;
		if (Navmesh.PathfindInProgress || Navmesh.IsPathRunning) return;

		// vnavmesh went idle without the point being covered. Two very different causes, told
		// apart by whether the character actually went anywhere:
		//  * barely moved -> no path to that spawn point, skip it and carry on,
		//  * moved -> the player took the controls back, so stop rather than fight them for
		//    the character. Clicking patrol again re-plans from wherever they now are.
		if (Vector3.Distance(playerPosition, moveStartPosition) < UnreachableMoveThreshold) {
			if (patrolWaypoint is { } unreachable) patrolRemaining.Remove(unreachable);
			patrolWaypoint = null;
			moveIssued = false;
			return;
		}

		Finish(Strings.HuntAssistStatusStopped);
	}

	/// <summary>Read-only object table scan for the mark. Never targets or attacks anything.</summary>
	private static IGameObject? FindTargetObject(HuntTargetInfo targetInfo) {
		if (targetInfo.BNpcBaseId is 0) return null;

		foreach (var gameObject in Service.ObjectTable) {
			if (gameObject.ObjectKind is not ObjectKind.BattleNpc) continue;
			if (gameObject.DataId != targetInfo.BNpcBaseId) continue;
			if (!gameObject.IsValid()) continue;

			return gameObject;
		}

		return null;
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
		zoneTarget = null;
		patrolTarget = null;
		patrolRemaining.Clear();
		patrolTotal = 0;
		patrolWaypoint = null;
		patrolFlying = false;
		mountClock.Reset();
		mountRetryClock.Reset();
		ActiveOrderTypeRowId = 0;
		PrintMessage(status);
	}

	private void Fail(string status) {
		Step = HuntAssistStep.Stopped;
		StatusText = status;
		stepClock.Reset();
		target = null;
		zoneTarget = null;
		patrolTarget = null;
		patrolRemaining.Clear();
		patrolTotal = 0;
		patrolWaypoint = null;
		patrolFlying = false;
		mountClock.Reset();
		mountRetryClock.Reset();
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

	private static void PlaceMapFlag(HuntBoardLocation board)
		=> PlaceMapFlag(board.TerritoryId, board.MapId, board.Position, true);

	/// <summary>Degradation path when we cannot walk there: show it on the map instead.</summary>
	private static unsafe void PlaceMapFlag(uint territoryId, uint mapId, Vector3 position, bool withFlag) {
		try {
			var agentMap = AgentMap.Instance();
			if (agentMap is null) return;

			if (withFlag) agentMap->SetFlagMapMarker(territoryId, mapId, position);
			agentMap->OpenMap(mapId, territoryId);
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
