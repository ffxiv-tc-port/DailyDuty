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

	/// <summary>遊戲表裡目的地物件的名稱（告示板、NPC）；自己記錄的位置沒有物件，是空字串。</summary>
	public string Name { get; init; } = string.Empty;

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
/// Only the EObj row ids are hardcoded - they are stable game data and carry no text. Every
/// position, zone name, map and aetheryte is resolved from the client's own sheets, so the TC
/// client shows TC names without a single hardcoded string.
/// EObjName gives the board names, and Level (Type 45) gives exactly one placement row per
/// board with X/Y/Z + Territory + Map.
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

	/// <summary>
	/// ENpcResident row of the Mini Cactpot broker. A Type 8 Level row places it inside the
	/// Gold Saucer, so the same resolver that places hunt boards can place it too.
	/// </summary>
	public const uint MiniCactpotBrokerObjectId = 1010445;

	/// <summary>ENpcResident row of the Jumbo Cactpot broker, placed by a Type 8 Level row too.</summary>
	public const uint JumboCactpotBrokerObjectId = 1010446;

	/// <summary>ENpcResident row of the Fashion Report judge, in the Gold Saucer.</summary>
	public const uint FashionReportJudgeObjectId = 1025176;

	/// <summary>ENpcResident row of the attendant who registers Masked Carnivale stages.</summary>
	public const uint MaskedCarnivaleAttendantObjectId = 1026926;

	/// <summary>
	/// EObj row of the Doman Enclave reconstruction donation box. An EObj rather than an NPC,
	/// so its Level row is Type 45 like a hunt board's - the resolver does not care which.
	/// </summary>
	public const uint DomanEnclaveDonationBoxObjectId = 2009567;

	/// <summary>ENpcResident row of Khloe Aliapoh, who hands out Wondrous Tails journals.</summary>
	public const uint WondrousTailsKhloeObjectId = 1017653;

	/// <summary>ENpcResident row of the Faux Commander, who runs the Faux Hollows puzzle.</summary>
	public const uint FauxHollowsCommanderObjectId = 1033921;

	/// <summary>
	/// Level Object ids that are not hunt boards but ride along with the same single pass over
	/// the sheet - walking ~58k rows is the expensive part, and doing it twice buys nothing.
	/// </summary>
	private static readonly uint[] ExtraPlacementObjects = [
		MiniCactpotBrokerObjectId,
		JumboCactpotBrokerObjectId,
		FashionReportJudgeObjectId,
		MaskedCarnivaleAttendantObjectId,
		DomanEnclaveDonationBoxObjectId,
		WondrousTailsKhloeObjectId,
		FauxHollowsCommanderObjectId,
	];

	/// <summary>MobHuntOrderType row of the A Realm Reborn weekly bill - the one group whose boards are per Grand Company.</summary>
	private const uint ArrOrderTypeRowId = 4;

	private static readonly Dictionary<uint, HuntBoardLocation?> ResolvedBoards = new();
	private static Dictionary<uint, uint>? boardGroupCache;
	private static Dictionary<uint, Vector3>? aetherytePositionCache;
	private static Dictionary<uint, (uint TerritoryId, uint MapId, Vector3 Position)>? boardPlacementCache;
	private static readonly object CacheLock = new();

	/// <summary>The weekly elite bill row ids we know a board for, in expansion order.</summary>
	public static IReadOnlyCollection<uint> WeeklyOrderTypeRowIds => WeeklyBoardObjects.Keys;

	public static bool IsWeeklyOrderTypeSupported(uint orderTypeRowId)
		=> WeeklyBoardObjects.ContainsKey(orderTypeRowId);

	/// <summary>Any bill row - daily or weekly - that we can point at a board.</summary>
	public static bool IsOrderTypeSupported(uint orderTypeRowId)
		=> GetBoardGroupKey(orderTypeRowId) is not 0;

	/// <summary>
	/// The board group a bill row belongs to, named by its expansion's weekly row id. Daily and
	/// weekly bills of one expansion are issued by the same physical boards, so they share this
	/// key - including as the key of the player's recorded-position table.
	/// 0 when the sheet does not settle it; callers fall back to "no board data" rather than guess.
	/// </summary>
	public static uint GetBoardGroupKey(uint orderTypeRowId) {
		lock (CacheLock) {
			EnsureBoardGroupCache();

			return boardGroupCache!.GetValueOrDefault(orderTypeRowId);
		}
	}

	/// <summary>
	/// All boards that can issue the given bill. For A Realm Reborn the list is filtered
	/// down to the player's own Grand Company (the other two boards will not talk to them).
	/// </summary>
	public static List<HuntBoardLocation> GetBoards(uint orderTypeRowId) {
		var result = new List<HuntBoardLocation>();
		FillBoards(orderTypeRowId, result);
		return result;
	}

	/// <summary>同 <see cref="GetBoards"/>，結果先清空再填進呼叫端給的清單（每幀畫的表格重用同一份）。</summary>
	public static void FillBoards(uint orderTypeRowId, List<HuntBoardLocation> result) {
		result.Clear();

		var groupKey = GetBoardGroupKey(orderTypeRowId);
		if (!WeeklyBoardObjects.TryGetValue(groupKey, out var objectIds)) return;

		if (groupKey is ArrOrderTypeRowId) {
			var grandCompanyIndex = GetGrandCompanyBoardIndex();
			if (grandCompanyIndex is >= 0 && grandCompanyIndex < objectIds.Length) {
				if (Resolve(objectIds[grandCompanyIndex]) is { } own) result.Add(own);
				return;
			}
		}

		foreach (var objectId in objectIds) {
			if (Resolve(objectId) is { } board) result.Add(board);
		}
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
		HuntBoardLocation? resolved = null;
		string? pendingWarning = null;
		Exception? pendingError = null;

		lock (CacheLock) {
			if (ResolvedBoards.TryGetValue(eObjectId, out var cached)) return cached;

			try {
				resolved = ResolveUncached(eObjectId, out pendingWarning);
			}
			catch (Exception ex) {
				pendingError = ex;
			}

			ResolvedBoards[eObjectId] = resolved;
		}

		// Written with the lock released. Service.Log goes through Dalamud's Serilog sink,
		// which does file I/O and takes locks of its own - holding CacheLock across that puts
		// every other resolver behind the log file.
		if (pendingWarning is not null) Service.Log.Warning(pendingWarning);
		if (pendingError is not null) Service.Log.Error(pendingError, $"[HuntAssist] Failed to resolve hunt board {eObjectId}");

		return resolved;
	}

	/// <summary>
	/// The warning this can produce is handed back rather than written: the only caller holds
	/// <see cref="CacheLock"/>, and the log write belongs outside it.
	/// </summary>
	private static HuntBoardLocation? ResolveUncached(uint eObjectId, out string? pendingWarning) {
		pendingWarning = null;

		EnsureBoardPlacementCache();

		if (!boardPlacementCache!.TryGetValue(eObjectId, out var placement)) {
			pendingWarning = $"[HuntAssist] No Level row places EObj {eObjectId}";
			return null;
		}

		var (territoryId, mapId, position) = placement;

		return BuildLocation(eObjectId, territoryId, mapId, position);
	}

	/// <summary>
	/// Builds a destination out of a position the player recorded by standing on it.
	/// Deliberately uncached: a record can be re-taken at any time, and this skips the Level
	/// scan that made caching worth it for the boards.
	/// </summary>
	public static HuntBoardLocation BuildFromPosition(uint territoryId, Vector3 position)
		=> BuildLocation(0, territoryId, 0, position);

	/// <summary>
	/// Everything that follows from a world position: zone name, map, and the two aetherytes
	/// the trip needs. A <paramref name="mapId"/> of 0 is taken from the territory instead.
	/// </summary>
	private static HuntBoardLocation BuildLocation(uint eObjectId, uint territoryId, uint mapId, Vector3 position) {
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
			Name = GetObjectName(eObjectId),
			PrimaryAetheryteId = primary,
			PrimaryTerritoryId = primaryTerritoryId,
			NearestAetheryteId = nearest,
			NearestAetheryteDistance = nearestDistance,
			ExpansionVersion = expansionVersion,
		};
	}

	/// <summary>Level 的 Object 可能是 NPC（ENpcResident）也可能是物件（EObjName），兩張表的 row id 不重疊。取不到回空字串。</summary>
	private static string GetObjectName(uint objectId) {
		if (objectId is 0) return string.Empty;

		if (Service.DataManager.GetExcelSheet<ENpcResident>().TryGetRow(objectId, out var npc)) return npc.Singular.ExtractText();
		if (Service.DataManager.GetExcelSheet<EObjName>().TryGetRow(objectId, out var eObj)) return eObj.Singular.ExtractText();

		return string.Empty;
	}

	/// <summary>
	/// MobHuntOrder is laid out as one contiguous block per expansion - that expansion's daily
	/// bills first, its weekly elite bill last - so the weekly row with the next-greater
	/// OrderStart is the one whose boards a daily bill shares. Derived from the sheet on
	/// purpose: not one daily row id is written down here.
	/// </summary>
	private static void EnsureBoardGroupCache() {
		if (boardGroupCache is not null) return;

		var groups = new Dictionary<uint, uint>();
		var weeklyRows = new List<(uint OrderStart, uint RowId)>();

		foreach (var order in Service.DataManager.GetExcelSheet<MobHuntOrderType>()) {
			if (order.Type is not 2) continue;
			if (!WeeklyBoardObjects.ContainsKey(order.RowId)) continue;

			weeklyRows.Add((order.OrderStart.RowId, order.RowId));
			groups[order.RowId] = order.RowId;
		}

		weeklyRows.Sort((left, right) => left.OrderStart.CompareTo(right.OrderStart));

		foreach (var order in Service.DataManager.GetExcelSheet<MobHuntOrderType>()) {
			if (order.Type is not 1) continue;

			foreach (var weekly in weeklyRows) {
				if (weekly.OrderStart <= order.OrderStart.RowId) continue;

				groups[order.RowId] = weekly.RowId;
				break;
			}
		}

		boardGroupCache = groups;
	}

	/// <summary>
	/// The Level sheet is the placement table - exactly one row per hunt board. It has ~58k
	/// rows, so scan it once for every board we know about rather than once per board.
	/// </summary>
	private static void EnsureBoardPlacementCache() {
		if (boardPlacementCache is not null) return;

		var wanted = new HashSet<uint>();
		foreach (var objectIds in WeeklyBoardObjects.Values) wanted.UnionWith(objectIds);
		wanted.UnionWith(ExtraPlacementObjects);

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

	/// <summary>
	/// Territory and position of one aetheryte/shard row. A position taken from the map marker has
	/// Y = 0, so compare it horizontally only.
	/// </summary>
	public static bool TryGetAetheryteLocation(uint aetheryteId, out uint territoryId, out Vector3 position) {
		territoryId = 0;
		position = default;

		if (!Service.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var aetheryte)) return false;
		if (GetAetherytePosition(aetheryte) is not { } found) return false;

		territoryId = aetheryte.Territory.RowId;
		position = found;
		return true;
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
			boardGroupCache = null;
			aetherytePositionCache = null;
			boardPlacementCache = null;
		}
	}
}
