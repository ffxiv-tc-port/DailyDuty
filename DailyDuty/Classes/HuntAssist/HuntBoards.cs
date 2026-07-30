using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>A resolved hunt board: where it is, and how to get near it.</summary>
public sealed class HuntBoardLocation {
	/// <summary>EObj row id of the board itself.</summary>
	public uint EObjectId { get; init; }

	public uint TerritoryId { get; init; }

	public uint MapId { get; init; }

	/// <summary>World position of the board, straight out of the Level sheet.</summary>
	public Vector3 Position { get; init; }

	/// <summary>Localized zone name, from the game's own sheets (never hardcoded).</summary>
	public string ZoneName { get; init; } = string.Empty;

	/// <summary>The teleportable aetheryte of this city.</summary>
	public uint PrimaryAetheryteId { get; init; }

	/// <summary>
	/// Territory the teleport actually lands in. Usually the same as <see cref="TerritoryId"/>,
	/// but not always: the Maelstrom board is in Limsa Lominsa Upper Decks, which has no
	/// aetheryte at all - its teleport lands one zone over in the Lower Decks.
	/// </summary>
	public uint PrimaryTerritoryId { get; init; }

	/// <summary>The aetheryte/aethernet shard physically closest to the board.</summary>
	public uint NearestAetheryteId { get; init; }

	/// <summary>Straight-line distance from <see cref="NearestAetheryteId"/> to the board.</summary>
	public float NearestAetheryteDistance { get; init; }

	/// <summary>Expansion major version - 2 for A Realm Reborn, 7 for Dawntrail.</summary>
	public int ExpansionVersion { get; init; }
}

/// <summary>
/// Static hunt board table plus the Lumina lookups that turn it into world positions.
///
/// Only the EObj row ids are hardcoded - they are stable game data and carry no text. Every
/// position, zone name, map and aetheryte is resolved from the client's own sheets, so the TC
/// client shows TC names without a single hardcoded string.
///
/// EObj ids verified against the TC 7.20 EXD dump: EObjName gives the board names, and Level
/// (Type 45) gives exactly one placement row per board with X/Y/Z + Territory + Map. The
/// EventItem name of each weekly bill cross-checks the board it belongs to.
/// </summary>
public static class HuntBoards {
	/// <summary>
	/// MobHuntOrderType.RowId (the weekly elite bills, Type == 2) -> hunt board EObj ids.
	/// A Realm Reborn has one board per Grand Company; Stormblood through Endwalker have two
	/// interchangeable boards, one per hub city.
	/// </summary>
	private static readonly Dictionary<uint, uint[]> WeeklyBoardObjects = new() {
		{ 4, [2004438, 2004439, 2004440] }, // 2.x - Grand Company boards: Limsa / Gridania / Ul'dah
		{ 5, [2005909] },                   // 3.x - Foundation
		{ 9, [2008654, 2008655] },          // 4.x - Rhalgr's Reach / Kugane
		{ 13, [2010340, 2010341] },         // 5.x - The Crystarium / Eulmore
		{ 17, [2012236, 2012237] },         // 6.x - Old Sharlayan / Radz-at-Han
		{ 21, [2014155] },                  // 7.x - Tuliyollal
	};

	/// <summary>Grand Company id (PlayerState.GrandCompany) -> index into the A Realm Reborn board list.</summary>
	private const uint ArrOrderTypeRowId = 4;

	private static readonly Dictionary<uint, HuntBoardLocation?> ResolvedBoards = new();
	private static Dictionary<uint, Vector3>? aetherytePositionCache;
	private static Dictionary<uint, (uint TerritoryId, uint MapId, Vector3 Position)>? boardPlacementCache;
	private static readonly object CacheLock = new();

	/// <summary>The weekly elite bill row ids we know a board for, in expansion order.</summary>
	public static IReadOnlyCollection<uint> WeeklyOrderTypeRowIds => WeeklyBoardObjects.Keys;

	public static bool IsWeeklyOrderTypeSupported(uint orderTypeRowId)
		=> WeeklyBoardObjects.ContainsKey(orderTypeRowId);

	/// <summary>
	/// All boards that can issue the given weekly bill. For A Realm Reborn the list is filtered
	/// down to the player's own Grand Company (the other two boards will not talk to them).
	/// </summary>
	public static List<HuntBoardLocation> GetBoards(uint orderTypeRowId) {
		var result = new List<HuntBoardLocation>();
		if (!WeeklyBoardObjects.TryGetValue(orderTypeRowId, out var objectIds)) return result;

		if (orderTypeRowId is ArrOrderTypeRowId) {
			var grandCompanyIndex = GetGrandCompanyBoardIndex();
			if (grandCompanyIndex is >= 0 && grandCompanyIndex < objectIds.Length) {
				objectIds = [objectIds[grandCompanyIndex]];
			}
		}

		foreach (var objectId in objectIds) {
			if (Resolve(objectId) is { } board) result.Add(board);
		}

		return result;
	}

	/// <summary>0 = Maelstrom (Limsa), 1 = Twin Adder (Gridania), 2 = Immortal Flames (Ul'dah), -1 = none.</summary>
	private static unsafe int GetGrandCompanyBoardIndex() {
		var playerState = PlayerState.Instance();
		if (playerState is null) return -1;

		return playerState->GrandCompany switch {
			1 => 0,
			2 => 1,
			3 => 2,
			_ => -1,
		};
	}

	/// <summary>Resolves (and caches) a board's placement. Null when the sheets do not have it.</summary>
	public static HuntBoardLocation? Resolve(uint eObjectId) {
		lock (CacheLock) {
			if (ResolvedBoards.TryGetValue(eObjectId, out var cached)) return cached;

			HuntBoardLocation? resolved = null;
			try {
				resolved = ResolveUncached(eObjectId);
			}
			catch (Exception ex) {
				Service.Log.Error(ex, $"[HuntAssist] Failed to resolve hunt board {eObjectId}");
			}

			ResolvedBoards[eObjectId] = resolved;
			return resolved;
		}
	}

	private static HuntBoardLocation? ResolveUncached(uint eObjectId) {
		EnsureBoardPlacementCache();

		if (!boardPlacementCache!.TryGetValue(eObjectId, out var placement)) {
			Service.Log.Warning($"[HuntAssist] No Level row places EObj {eObjectId}");
			return null;
		}

		var (territoryId, mapId, position) = placement;

		var zoneName = string.Empty;
		var expansionVersion = 2;
		if (Service.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory)) {
			zoneName = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
			expansionVersion = (int) territory.ExVersion.RowId + 2;
			if (mapId is 0) mapId = territory.Map.RowId;
		}

		FindAetherytes(territoryId, position, out var primary, out var nearest, out var nearestDistance);

		// Some boards sit in a city sub-zone that has no aetheryte of its own (Limsa Lominsa
		// Upper Decks). Fall back to the primary aetheryte of the nearest shard's aethernet
		// group, and remember that the teleport lands in a different territory.
		if (primary is 0 && nearest is not 0) primary = FindPrimaryOfAethernetGroup(nearest);

		var primaryTerritoryId = territoryId;
		if (primary is not 0 && Service.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(primary, out var primaryAetheryte)) {
			if (primaryAetheryte.Territory.RowId is not 0) primaryTerritoryId = primaryAetheryte.Territory.RowId;
		}

		return new HuntBoardLocation {
			EObjectId = eObjectId,
			TerritoryId = territoryId,
			MapId = mapId,
			Position = position,
			ZoneName = zoneName,
			PrimaryAetheryteId = primary,
			PrimaryTerritoryId = primaryTerritoryId,
			NearestAetheryteId = nearest,
			NearestAetheryteDistance = nearestDistance,
			ExpansionVersion = expansionVersion,
		};
	}

	/// <summary>
	/// The Level sheet is the placement table - exactly one row per hunt board. It has ~58k
	/// rows, so scan it once for every board we know about rather than once per board.
	/// </summary>
	private static void EnsureBoardPlacementCache() {
		if (boardPlacementCache is not null) return;

		var wanted = new HashSet<uint>();
		foreach (var objectIds in WeeklyBoardObjects.Values) wanted.UnionWith(objectIds);

		var placements = new Dictionary<uint, (uint TerritoryId, uint MapId, Vector3 Position)>();
		foreach (var level in Service.DataManager.GetExcelSheet<Level>()) {
			var objectId = level.Object.RowId;
			if (!wanted.Contains(objectId)) continue;
			if (placements.ContainsKey(objectId)) continue;

			placements[objectId] = (level.Territory.RowId, level.Map.RowId, new Vector3(level.X, level.Y, level.Z));
		}

		boardPlacementCache = placements;
	}

	/// <summary>
	/// Picks the city's teleport target and the aetheryte/shard closest to <paramref name="position"/>.
	/// </summary>
	private static void FindAetherytes(uint territoryId, Vector3 position, out uint primary, out uint nearest, out float nearestDistance) {
		primary = 0;
		nearest = 0;
		nearestDistance = float.MaxValue;

		foreach (var aetheryte in Service.DataManager.GetExcelSheet<Aetheryte>()) {
			if (aetheryte.RowId is 0) continue;
			if (aetheryte.Territory.RowId != territoryId) continue;

			if (aetheryte is { IsAetheryte: true, Invisible: false } && primary is 0) {
				primary = aetheryte.RowId;
			}

			if (GetAetherytePosition(aetheryte) is not { } aetherytePosition) continue;

			var distance = Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(aetherytePosition.X, aetherytePosition.Z));
			if (distance >= nearestDistance) continue;

			nearestDistance = distance;
			nearest = aetheryte.RowId;
		}

		if (nearest is 0) nearestDistance = 0.0f;
	}

	/// <summary>
	/// The teleportable aetheryte that owns an aethernet shard's network. Ported from ECommons'
	/// GameHelpers/Map.FindPrimaryAetheryte (AGPL-3.0, same license as this plugin).
	/// </summary>
	private static uint FindPrimaryOfAethernetGroup(uint aetheryteId) {
		if (!Service.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var shard)) return 0;
		if (shard.IsAetheryte) return aetheryteId;

		foreach (var candidate in Service.DataManager.GetExcelSheet<Aetheryte>()) {
			if (candidate.RowId is 0) continue;
			if (candidate.AethernetGroup != shard.AethernetGroup) continue;
			if (candidate is { IsAetheryte: true, Invisible: false }) return candidate.RowId;
		}

		return 0;
	}

	/// <summary>
	/// World position of an aetheryte. Most aethernet shards have no usable Level row, so we
	/// fall back to their map marker and convert the marker's texture pixel back into world
	/// space. Algorithm ported from ECommons' GameHelpers/Map (AGPL-3.0, same license as this
	/// plugin); see also xivapi/ffxiv-datamining docs/MapCoordinates.md.
	/// </summary>
	public static Vector3? GetAetherytePosition(Aetheryte aetheryte) {
		// Monitor is reentrant, so this is safe both standalone and from inside Resolve().
		lock (CacheLock) {
			return GetAetherytePositionUnlocked(aetheryte);
		}
	}

	private static Vector3? GetAetherytePositionUnlocked(Aetheryte aetheryte) {
		EnsureAetherytePositionCache();

		if (aetheryte.Level.Count > 0 && aetheryte.Level[0].ValueNullable is { } level) {
			// Guard against a stale/blank Level row pointing at a different zone.
			if (level.Territory.RowId == aetheryte.Territory.RowId) {
				return new Vector3(level.X, level.Y, level.Z);
			}
		}

		return aetherytePositionCache!.TryGetValue(aetheryte.RowId, out var markerPosition) ? markerPosition : null;
	}

	private static void EnsureAetherytePositionCache() {
		if (aetherytePositionCache is not null) return;

		var byAetheryte = new Dictionary<uint, MapMarker>();
		var byAethernetName = new Dictionary<uint, MapMarker>();

		// DataType 3 keys on the Aetheryte row, DataType 4 keys on its AethernetName row.
		foreach (var marker in Service.DataManager.GetSubrowExcelSheet<MapMarker>().Flatten()) {
			switch (marker.DataType) {
				case 3 when !byAetheryte.ContainsKey(marker.DataKey.RowId):
					byAetheryte[marker.DataKey.RowId] = marker;
					break;

				case 4 when !byAethernetName.ContainsKey(marker.DataKey.RowId):
					byAethernetName[marker.DataKey.RowId] = marker;
					break;
			}
		}

		var positions = new Dictionary<uint, Vector3>();
		foreach (var aetheryte in Service.DataManager.GetExcelSheet<Aetheryte>()) {
			if (aetheryte.RowId is 0) continue;

			if (!byAetheryte.TryGetValue(aetheryte.RowId, out var marker) &&
			    !(aetheryte.AethernetName.RowId is not 0 && byAethernetName.TryGetValue(aetheryte.AethernetName.RowId, out marker))) {
				continue;
			}

			var mapId = aetheryte.Map.RowId is not 0
				? aetheryte.Map.RowId
				: aetheryte.Territory.ValueNullable?.Map.RowId ?? 0;

			positions[aetheryte.RowId] = PixelToWorld(marker.X, marker.Y, mapId);
		}

		aetherytePositionCache = positions;
	}

	private static Vector3 PixelToWorld(int x, int y, uint mapId) {
		var scale = 1.0f;
		short offsetX = 0;
		short offsetY = 0;

		// Fully qualified: FFXIVClientStructs also has a "Map" in the namespace we import above.
		if (Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>().TryGetRow(mapId, out var map)) {
			scale = map.SizeFactor * 0.01f;
			offsetX = map.OffsetX;
			offsetY = map.OffsetY;
		}

		return new Vector3(PixelToWorld(x, scale, offsetX), 0.0f, PixelToWorld(y, scale, offsetY));
	}

	private static float PixelToWorld(float coordinate, float scale, short offset) {
		const float factor = 2048.0f / (50 * 41);
		return ((coordinate * factor) - 1024.0f) / scale - (offset * 0.001f);
	}

	/// <summary>Drops cached lookups; used when the client language changes.</summary>
	public static void InvalidateCache() {
		lock (CacheLock) {
			ResolvedBoards.Clear();
			aetherytePositionCache = null;
			boardPlacementCache = null;
		}
	}
}
