using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Newtonsoft.Json;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// Known notorious-monster spawn points, keyed by TerritoryType.
///
/// The data file (Resources/HuntSpawnPoints.json) is a verbatim copy of Hunt Helper's
/// Data/SpawnPointData.json. Hunt Helper does not expose its spawn points over IPC and saves
/// user refinements into its own install directory (wiped on update), so a vendored copy is
/// both the cleaner dependency and the one that keeps working when Hunt Helper is not
/// installed.
///
/// Hunt Helper is MIT licensed:
///
///   MIT License - Copyright (c) 2022 imaginary-png
///
///   Permission is hereby granted, free of charge, to any person obtaining a copy of this
///   software and associated documentation files (the "Software"), to deal in the Software
///   without restriction, including without limitation the rights to use, copy, modify, merge,
///   publish, distribute, sublicense, and/or sell copies of the Software, and to permit
///   persons to whom the Software is furnished to do so, subject to the following conditions:
///
///   The above copyright notice and this permission notice shall be included in all copies or
///   substantial portions of the Software.
///
///   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
///   INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
///   PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
///   FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
///   OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
///   DEALINGS IN THE SOFTWARE.
///
/// The stored coordinates are *map* coordinates (what the game shows the player), so they are
/// converted back to world space using the zone's own Map row. That conversion is not a
/// constant: the six Heavensward zones have SizeFactor 95 while everything else has 100.
/// </summary>
public static class HuntSpawnPoints {
	private const string EmbeddedResourceName = "DailyDuty.Resources.HuntSpawnPoints.json";

	/// <summary>Hunt mark rank, matching NotoriousMonster.Rank.</summary>
	public enum MarkRank {
		B = 1,
		A = 2,
		S = 3,
	}

	// ReSharper disable ClassNeverInstantiated.Local
	// ReSharper disable UnusedAutoPropertyAccessor.Local
	private sealed class RawZone {
		public ushort MapID { get; set; }
		public List<RawPosition>? Positions { get; set; }
	}

	private sealed class RawPosition {
		public float X { get; set; }
		public float Y { get; set; }
		public bool A { get; set; }
		public bool B { get; set; }
		public bool S { get; set; }
	}
	// ReSharper restore UnusedAutoPropertyAccessor.Local
	// ReSharper restore ClassNeverInstantiated.Local

	private static Dictionary<ushort, List<RawPosition>>? rawZones;
	private static readonly Dictionary<(uint Territory, MarkRank Rank), List<Vector3>> WorldPointCache = new();
	private static readonly object CacheLock = new();

	/// <summary>
	/// World-space spawn points for a zone, filtered to the ranks that can use them.
	/// Empty when we have no data for that zone - callers must degrade, not assume.
	/// </summary>
	public static IReadOnlyList<Vector3> GetSpawnPoints(uint territoryId, MarkRank rank)
		=> TryGetSpawnPoints(territoryId, rank, out var points) ? points : [];

	/// <summary>
	/// World-space spawn points for a zone, filtered to the ranks that can use them.
	///
	/// Returns false when the conversion data is not available *yet* - that is a different
	/// thing from a zone having no spawn points, and the caller must not treat it as "nothing
	/// to do". Nothing is cached in that case, so a later call can succeed.
	/// </summary>
	public static bool TryGetSpawnPoints(uint territoryId, MarkRank rank, out IReadOnlyList<Vector3> points) {
		lock (CacheLock) {
			if (WorldPointCache.TryGetValue((territoryId, rank), out var cached)) {
				points = cached;
				return true;
			}

			List<Vector3>? built;
			try {
				built = BuildSpawnPoints(territoryId, rank);
			}
			catch (Exception ex) {
				// An exception is "not ready", never "no points" - caching an empty list here
				// would make one bad moment permanent for the rest of the session.
				Service.Log.Error(ex, $"[HuntAssist] Failed to build spawn points for territory {territoryId}");
				points = [];
				return false;
			}

			if (built is null) {
				points = [];
				return false;
			}

			WorldPointCache[(territoryId, rank)] = built;
			points = built;
			return true;
		}
	}

	public static bool HasData(uint territoryId) {
		lock (CacheLock) {
			EnsureRawData();
			return rawZones!.ContainsKey((ushort) territoryId);
		}
	}

	/// <summary>
	/// Null means "the conversion data is not available yet, ask again later". An empty list
	/// means "this zone genuinely has no spawn points" and is safe to cache.
	/// </summary>
	private static List<Vector3>? BuildSpawnPoints(uint territoryId, MarkRank rank) {
		EnsureRawData();

		if (!rawZones!.TryGetValue((ushort) territoryId, out var positions)) return [];

		// The conversion needs this zone's own Map row: SizeFactor is not a constant (the six
		// Heavensward zones are 95, everything else 100) and it divides into the result. The
		// old code silently fell back to 100 and then cached the outcome forever, which turned
		// one unlucky moment into permanently misplaced points. Refuse instead, and retry.
		if (!Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(territoryId, out var territory)) return null;
		if (territory.Map.ValueNullable is not { } map) return null;
		if (map.SizeFactor is 0) return null;

		float scale = map.SizeFactor;
		int offsetX = map.OffsetX;
		int offsetY = map.OffsetY;

		var result = new List<Vector3>(positions.Count);
		foreach (var position in positions) {
			var usable = rank switch {
				MarkRank.B => position.B,
				MarkRank.A => position.A,
				MarkRank.S => position.S,
				_ => true,
			};

			if (!usable) continue;

			result.Add(new Vector3(
				MapToWorld(position.X, scale, offsetX),
				0.0f,
				MapToWorld(position.Y, scale, offsetY)));
		}

		return result;
	}

	/// <summary>
	/// Inverse of Dalamud's MapUtil.ConvertWorldCoordXZToMapCoord
	/// (map = 0.02 * offset + 2048 / scale + 0.02 * world + 1).
	/// </summary>
	private static float MapToWorld(float mapCoordinate, float scale, int offset)
		=> (mapCoordinate - 1.0f - (2048.0f / scale) - (0.02f * offset)) / 0.02f;

	private static void EnsureRawData() {
		if (rawZones is not null) return;

		var zones = new Dictionary<ushort, List<RawPosition>>();

		try {
			var assembly = Assembly.GetExecutingAssembly();
			using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);

			if (stream is null) {
				Service.Log.Warning($"[HuntAssist] Embedded resource '{EmbeddedResourceName}' was not found, patrol data will be unavailable.");
				rawZones = zones;
				return;
			}

			using var reader = new StreamReader(stream);
			var parsed = JsonConvert.DeserializeObject<List<RawZone>>(reader.ReadToEnd());

			if (parsed is not null) {
				foreach (var zone in parsed) {
					if (zone.Positions is not { Count: > 0 }) continue;
					zones[zone.MapID] = zone.Positions;
				}
			}
		}
		catch (Exception ex) {
			Service.Log.Error(ex, "[HuntAssist] Failed to load embedded spawn point data");
		}

		rawZones = zones;
	}

	/// <summary>
	/// Greedy nearest-neighbour tour length from <paramref name="start"/> through every point.
	/// Used to score which aetheryte makes for the shorter patrol, and (in the patrol step) to
	/// order the points themselves. Greedy is not optimal, but with 10-60 points it is instant
	/// and good enough to compare starting positions.
	/// </summary>
	public static float EstimateRouteLength(Vector3 start, IReadOnlyList<Vector3> points) {
		if (points.Count is 0) return 0.0f;

		var remaining = points.ToList();
		var current = start;
		var total = 0.0f;

		while (remaining.Count > 0) {
			var bestIndex = 0;
			var bestDistance = float.MaxValue;

			for (var index = 0; index < remaining.Count; index++) {
				var distance = Vector2.DistanceSquared(
					new Vector2(current.X, current.Z),
					new Vector2(remaining[index].X, remaining[index].Z));

				if (distance >= bestDistance) continue;

				bestDistance = distance;
				bestIndex = index;
			}

			total += MathF.Sqrt(bestDistance);
			current = remaining[bestIndex];
			remaining.RemoveAt(bestIndex);
		}

		return total;
	}
}
