namespace DailyDuty.Classes.HuntAssist;

internal enum EngageTargetState {
	Missing,
	Present,
	Defeated,
}

internal enum EngageAction {
	None,
	Engage,
	Done,
	HandBack,
}

internal enum EngageStop {
	None,
	TargetLost,
	TargetDefeated,
	OtherTarget,
	Untargetable,
	RangeUnknown,
	OutOfRange,
	AutoAttackBlocked,
	NoCombat,
}

/// <summary>
/// 每幀從遊戲讀好的值。<see cref="InRange"/>：遊戲對自動攻擊的射程與視線判定，null＝問不到；
/// <see cref="OtherTargetSelected"/>：目前選著的目標存在、而且不是這隻通緝目標。
/// </summary>
internal readonly record struct EngageFrame(
	long Now,
	EngageTargetState Target,
	bool Targetable,
	bool InCombat,
	bool OtherTargetSelected,
	bool? InRange,
	bool AutoAttacking);

/// <summary>
/// 接近完成後「選取通緝目標並開自動攻擊」的判斷。只出手一次；已在戰鬥中（不論誰開的）就收手交給戰鬥插件。
/// 使用者選了別的目標、目標不可選取或在射程外都不出手；出手後自動攻擊沒開起來或沒進戰鬥就交還。只依賴 System，離線探針直接編譯。
/// </summary>
internal sealed class EngageMachine {
	public const long TargetableGraceMilliseconds = 5_000;
	public const long AutoAttackStartMilliseconds = 1_500;
	public const long CombatTimeoutMilliseconds = 5_000;

	private long startedAt;
	private long actedAt;

	public bool Active { get; private set; }

	public bool Acted { get; private set; }

	public void Begin(long now) {
		Active = true;
		Acted = false;
		startedAt = now;
	}

	public void Reset() => Active = false;

	public EngageAction Observe(in EngageFrame f, out EngageStop stop) {
		stop = EngageStop.None;
		if (!Active) return EngageAction.None;

		if (f.Target is EngageTargetState.Missing) return HandBack(EngageStop.TargetLost, out stop);
		if (f.Target is EngageTargetState.Defeated) return HandBack(EngageStop.TargetDefeated, out stop);

		if (f.InCombat) {
			Active = false;
			return EngageAction.Done;
		}

		if (f.OtherTargetSelected) return HandBack(EngageStop.OtherTarget, out stop);

		if (Acted) {
			if (!f.AutoAttacking && f.Now - actedAt > AutoAttackStartMilliseconds) return HandBack(EngageStop.AutoAttackBlocked, out stop);
			return f.Now - actedAt > CombatTimeoutMilliseconds ? HandBack(EngageStop.NoCombat, out stop) : EngageAction.None;
		}

		if (!f.Targetable) {
			return f.Now - startedAt > TargetableGraceMilliseconds ? HandBack(EngageStop.Untargetable, out stop) : EngageAction.None;
		}

		if (f.InRange is null) return HandBack(EngageStop.RangeUnknown, out stop);
		if (f.InRange is false) return HandBack(EngageStop.OutOfRange, out stop);

		Acted = true;
		actedAt = f.Now;
		return EngageAction.Engage;
	}

	private EngageAction HandBack(EngageStop reason, out EngageStop stop) {
		stop = reason;
		Active = false;
		return EngageAction.HandBack;
	}
}
