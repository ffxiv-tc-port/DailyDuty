using System;
using System.Collections.Generic;

namespace DailyDuty.Classes.HuntAssist;

internal enum BillAcceptPhase {
	Off,
	Interacting,
	WaitingMenu,
	WaitingBillWindow,
	WaitingObtained,
	WaitingClose,
}

internal enum BillAcceptAction {
	None,
	Interact,
	SelectOption,
	ClickAccept,
	Done,
	HandBack,
}

internal enum BillAcceptStop {
	None,
	InCombat,
	Busy,
	NoBoard,
	OutOfRange,
	YesAlreadyUnknown,
	MenuTimeout,
	MenuMismatch,
	BillWindowTimeout,
	AcceptButtonUnusable,
	ConfirmShown,
	NotObtained,
	WindowStillOpen,
}

/// <summary>
/// 每幀從遊戲讀好的值；機器本身不碰遊戲狀態。只在用得到的階段才讀的欄位，其他階段給 false／-1 即可。
/// <see cref="MenuOptionIndex"/>：-1＝開著的選單和這張通緝令的告示板選單對不上。
/// </summary>
internal readonly record struct BillAcceptFrame(
	long Now,
	bool Obtained,
	bool InCombat,
	bool Occupied,
	bool BoardFound,
	bool BoardInRange,
	bool MenuReady,
	int MenuOptionIndex,
	bool BillWindowReady,
	bool AcceptButtonUsable,
	bool ConfirmOpen,
	bool YesAlreadyExclusive);

/// <summary>
/// 抵達告示板後「互動 → 選通緝令種類 → 按接受」的順序與時限。每個階段最多送出一次動作就換到下一階段，
/// 所以同一扇視窗不會被按兩次；任何對不上、逾時都交還使用者，不重試、不按別的鈕。只依賴 System，離線探針直接編譯這個檔。
/// </summary>
internal sealed class BillAcceptMachine {
	public const long MenuTimeoutMilliseconds = 5_000;
	public const long BillWindowTimeoutMilliseconds = 5_000;
	public const long ObtainTimeoutMilliseconds = 5_000;
	public const long CloseTimeoutMilliseconds = 10_000;

	private long phaseStart;

	public BillAcceptPhase Phase { get; private set; } = BillAcceptPhase.Off;

	/// <summary>這一輪是遊戲狀態先變成「已接取」（使用者自己按了，或抵達時就持有），不是我們按的。</summary>
	public bool ObtainedWithoutClick { get; private set; }

	public void Begin(long now) {
		Phase = BillAcceptPhase.Interacting;
		phaseStart = now;
		ObtainedWithoutClick = false;
	}

	public void Reset() => Phase = BillAcceptPhase.Off;

	public BillAcceptAction Observe(in BillAcceptFrame f, out BillAcceptStop stop) {
		stop = BillAcceptStop.None;
		if (Phase is BillAcceptPhase.Off) return BillAcceptAction.None;

		if (f.Obtained && Phase is not (BillAcceptPhase.WaitingObtained or BillAcceptPhase.WaitingClose)) {
			ObtainedWithoutClick = true;
			Enter(BillAcceptPhase.WaitingClose, f.Now);
		}

		switch (Phase) {
			case BillAcceptPhase.Interacting:
				if (f.InCombat) return HandBack(BillAcceptStop.InCombat, out stop);
				if (f.Occupied) return HandBack(BillAcceptStop.Busy, out stop);
				if (!f.BoardFound) return HandBack(BillAcceptStop.NoBoard, out stop);
				if (!f.BoardInRange) return HandBack(BillAcceptStop.OutOfRange, out stop);
				if (!f.YesAlreadyExclusive) return HandBack(BillAcceptStop.YesAlreadyUnknown, out stop);

				Enter(BillAcceptPhase.WaitingMenu, f.Now);
				return BillAcceptAction.Interact;

			case BillAcceptPhase.WaitingMenu:
				if (f.MenuReady) {
					if (f.MenuOptionIndex < 0) return HandBack(BillAcceptStop.MenuMismatch, out stop);
					if (!f.YesAlreadyExclusive) return HandBack(BillAcceptStop.YesAlreadyUnknown, out stop);

					Enter(BillAcceptPhase.WaitingBillWindow, f.Now);
					return BillAcceptAction.SelectOption;
				}

				return TimedOut(f.Now, MenuTimeoutMilliseconds) ? HandBack(BillAcceptStop.MenuTimeout, out stop) : BillAcceptAction.None;

			case BillAcceptPhase.WaitingBillWindow:
				if (f.BillWindowReady) {
					if (!f.AcceptButtonUsable) return HandBack(BillAcceptStop.AcceptButtonUnusable, out stop);

					Enter(BillAcceptPhase.WaitingObtained, f.Now);
					return BillAcceptAction.ClickAccept;
				}

				return TimedOut(f.Now, BillWindowTimeoutMilliseconds) ? HandBack(BillAcceptStop.BillWindowTimeout, out stop) : BillAcceptAction.None;

			case BillAcceptPhase.WaitingObtained:
				if (f.Obtained) {
					Enter(BillAcceptPhase.WaitingClose, f.Now);
					return BillAcceptAction.None;
				}

				// 「會覆蓋掉已有的任務」之類的確認框：不替使用者決定。
				if (f.ConfirmOpen) return HandBack(BillAcceptStop.ConfirmShown, out stop);

				return TimedOut(f.Now, ObtainTimeoutMilliseconds) ? HandBack(BillAcceptStop.NotObtained, out stop) : BillAcceptAction.None;

			case BillAcceptPhase.WaitingClose:
				if (!f.BillWindowReady && !f.Occupied) {
					Phase = BillAcceptPhase.Off;
					return BillAcceptAction.Done;
				}

				return TimedOut(f.Now, CloseTimeoutMilliseconds) ? HandBack(BillAcceptStop.WindowStillOpen, out stop) : BillAcceptAction.None;
		}

		return BillAcceptAction.None;
	}

	private bool TimedOut(long now, long limit) => now - phaseStart > limit;

	private void Enter(BillAcceptPhase phase, long now) {
		Phase = phase;
		phaseStart = now;
	}

	private BillAcceptAction HandBack(BillAcceptStop reason, out BillAcceptStop stop) {
		stop = reason;
		Phase = BillAcceptPhase.Off;
		return BillAcceptAction.HandBack;
	}
}

/// <summary>
/// 告示板選單的版面：選項依序是這個資料片的每日通緝令（列號由小到大）、每週通緝令、取消。
/// 選項文字在告示板事件腳本的文字表裡，一個資料片一組（<c>…_TALK_ACTOR_{組}_{行}</c>，第 1 行是提示、選項從第 2 行起）；
/// 組的順序＝腳本參數裡 <c>ORDER_TYPE_WEEKLY_*</c> 出現的順序。只依賴 System，離線探針直接編譯。
/// </summary>
internal static class HuntBoardMenuLayout {
	/// <param name="weeklyOrder">腳本參數裡每週通緝令列號的出現順序。</param>
	/// <param name="groupDailies">與這張通緝令同一組告示板的每日通緝令列號，由小到大。</param>
	/// <param name="optionCount">取消以外的選項數；取消在這個位置。</param>
	public static bool TryLocate(uint orderTypeRowId, bool isWeekly, uint groupWeeklyRowId, IReadOnlyList<uint> weeklyOrder, IReadOnlyList<uint> groupDailies,
		out int actor, out int optionIndex, out int optionCount) {
		actor = -1;
		optionIndex = -1;
		optionCount = groupDailies.Count + 1;

		for (var index = 0; index < weeklyOrder.Count; index++) {
			if (weeklyOrder[index] != groupWeeklyRowId) continue;

			actor = index;
			break;
		}

		if (actor < 0) return false;

		if (isWeekly) {
			if (orderTypeRowId != groupWeeklyRowId) return false;

			optionIndex = groupDailies.Count;
			return true;
		}

		for (var index = 0; index < groupDailies.Count; index++) {
			if (groupDailies[index] != orderTypeRowId) continue;

			optionIndex = index;
			return true;
		}

		return false;
	}

	/// <summary>選項（從 0 起）在文字表裡的鍵；<paramref name="scriptName"/> 是 CustomTalk 的 Name（例 ComDefMobHuntBoard_00202）。</summary>
	public static string OptionKey(string scriptName, int actor, int optionIndex)
		=> $"TEXT_{scriptName.ToUpperInvariant()}_TALK_ACTOR_{actor:D3}_{optionIndex + 2:D4}";

	/// <summary>
	/// 開著的選單是不是這組告示板的選單：選項數、要選的那一項、最後的取消三者都要逐字相同（兩邊先去掉換行）。
	/// 回要選的索引，對不上回 -1。
	/// </summary>
	public static int MatchMenu(IReadOnlyList<string> entries, string expectedOption, string expectedCancel, int optionIndex, int optionCount) {
		if (entries.Count != optionCount + 1) return -1;
		if (optionIndex < 0 || optionIndex >= optionCount) return -1;
		if (!SameText(entries[optionIndex], expectedOption)) return -1;
		if (!SameText(entries[optionCount], expectedCancel)) return -1;

		return optionIndex;
	}

	private static bool SameText(string left, string right)
		=> left.Length > 0 && string.Equals(left.ReplaceLineEndings(string.Empty), right.ReplaceLineEndings(string.Empty), StringComparison.Ordinal);
}
