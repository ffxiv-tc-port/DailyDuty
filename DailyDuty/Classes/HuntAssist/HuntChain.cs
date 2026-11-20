using System.Numerics;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>一趟行程怎麼結束的；<see cref="HuntAssistController"/> 每次結束（含沒在跑時的取消）都記一筆。</summary>
public enum HuntTripEnd {
	None,
	Finished,

	/// <summary>vnavmesh 因手動操作放掉路徑，趕路或巡邏就此結束。</summary>
	UserTookOver,

	Stopped,
	Cancelled,
}

internal enum HuntChainPhase {
	Off,

	/// <summary>使用者按過出發，等任何一張每週通緝令變成完成。</summary>
	Armed,

	/// <summary>有一張剛完成，等戰鬥結束、角色停穩再出發。</summary>
	Settling,
}

internal enum HuntChainStop {
	None,
	Disabled,
	LoggedOut,
	Duty,
	Cancelled,
	UserTookOver,
	TripStopped,
	OtherTrip,
	CombatAgain,
	CombatTooLong,
	Moved,
	Busy,
	NotSettled,
	Idle,
}

/// <summary>每幀從遊戲讀好的值；機器本身不碰遊戲狀態。</summary>
internal readonly record struct HuntChainFrame(
	long Now,
	bool Enabled,
	bool LoggedIn,
	bool InDuty,
	int TripSerial,
	bool TripRunning,
	int EndSerial,
	HuntTripEnd LastEnd,
	bool LastEndInCombat,
	bool NewlyCompleted,
	bool InCombat,
	bool Occupied,
	bool Transient,
	uint Territory,
	Vector3 Position);

/// <summary>
/// 「打完自動前往下一張」的狀態機。只有 <see cref="Arm"/>（使用者自己按出發）能從 Off 開始；
/// 載入、登入、換區都不會。只依賴 System.Numerics，離線探針直接編譯這個檔。
/// </summary>
internal sealed class HuntChainMachine {
	public const long CalmMilliseconds = 3_000;
	public const long CombatLimitMilliseconds = 60_000;
	public const long SettleLimitMilliseconds = 30_000;
	public const long IdleLimitMilliseconds = 15 * 60 * 1000;
	public const float MoveTolerance = 1.5f;

	private int knownTripSerial;
	private int seenEndSerial;
	private long lastActivity;
	private long completedAt;
	private long combatEndedAt;
	private bool combatEnded;
	private long calmSince = -1;
	private Vector3 anchor;
	private uint settleTerritory;

	public HuntChainPhase Phase { get; private set; } = HuntChainPhase.Off;

	public bool IsActive => Phase is not HuntChainPhase.Off;

	/// <summary>戰鬥結束到出發的實際秒數，給「接續」那行 log。</summary>
	public long CombatEndedAt => combatEndedAt;

	/// <returns>true＝這次才開始（原本是 Off）。已在接續中只換成這一趟，回 false。</returns>
	public bool Arm(int tripSerial, int endSerial, long now) {
		var started = Phase is HuntChainPhase.Off;
		Phase = HuntChainPhase.Armed;
		knownTripSerial = tripSerial;
		seenEndSerial = endSerial;
		lastActivity = now;
		return started;
	}

	/// <summary>自己送出的下一趟已經出發。</summary>
	public void DepartStarted(int tripSerial, int endSerial, long now) => Arm(tripSerial, endSerial, now);

	public void Stop() {
		Phase = HuntChainPhase.Off;
		calmSince = -1;
	}

	/// <returns>要不要出發（true＝呼叫端去找下一張）；<paramref name="stop"/> 不是 None＝已停止，原因在裡面。</returns>
	public bool Observe(in HuntChainFrame f, out HuntChainStop stop) {
		stop = HuntChainStop.None;
		if (Phase is HuntChainPhase.Off) return false;

		stop = CheckAbort(f);
		if (stop is not HuntChainStop.None) {
			Stop();
			return false;
		}

		if (f.TripRunning || f.InCombat) lastActivity = f.Now;

		if (f.NewlyCompleted && Phase is HuntChainPhase.Armed) {
			Phase = HuntChainPhase.Settling;
			completedAt = f.Now;
			combatEnded = false;
			calmSince = -1;
			settleTerritory = f.Territory;
		}

		if (Phase is HuntChainPhase.Armed) {
			if (f.Now - lastActivity <= IdleLimitMilliseconds) return false;

			return Abort(HuntChainStop.Idle, out stop);
		}

		return UpdateSettling(f, out stop);
	}

	private HuntChainStop CheckAbort(in HuntChainFrame f) {
		if (!f.LoggedIn) return HuntChainStop.LoggedOut;
		if (!f.Enabled) return HuntChainStop.Disabled;
		if (f.InDuty) return HuntChainStop.Duty;

		if (f.EndSerial != seenEndSerial) {
			seenEndSerial = f.EndSerial;
			switch (f.LastEnd) {
				case HuntTripEnd.Cancelled: return HuntChainStop.Cancelled;
				case HuntTripEnd.UserTookOver: return HuntChainStop.UserTookOver;
				// 戰鬥中被停下多半就是在打通緝目標，交給完成判斷。
				case HuntTripEnd.Stopped when !f.LastEndInCombat: return HuntChainStop.TripStopped;
			}
		}

		return f.TripSerial != knownTripSerial ? HuntChainStop.OtherTrip : HuntChainStop.None;
	}

	private bool UpdateSettling(in HuntChainFrame f, out HuntChainStop stop) {
		stop = HuntChainStop.None;
		if (f.TripRunning) return false;
		if (f.Territory != settleTerritory) return Abort(HuntChainStop.Moved, out stop);

		if (f.InCombat) {
			if (combatEnded) return Abort(HuntChainStop.CombatAgain, out stop);
			return f.Now - completedAt > CombatLimitMilliseconds && Abort(HuntChainStop.CombatTooLong, out stop);
		}

		if (!combatEnded) {
			combatEnded = true;
			combatEndedAt = f.Now;
		}

		// 戰鬥結束後才開始的對話、雇員鈴、過場＝使用者在做別的事。
		if (f.Occupied) return Abort(HuntChainStop.Busy, out stop);

		if (f.Transient) {
			calmSince = -1;
			return f.Now - combatEndedAt > SettleLimitMilliseconds && Abort(HuntChainStop.NotSettled, out stop);
		}

		if (calmSince < 0) {
			calmSince = f.Now;
			anchor = f.Position;
			return false;
		}

		if (HuntAssistGeometry.HorizontalDistance(f.Position, anchor) > MoveTolerance) return Abort(HuntChainStop.Moved, out stop);

		return f.Now - calmSince >= CalmMilliseconds;
	}

	private bool Abort(HuntChainStop reason, out HuntChainStop stop) {
		stop = reason;
		Stop();
		return false;
	}
}
