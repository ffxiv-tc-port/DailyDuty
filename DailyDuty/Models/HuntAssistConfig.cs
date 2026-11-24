using KamiLib.Configuration;

namespace DailyDuty.Models;

public class HuntAssistConfig {
	/// <summary>
	/// How close the patrol has to get to a spawn point before it counts as checked.
	/// Marks are in the object table well beyond 100y on TC (patrol has spotted them at 311/338/507y),
	/// so the ceiling is 300y. The default stays below the player's observed ~200y - missing the
	/// mark costs the run, re-checking a covered point only costs a few seconds.
	/// </summary>
	public float DetectionRadius = 150.0f;

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

	/// <summary>
	/// 使用者在每週討伐頁按過出發後，每打完一張通緝目標就自動前往下一張。缺鍵時同上沿用欄位初始值（開）；
	/// 只在本次登入、使用者自己起頭的接續裡有效，不存任何「接續中」狀態。
	/// </summary>
	public bool AutoContinueNextBill = true;

	/// <summary>
	/// 帶路到告示板後自動接取這一趟要接的那張（每日或每週），接到每週的就接著前往目標。預設關；
	/// 只在使用者按出發（含自動接續）的告示板行程裡生效，出發當下從設定抄一份。
	/// </summary>
	public bool AutoAcceptBill = false;

	/// <summary>接近通緝目標完成後選取它並開自動攻擊，進入戰鬥後的出招交給戰鬥插件。預設關。</summary>
	public bool AutoEngageTarget = false;

	public const float MinimumDetectionRadius = 10.0f;
	public const float MaximumDetectionRadius = 300.0f;
	public const float MinimumApproachDistance = 5.0f;
	public const float MaximumApproachDistance = 50.0f;

	public static HuntAssistConfig Load()
		=> Service.PluginInterface.LoadCharacterFile(Service.PlayerState.ContentId, "HuntAssist.config.json", () => new HuntAssistConfig());

	public void Save()
		=> Service.PluginInterface.SaveCharacterFile(Service.PlayerState.ContentId, "HuntAssist.config.json", this);
}
