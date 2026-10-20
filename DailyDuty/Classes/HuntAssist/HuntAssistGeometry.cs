using System.Numerics;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// 討伐輔助裡不碰遊戲狀態的判斷。只依賴 System.Numerics，離線探針直接編譯這個檔來測。
/// </summary>
internal static class HuntAssistGeometry {
	public static float HorizontalDistance(Vector3 a, Vector3 b)
		=> Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

	/// <summary>
	/// 乙太之光旁的站位：從乙太之光朝玩家的方向退 <paramref name="standOff"/> 米（水平），高度沿用乙太之光。
	/// 玩家幾乎就在正上方時方向不可靠，回乙太之光本身、交給尋路找最近的地面。
	/// </summary>
	public static Vector3 StandPointNear(Vector3 aetheryte, Vector3 player, float standOff) {
		var direction = new Vector2(player.X - aetheryte.X, player.Z - aetheryte.Z);
		if (direction.LengthSquared() < 0.01f) return aetheryte;

		direction = Vector2.Normalize(direction) * standOff;
		return new Vector3(aetheryte.X + direction.X, aetheryte.Y, aetheryte.Z + direction.Y);
	}
}
