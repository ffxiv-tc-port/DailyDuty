using System;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>
/// 介面與 <see cref="System.Diagnostics.Stopwatch"/> 用到的那幾個成員相同，多一個 <see cref="Paused"/>：
/// 暫停期間不累計。用來讓「使用者手動操作、討伐輔助讓位」的時間不算進各步驟的時限。
/// 時間來源可注入（毫秒、單調遞增），預設 <see cref="Environment.TickCount64"/>。框架執行緒限定。
/// </summary>
internal sealed class PausableClock(Func<long>? timeSource = null) {
	private readonly Func<long> now = timeSource ?? (static () => Environment.TickCount64);
	private long accumulated;
	private long segmentStart;
	private bool running;
	private bool paused;

	public bool IsRunning => running;

	public TimeSpan Elapsed => TimeSpan.FromMilliseconds(accumulated + (running && !paused ? now() - segmentStart : 0));

	/// <summary>暫停狀態獨立於 <see cref="Restart"/>／<see cref="Reset"/>：讓位中換步驟，新步驟的時鐘從暫停開始。</summary>
	public bool Paused {
		get => paused;
		set {
			if (value == paused) return;

			var t = now();
			if (running && !paused) accumulated += t - segmentStart;
			paused = value;
			segmentStart = t;
		}
	}

	public void Restart() {
		accumulated = 0;
		running = true;
		segmentStart = now();
	}

	public void Reset() {
		accumulated = 0;
		running = false;
	}
}

internal enum InputYieldTransition {
	None,
	Started,
	Ended,
}

/// <summary>
/// 把 vnavmesh「讓位中」的逐幀讀值變成狀態轉換。讓位結束後 <c>settle</c> 毫秒內仍算「不穩定」：
/// vnavmesh 接續時可能還在內部重新尋路，角色會短暫不動，卡住判斷不能把這段算進去。
/// </summary>
internal sealed class InputYieldTracker(long settleMilliseconds) {
	private bool yielding;
	private bool hasEnded;
	private long startedAt;
	private long endedAt;

	public bool IsYielding => yielding;

	/// <summary>目前這次（或剛結束那次）讓位的長度。</summary>
	public long YieldMilliseconds(long now) => yielding ? now - startedAt : hasEnded ? endedAt - startedAt : 0;

	public InputYieldTransition Observe(bool yieldingNow, long now) {
		if (yieldingNow == yielding) return InputYieldTransition.None;

		yielding = yieldingNow;
		if (yieldingNow) {
			startedAt = now;
			return InputYieldTransition.Started;
		}

		hasEnded = true;
		endedAt = now;
		return InputYieldTransition.Ended;
	}

	public bool Unsettled(long now) => yielding || (hasEnded && now - endedAt < settleMilliseconds);

	public void Reset() {
		yielding = false;
		hasEnded = false;
	}
}
