using KamiLib.Configuration;

namespace DailyDuty.Models;

public class HuntAssistConfig {
	/// <summary>
	/// How close the patrol has to get to a spawn point before it counts as checked.
	/// Upper bound comes from what the client will actually let us see: Hunt Helper's radar
	/// draws its detection circle at 2 map coordinate units, and one map coordinate unit is
	/// 50 yalms, so ~100y is the furthest a mark can realistically be in the object table.
	/// The default is deliberately half of that - missing the mark costs the player the run,
	/// while re-checking a point they already covered only costs a few seconds.
	/// </summary>
	public float DetectionRadius = 50.0f;

	/// <summary>Mount up and use flying paths when the zone allows it.</summary>
	public bool UseFlying = true;

	/// <summary>
	/// 趕路／巡邏途中手動操作時讓位、停手後接續（需要支援的 vnavmesh）；false＝照舊一碰就結束。
	/// 舊設定檔沒有這個鍵時沿用欄位初始值（System.Text.Json 先 new 再填有的鍵）。
	/// </summary>
	public bool YieldToManualInput = true;

	/// <summary>「前往目標地圖」抵達後接著巡邏；false＝抵達就停（舊行為）。缺鍵時同上沿用欄位初始值。</summary>
	public bool PatrolAfterZoneTeleport = true;

	/// <summary>巡邏發現目標後先接近到 <see cref="ApproachDistance"/> 內再停；false＝發現就原地停（舊行為）。</summary>
	public bool ApproachFoundTarget = true;

	/// <summary>與目標中心的直線距離（含高度）；超出上下限時以上下限為準。</summary>
	public float ApproachDistance = 20.0f;

	public const float MinimumDetectionRadius = 10.0f;
	public const float MaximumDetectionRadius = 100.0f;
	public const float MinimumApproachDistance = 5.0f;
	public const float MaximumApproachDistance = 50.0f;

	public static HuntAssistConfig Load()
		=> Service.PluginInterface.LoadCharacterFile(Service.PlayerState.ContentId, "HuntAssist.config.json", () => new HuntAssistConfig());

	public void Save()
		=> Service.PluginInterface.SaveCharacterFile(Service.PlayerState.ContentId, "HuntAssist.config.json", this);
}
