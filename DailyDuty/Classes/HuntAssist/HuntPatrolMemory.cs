using System.Collections.Generic;
using System.Numerics;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// 巡邏已巡過或跳過的出現點，跨「重按巡邏」保留一段時間，重按時不會又挑回同一個點。
/// 依目標（區域、怪物、等級）分開記；每筆到期就丟、總筆數有上限。時間由呼叫端給（毫秒，單調遞增）。
/// 框架執行緒限定；只依賴 System.Numerics，離線探針直接編譯這個檔。
/// </summary>
internal sealed class PatrolPointMemory(long lifetimeMilliseconds, int capacity) {
	public readonly record struct Key(uint TerritoryId, uint TargetId, int Rank);

	private readonly record struct Entry(Key Key, Vector3 Point, long At, bool Skipped);

	/// <summary>出現點每次換算出的座標應逐位相同；容差只防浮點差。</summary>
	private const float MatchRadius = 1.0f;

	private readonly List<Entry> entries = [];

	public int Count => entries.Count;

	public void Remember(Key key, Vector3 point, bool skipped, long now) {
		Prune(now);

		var index = Find(key, point);
		if (index >= 0) {
			entries[index] = entries[index] with { At = now, Skipped = skipped };
			return;
		}

		if (entries.Count >= capacity) entries.RemoveAt(OldestIndex());
		entries.Add(new Entry(key, point, now, skipped));
	}

	/// <summary>把還沒到期、屬於 <paramref name="key"/> 的點從 <paramref name="points"/> 移掉，回傳移掉的巡過／跳過各幾個。</summary>
	public (int Visited, int Skipped) Exclude(Key key, List<Vector3> points, long now) {
		Prune(now);

		var visited = 0;
		var skipped = 0;
		for (var index = points.Count - 1; index >= 0; index--) {
			var found = Find(key, points[index]);
			if (found < 0) continue;

			if (entries[found].Skipped) skipped++; else visited++;
			points.RemoveAt(index);
		}

		return (visited, skipped);
	}

	public void Forget(Key key) {
		for (var index = entries.Count - 1; index >= 0; index--) {
			if (entries[index].Key == key) entries.RemoveAt(index);
		}
	}

	private void Prune(long now) {
		for (var index = entries.Count - 1; index >= 0; index--) {
			if (now - entries[index].At >= lifetimeMilliseconds) entries.RemoveAt(index);
		}
	}

	private int Find(Key key, Vector3 point) {
		for (var index = 0; index < entries.Count; index++) {
			var entry = entries[index];
			if (entry.Key == key && HuntAssistGeometry.HorizontalDistance(entry.Point, point) <= MatchRadius) return index;
		}

		return -1;
	}

	private int OldestIndex() {
		var oldest = 0;
		for (var index = 1; index < entries.Count; index++) {
			if (entries[index].At < entries[oldest].At) oldest = index;
		}

		return oldest;
	}
}

/// <summary>
/// 「尋路還在跑、角色卻沒在動」的判斷：從錨點起 <c>window</c> 毫秒內移動不到 <c>minimumMove</c> 米就算卡住。
/// 移動超過門檻就把錨點換到目前位置；呼叫端在尋路計算中、移動被別人壓住時要 <see cref="Reset"/>。
/// </summary>
internal sealed class MovementStallWatch(float minimumMove, long windowMilliseconds) {
	private Vector3 anchor;
	private long anchorAt;
	private bool armed;

	public void Reset() => armed = false;

	public bool Update(Vector3 position, long now) {
		if (!armed || Vector3.Distance(position, anchor) >= minimumMove) {
			anchor = position;
			anchorAt = now;
			armed = true;
			return false;
		}

		return now - anchorAt >= windowMilliseconds;
	}

	public double StalledSeconds(long now) => armed ? (now - anchorAt) / 1000.0 : 0.0;
}
