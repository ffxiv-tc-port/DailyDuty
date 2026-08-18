using KamiLib.Configuration;

namespace DailyDuty.Models;

public class HuntAssistConfig {
	/// <summary>
	/// How close the patrol has to get to a spawn point before it counts as checked.
	///
	/// Upper bound comes from what the client will actually let us see: Hunt Helper's radar
	/// draws its detection circle at 2 map coordinate units, and one map coordinate unit is
	/// 50 yalms, so ~100y is the furthest a mark can realistically be in the object table.
	/// The default is deliberately half of that - missing the mark costs the player the run,
	/// while re-checking a point they already covered only costs a few seconds.
	/// </summary>
	public float DetectionRadius = 50.0f;

	/// <summary>Mount up and use flying paths when the zone allows it.</summary>
	public bool UseFlying = true;

	public const float MinimumDetectionRadius = 10.0f;
	public const float MaximumDetectionRadius = 100.0f;

	public static HuntAssistConfig Load()
		=> Service.PluginInterface.LoadCharacterFile(Service.PlayerState.ContentId, "HuntAssist.config.json", () => new HuntAssistConfig());

	public void Save()
		=> Service.PluginInterface.SaveCharacterFile(Service.PlayerState.ContentId, "HuntAssist.config.json", this);
}
