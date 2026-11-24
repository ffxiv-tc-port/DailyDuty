using System;
using System.Collections.Generic;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace DailyDuty.Classes.HuntAssist;

/// <summary>某張通緝令在告示板選單上的樣子：要選的文字、取消的文字與它們的位置（取消在 <see cref="OptionCount"/>）。</summary>
internal sealed record HuntBoardMenuExpectation(string Option, string Cancel, int OptionIndex, int OptionCount);

/// <summary>
/// 從台服資料表組出告示板選單：選項文字取自告示板事件腳本的文字表，版面見 <see cref="HuntBoardMenuLayout"/>。
/// 只寫死腳本的 CustomTalk 列號（所有告示板共用，EObj.Data 指向它或指向包著它的事件），不寫任何中文。
/// 框架執行緒限定；結果按通緝令快取，查不到也快取（資料表不會在執行中變）。
/// </summary>
internal static class HuntBoardMenu {
	private const uint BoardCustomTalkId = 721098;
	private const string WeeklyArgumentPrefix = "ORDER_TYPE_WEEKLY_";

	/// <summary>通緝令視窗「接受」鈕的文字（Addon 表）。</summary>
	private const uint AcceptButtonAddonId = 8421;

	private static readonly Dictionary<uint, HuntBoardMenuExpectation?> Cache = [];
	private static string? acceptButtonText;

	public static string AcceptButtonText
		=> acceptButtonText ??= Service.DataManager.GetExcelSheet<Addon>().GetRowOrDefault(AcceptButtonAddonId)?.Text.ExtractText() ?? string.Empty;

	public static HuntBoardMenuExpectation? Resolve(uint orderTypeRowId) {
		if (Cache.TryGetValue(orderTypeRowId, out var cached)) return cached;

		HuntBoardMenuExpectation? resolved = null;
		try {
			resolved = ResolveUncached(orderTypeRowId, out var why);
			if (resolved is null) Service.Log.Information($"[HuntAssist] 組不出通緝令 {orderTypeRowId} 的告示板選單：{why}。");
		}
		catch (Exception ex) {
			Service.Log.Warning(ex, $"[HuntAssist] 讀告示板選單資料失敗（通緝令 {orderTypeRowId}）");
		}

		Cache[orderTypeRowId] = resolved;
		return resolved;
	}

	private static HuntBoardMenuExpectation? ResolveUncached(uint orderTypeRowId, out string why) {
		var orderTypes = Service.DataManager.GetExcelSheet<MobHuntOrderType>();
		if (!orderTypes.TryGetRow(orderTypeRowId, out var order)) {
			why = "MobHuntOrderType 沒有這一列";
			return null;
		}

		var groupKey = HuntBoards.GetBoardGroupKey(orderTypeRowId);
		if (groupKey is 0) {
			why = "不知道它屬於哪一組告示板";
			return null;
		}

		var dailies = new List<uint>();
		foreach (var row in orderTypes) {
			if (row.Type is 1 && HuntBoards.GetBoardGroupKey(row.RowId) == groupKey) dailies.Add(row.RowId);
		}

		dailies.Sort();

		if (!Service.DataManager.GetExcelSheet<CustomTalk>().TryGetRow(BoardCustomTalkId, out var talk)) {
			why = $"CustomTalk 沒有 {BoardCustomTalkId}";
			return null;
		}

		var scriptName = talk.Name.ExtractText();
		var weeklyOrder = new List<uint>();
		foreach (var script in talk.Script) {
			if (script.ScriptInstruction.ExtractText().StartsWith(WeeklyArgumentPrefix, StringComparison.Ordinal)) weeklyOrder.Add(script.ScriptArg);
		}

		if (scriptName.Length is 0 || !HuntBoardMenuLayout.TryLocate(orderTypeRowId, order.Type is 2, groupKey, weeklyOrder, dailies, out var actor, out var optionIndex, out var optionCount)) {
			why = $"腳本 {scriptName} 裡找不到這張通緝令的位置（每週參數 {string.Join("/", weeklyOrder)}，同組每日 {string.Join("/", dailies)}）";
			return null;
		}

		var sheetName = $"custom/{(BoardCustomTalkId & 0xFFFF) / 100:D3}/{scriptName}";
		var optionKey = HuntBoardMenuLayout.OptionKey(scriptName, actor, optionIndex);
		var cancelKey = HuntBoardMenuLayout.OptionKey(scriptName, actor, optionCount);
		string? option = null;
		string? cancel = null;

		foreach (var row in Service.DataManager.GetExcelSheet<RawRow>(null, sheetName)) {
			var key = row.ReadStringColumn(0).ExtractText();
			if (key == optionKey) option = row.ReadStringColumn(1).ExtractText();
			else if (key == cancelKey) cancel = row.ReadStringColumn(1).ExtractText();
		}

		if (string.IsNullOrEmpty(option) || string.IsNullOrEmpty(cancel)) {
			why = $"{sheetName} 裡沒有 {optionKey} 或 {cancelKey}";
			return null;
		}

		why = string.Empty;
		return new HuntBoardMenuExpectation(option, cancel, optionIndex, optionCount);
	}
}
