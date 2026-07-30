using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>The mark a weekly elite bill is currently asking for.</summary>
public sealed class HuntTargetInfo {
	/// <summary>Localized mark name, straight from BNpcName.</summary>
	public string Name { get; init; } = string.Empty;

	public uint TerritoryId { get; init; }

	public uint MapId { get; init; }

	/// <summary>Localized zone name.</summary>
	public string ZoneName { get; init; } = string.Empty;

	public HuntSpawnPoints.MarkRank Rank { get; init; } = HuntSpawnPoints.MarkRank.B;

	/// <summary>BNpcBase row id of the mark - this is what IGameObject.DataId reports.</summary>
	public uint BNpcBaseId { get; init; }

	/// <summary>Aetheryte chosen as the best patrol start. 0 when the zone has none.</summary>
	public uint AetheryteId { get; init; }

	/// <summary>Localized name of that aetheryte's place.</summary>
	public string AetheryteName { get; init; } = string.Empty;

	/// <summary>True when the choice was made against real spawn-point data.</summary>
	public bool AetheryteChosenFromRoute { get; init; }
}

/// <summary>
/// Resolves which mark the player's currently-held weekly elite bill wants, and where to
/// teleport for it.
/// </summary>
public static class HuntTargets {
	// Resolving a target walks the Aetheryte sheet and scores a patrol route per aetheryte, so
	// it must not run per frame. The bill only changes when the obtained mark index changes,
	// which makes that index a sufficient cache key.
	private static readonly Dictionary<(uint OrderType, byte ObtainedIndex), HuntTargetInfo?> TargetCache = new();
	private static readonly object CacheLock = new();

	/// <summary>
	/// The mark the player's obtained bill points at, or null when no bill is held (or the
	/// sheets cannot resolve it).
	/// </summary>
	public static unsafe HuntTargetInfo? GetCurrentTarget(uint orderTypeRowId) {
		try {
			var huntData = MobHunt.Instance();
			if (huntData is null) return null;
			if (!huntData->IsMarkBillObtained((int) orderTypeRowId)) return null;

			var obtainedIndex = huntData->ObtainedMarkId[(int) orderTypeRowId];
			if (obtainedIndex is 0) return null;

			lock (CacheLock) {
				if (TargetCache.TryGetValue((orderTypeRowId, obtainedIndex), out var cached)) return cached;
			}

			var resolved = ResolveTarget(orderTypeRowId, obtainedIndex);

			lock (CacheLock) {
				TargetCache[(orderTypeRowId, obtainedIndex)] = resolved;
			}

			return resolved;
		}
		catch (Exception ex) {
			Service.Log.Error(ex, $"[HuntAssist] Failed to resolve the target of bill {orderTypeRowId}");
			return null;
		}
	}

	private static HuntTargetInfo? ResolveTarget(uint orderTypeRowId, byte obtainedIndex) {
		try {
			if (!Service.DataManager.GetExcelSheet<MobHuntOrderType>().TryGetRow(orderTypeRowId, out var orderType)) return null;

			// Same arithmetic HuntMarksBase uses to find the active order row.
			var orderRowId = orderType.OrderStart.RowId + obtainedIndex - 1;
			if (!Service.DataManager.GetSubrowExcelSheet<MobHuntOrder>().TryGetSubrow(orderRowId, 0, out var order)) return null;

			if (order.Target.ValueNullable is not { } target) return null;

			return BuildTargetInfo(target);
		}
		catch (Exception ex) {
			Service.Log.Error(ex, $"[HuntAssist] Failed to resolve the target of bill {orderTypeRowId}");
			return null;
		}
	}

	private static HuntTargetInfo? BuildTargetInfo(MobHuntTarget target) {
		// ⚠️ Lumina names this column "TerritoryType", but the sheet actually stores a *Map*
		// row id. Verified against the TC 7.20 EXD dump: every weekly elite target resolves to
		// the correct zone through Map, while the same numbers are empty rows in TerritoryType
		// (e.g. Dawntrail targets carry 857-862, which are the Dawntrail field Maps).
		var mapId = target.TerritoryType.RowId;

		uint territoryId = 0;
		var mapRowId = mapId;

		if (Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>().TryGetRow(mapId, out var map)) {
			territoryId = map.TerritoryType.RowId;
		}

		// Defensive: if the value ever really is a TerritoryType, still do the right thing.
		if (territoryId is 0 && Service.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(mapId, out var directTerritory)) {
			if (directTerritory.PlaceName.RowId is not 0) {
				territoryId = mapId;
				mapRowId = directTerritory.Map.RowId;
			}
		}

		if (territoryId is 0) return null;

		var zoneName = string.Empty;
		if (Service.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory)) {
			zoneName = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
			if (mapRowId is 0) mapRowId = territory.Map.RowId;
		}

		var (rank, bNpcBaseId) = ResolveMonster(target.Name.RowId);
		var (aetheryteId, aetheryteName, fromRoute) = ChooseAetheryte(territoryId, rank);

		return new HuntTargetInfo {
			Name = target.Name.ValueNullable?.Singular.ExtractText() ?? string.Empty,
			TerritoryId = territoryId,
			MapId = mapRowId,
			ZoneName = zoneName,
			Rank = rank,
			BNpcBaseId = bNpcBaseId,
			AetheryteId = aetheryteId,
			AetheryteName = aetheryteName,
			AetheryteChosenFromRoute = fromRoute,
		};
	}

	/// <summary>
	/// Hunt rank of a mark, via NotoriousMonster. Every weekly elite bill target is Rank 1
	/// (B rank) - verified across all six expansions in the TC 7.20 dump - but read it rather
	/// than assume it, so a future patch that changes this does not silently send the player
	/// to the wrong spawn points.
	/// </summary>
	private static (HuntSpawnPoints.MarkRank Rank, uint BNpcBaseId) ResolveMonster(uint bNpcNameRowId) {
		if (bNpcNameRowId is 0) return (HuntSpawnPoints.MarkRank.B, 0);

		foreach (var monster in Service.DataManager.GetExcelSheet<NotoriousMonster>()) {
			if (monster.BNpcName.RowId != bNpcNameRowId) continue;

			var rank = monster.Rank switch {
				2 => HuntSpawnPoints.MarkRank.A,
				3 => HuntSpawnPoints.MarkRank.S,
				_ => HuntSpawnPoints.MarkRank.B,
			};

			return (rank, monster.BNpcBase.RowId);
		}

		return (HuntSpawnPoints.MarkRank.B, 0);
	}

	/// <summary>
	/// Picks the aetheryte to teleport to. Not simply the closest one to anything: it scores
	/// each aetheryte by how long a greedy patrol of the zone's spawn points would be if it
	/// started there, so the teleport already sets up the next step.
	/// </summary>
	private static (uint AetheryteId, string Name, bool FromRoute) ChooseAetheryte(uint territoryId, HuntSpawnPoints.MarkRank rank) {
		var spawnPoints = HuntSpawnPoints.GetSpawnPoints(territoryId, rank);

		uint bestId = 0;
		var bestName = string.Empty;
		var bestScore = float.MaxValue;
		var fromRoute = false;

		foreach (var aetheryte in Service.DataManager.GetExcelSheet<Aetheryte>()) {
			if (aetheryte.RowId is 0) continue;
			if (aetheryte.Territory.RowId != territoryId) continue;
			if (aetheryte is not { IsAetheryte: true, Invisible: false }) continue;

			var name = aetheryte.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

			// No route data: fall back to the first (lowest row id) aetheryte in the zone.
			if (spawnPoints.Count is 0) {
				if (bestId is not 0) continue;

				bestId = aetheryte.RowId;
				bestName = name;
				continue;
			}

			if (HuntBoards.GetAetherytePosition(aetheryte) is not { } position) continue;

			var score = HuntSpawnPoints.EstimateRouteLength(position, spawnPoints);
			if (score >= bestScore) continue;

			bestScore = score;
			bestId = aetheryte.RowId;
			bestName = name;
			fromRoute = true;
		}

		return (bestId, bestName, fromRoute);
	}

	/// <summary>Drops cached target resolutions; used on logout so a new character re-resolves.</summary>
	public static void InvalidateCache() {
		lock (CacheLock) {
			TargetCache.Clear();
		}
	}

	/// <summary>World position of an aetheryte, for the patrol start. Zero when unknown.</summary>
	public static Vector3 GetAetherytePosition(uint aetheryteId) {
		if (aetheryteId is 0) return Vector3.Zero;
		if (!Service.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var aetheryte)) return Vector3.Zero;

		return HuntBoards.GetAetherytePosition(aetheryte) ?? Vector3.Zero;
	}
}
