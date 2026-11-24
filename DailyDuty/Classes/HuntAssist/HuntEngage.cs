namespace DailyDuty.Classes.HuntAssist;

internal enum EngageTargetState {
	Missing,
	Present,
	Defeated,
}

internal enum EngageAction {
	None,
	Dismount,
	Request,
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
	StillMounted,
}

/// <summary>
/// 每幀從遊戲讀好的值。<see cref="InRange"/>：遊戲對自動攻擊的射程與視線判定，null＝問不到；
/// <see cref="OtherTargetSelected"/>：目前選著的目標存在、而且不是這隻通緝目標；<see cref="Casting"/>：自己正在詠唱；
/// <see cref="CanDismount"/>：騎乘中、而且「跳下」此刻遊戲允許使用。
/// </summary>
internal readonly record struct EngageFrame(
	long Now,
	EngageTargetState Target,
	bool Targetable,
	bool InCombat,
	bool OtherTargetSelected,
	bool? InRange,
	bool AutoAttacking,
	bool Casting = false,
	bool Mounted = false,
	bool CanDismount = false);

/// <summary>
/// 接近完成後的開打判斷。只出手一次；已在戰鬥中（不論誰開的）就收手交給戰鬥插件。
/// 有戰鬥插件可接手時先 <see cref="EngageAction.Request"/>（選取並發出開打請求），等不到戰鬥才改開自動攻擊；
/// 使用者選了別的目標、目標不可選取或在射程外都不出手；自動攻擊沒開起來或沒進戰鬥就交還。只依賴 System，離線探針直接編譯。
/// 確認可打時人在坐騎上就先 <see cref="EngageAction.Dismount"/>（每 1.5 秒最多一次，飛行中第一下只降落），下來後重新判射程再出手。
/// </summary>
internal sealed class EngageMachine {
	public const long TargetableGraceMilliseconds = 5_000;
	public const long AutoAttackStartMilliseconds = 1_500;
	public const long CombatTimeoutMilliseconds = 5_000;
	public const long CombatPluginHandoffMilliseconds = 4_000;

	/// <summary>請求後一直在詠唱（戰鬥插件的第一招還在讀）最多等到這裡。</summary>
	public const long CombatPluginCastingMilliseconds = 8_000;

	public const long DismountPressIntervalMilliseconds = 1_500;
	public const long DismountTimeoutMilliseconds = 6_000;

	private long startedAt;
	private long actedAt;
	private long requestedAt;
	private bool waitForCombatPlugin;
	private long dismountStartedAt;
	private long dismountPressedAt;

	public bool Active { get; private set; }

	public bool Acted { get; private set; }

	/// <summary>已選取目標並請戰鬥插件開打（之後若改開自動攻擊，<see cref="Acted"/> 也會成立）。</summary>
	public bool Requested { get; private set; }

	/// <summary>正在等下坐騎；期間不重判射程與可選取（降落中會變）。</summary>
	public bool Dismounting { get; private set; }

	/// <summary>這次開打送過幾次「跳下」；0＝一開始就沒騎乘。</summary>
	public int DismountPresses { get; private set; }

	/// <summary>只在下坐騎完成的那一次 <see cref="Observe"/> 為 true（給呼叫端記一行）。</summary>
	public bool JustDismounted { get; private set; }

	/// <summary>從開始下坐騎到最近一次判斷經過的毫秒。</summary>
	public long DismountElapsedMilliseconds { get; private set; }

	/// <param name="waitForCombatPlugin">有戰鬥插件可接手：先請它開打，等不到才開自動攻擊。</param>
	public void Begin(long now, bool waitForCombatPlugin = false) {
		Active = true;
		Acted = false;
		Requested = false;
		Dismounting = false;
		DismountPresses = 0;
		JustDismounted = false;
		startedAt = now;
		this.waitForCombatPlugin = waitForCombatPlugin;
	}

	public void Reset() => Active = false;

	public EngageAction Observe(in EngageFrame f, out EngageStop stop) {
		stop = EngageStop.None;
		JustDismounted = false;
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

		if (Requested) {
			var waited = f.Now - requestedAt;
			if (waited <= CombatPluginHandoffMilliseconds || f.Casting && waited <= CombatPluginCastingMilliseconds) return EngageAction.None;
		}

		if (Dismounting) {
			if (f.Mounted) return PollDismount(f, out stop);

			Dismounting = false;
			JustDismounted = true;
			DismountElapsedMilliseconds = f.Now - dismountStartedAt;
		}

		if (!f.Targetable) {
			return f.Now - startedAt > TargetableGraceMilliseconds ? HandBack(EngageStop.Untargetable, out stop) : EngageAction.None;
		}

		if (f.InRange is null) return HandBack(EngageStop.RangeUnknown, out stop);
		if (f.InRange is false) return HandBack(EngageStop.OutOfRange, out stop);

		if (f.Mounted) {
			Dismounting = true;
			dismountStartedAt = f.Now;
			return PollDismount(f, out stop);
		}

		if (waitForCombatPlugin && !Requested) {
			Requested = true;
			requestedAt = f.Now;
			return EngageAction.Request;
		}

		Acted = true;
		actedAt = f.Now;
		return EngageAction.Engage;
	}

	private EngageAction PollDismount(in EngageFrame f, out EngageStop stop) {
		stop = EngageStop.None;
		DismountElapsedMilliseconds = f.Now - dismountStartedAt;
		if (DismountElapsedMilliseconds >= DismountTimeoutMilliseconds) {
			Dismounting = false;
			return HandBack(EngageStop.StillMounted, out stop);
		}

		if (!f.CanDismount || DismountPresses > 0 && f.Now - dismountPressedAt < DismountPressIntervalMilliseconds) return EngageAction.None;

		DismountPresses++;
		dismountPressedAt = f.Now;
		return EngageAction.Dismount;
	}

	private EngageAction HandBack(EngageStop reason, out EngageStop stop) {
		stop = reason;
		Active = false;
		return EngageAction.HandBack;
	}
}

/// <summary>
/// 對外的開打請求（IPC <c>DailyDuty.HuntAssist.EngageTargetId</c> 讀這裡）：要求開打的 GameObjectId 與到期時刻。
/// 框架執行緒寫、任何執行緒讀；已發佈的那份不就地修改，整份換參考。沒有請求或已到期一律讀成 0。
/// </summary>
internal sealed class EngageRequestSlot {
	public const long LifetimeMilliseconds = 10_000;

	private sealed record Request(ulong ObjectId, long ExpiresAt);

	private volatile Request? current;

	public void Set(ulong objectId, long now)
		=> current = objectId is 0 ? null : new Request(objectId, now + LifetimeMilliseconds);

	public void Clear() => current = null;

	public bool IsSet => current is not null;

	public ulong Read(long now)
		=> current is { } request && now < request.ExpiresAt ? request.ObjectId : 0;
}
