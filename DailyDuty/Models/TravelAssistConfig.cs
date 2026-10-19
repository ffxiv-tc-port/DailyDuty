using System.Collections.Generic;
using System.Numerics;
using KamiLib.Configuration;

namespace DailyDuty.Models;

/// <summary>A world position the player recorded by standing on it.</summary>
public class RecordedPosition {
	public uint TerritoryId;

	public float X;

	public float Y;

	public float Z;

	public Vector3 ToVector3() => new(X, Y, Z);
}

/// <summary>
/// Destinations the player recorded for themselves, used when the game sheets cannot place
/// something (or place it somewhere unhelpful).
///
/// Stored account-wide rather than per character on purpose: a notice board does not move
/// between characters, and re-recording it on every alt would be busywork.
/// </summary>
public class TravelAssistConfig {
	/// <summary>Board group key (HuntBoards.GetBoardGroupKey) -> the position the player recorded.</summary>
	/// <remarks>Daily and weekly bills of one expansion share a board, so they share an entry.</remarks>
	public Dictionary<uint, RecordedPosition> HuntBoardPositions = new();

	public static TravelAssistConfig Load()
		=> Service.PluginInterface.LoadConfigFile("TravelAssist.config.json", () => new TravelAssistConfig());

	public void Save()
		=> Service.PluginInterface.SaveConfigFile("TravelAssist.config.json", this);
}
