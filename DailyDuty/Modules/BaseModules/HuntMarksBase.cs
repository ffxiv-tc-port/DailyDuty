using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using DailyDuty.Classes;
using DailyDuty.Classes.HuntAssist;
using DailyDuty.Localization;
using DailyDuty.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using KamiLib.Classes;
using Lumina.Excel.Sheets;

namespace DailyDuty.Modules.BaseModules;

public abstract unsafe class HuntMarksBase : Modules.SpecialTask<ModuleTaskData<MobHuntOrderType>, ModuleTaskConfig<MobHuntOrderType>, MobHuntOrderType> {
	private static MobHunt* HuntData => MobHunt.Instance();

	// 資料頁每幀畫一次「帶我去」表格：排序結果、告示板清單、圖示按鈕的標籤都重用，不每幀重新配置。
	private readonly List<LuminaTaskConfig<MobHuntOrderType>> sortedConfigs = [];
	private readonly List<HuntBoardLocation> rowBoards = [];
	private readonly Dictionary<uint, (string Record, string Clear)> recordButtonLabels = [];
	private static readonly Comparison<LuminaTaskConfig<MobHuntOrderType>> ByRowId = static (left, right) => left.RowId.CompareTo(right.RowId);

	// 打完自動前往下一張：狀態只在記憶體裡，載入、登入都從 Off 開始。
	private readonly HuntChainMachine chain = new();
	private readonly HashSet<uint> chainCompleteSeen = [];
	private uint chainCompletedRow;
    
	public override void Update() {
		// 直接走底下的 List：經 TaskData 自己的 GetEnumerator() 每次都會裝箱一個列舉器。
		foreach (var task in Data.TaskData.DataList) {
			// If we have the active mark bill
			var availableMarkId = HuntData->GetAvailableHuntOrderRowId((byte) task.RowId);
			var obtainedMarkId = HuntData->GetObtainedHuntOrderRowId((byte) task.RowId);
                
			if (availableMarkId == obtainedMarkId && !task.Complete) {
				// 跨版本殘留、表裡已沒有的列：當作未完成跳過，不擲例外。
				if (Service.DataManager.GetExcelSheet<MobHuntOrderType>().GetRowOrDefault(task.RowId) is not { } orderData) continue;
				var targetRow = orderData.OrderStart.RowId + HuntData->ObtainedMarkId[(int) task.RowId] - 1;
                
				// Elite
				if (orderData.Type is 2 && IsEliteMarkComplete(targetRow, task.RowId)) {
					task.Complete = true;
					DataChanged = true;
				}
				// Regular Hunt
				else if (orderData.Type is 1 && IsNormalMarkComplete(targetRow, task.RowId)) {
					task.Complete = true;
					DataChanged = true;
				}
			}
		}

		if (SupportsAutoContinue) UpdateChain();
        
		base.Update();
	}

	private bool IsEliteMarkComplete(uint targetRow, uint markId) {
		if (!Service.DataManager.GetSubrowExcelSheet<MobHuntOrder>().TryGetSubrow(targetRow, 0, out var eliteTargetInfo)) return false;

		return HuntData->CurrentKills[(int) markId][0] == eliteTargetInfo.NeededKills;
	}

	private bool IsNormalMarkComplete(uint targetRow, uint markId) {
		for (var index = 0; index < 5; index++) {
			if (!Service.DataManager.GetSubrowExcelSheet<MobHuntOrder>().TryGetSubrow(targetRow, (ushort) index, out var huntData)) return false;

			if (HuntData->CurrentKills[(int) markId][index] != huntData.NeededKills) return false;
		}

		return true;
	}

	public override void Reset() {
		Data.TaskData.Reset();
        
		base.Reset();
	}

	protected override ModuleStatus GetModuleStatus() {
		// 一張都沒勾＝不知道玩家要做哪幾張；算成「已完成」會讓待辦清單與狀態 IPC 把它當成做完了。
		if (!HasTrackedBills()) return ModuleStatus.Unknown;

		return IncompleteTaskCount == 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;
	}

	protected override StatusMessage GetStatusMessage()
		=> HasTrackedBills() ? $"{IncompleteTaskCount} {Strings.HuntsRemaining}" : Strings.HuntMarksNoneTracked;

	protected override bool NothingSelectedToTrack => !HasTrackedBills();

	/// <summary>設定裡有沒有勾任何一張通緝令。每幀都可能被讀，所以不用 LINQ。</summary>
	private bool HasTrackedBills() {
		foreach (var config in Config.TaskConfig.ConfigList) {
			if (config.Enabled) return true;
		}

		return false;
	}

	/// <summary>Weekly elite bills also get "go to the mark" and the patrol; daily bills do not.</summary>
	protected virtual bool SupportsTargetAssist => false;

	/// <summary>打完一張自動前往下一張（<see cref="HuntAssistConfig.AutoContinueNextBill"/>）；只有每週通緝令有。</summary>
	protected virtual bool SupportsAutoContinue => false;

	/// <summary>
	/// 模組層級的「帶我去」＝追蹤中、還沒完成、也還沒接的第一張單（表格順序）的告示板，
	/// 目的地是表格那一列按鈕的其中一個。已經接了的單要去的是標的不是告示板，不算。
	/// </summary>
	public override bool HasTravelDestination => TryGetTravelBill(out _, out _);

	public override bool StartTravel() {
		if (!TryGetTravelBill(out var orderTypeRowId, out var boardGroupKey)) return false;
		if (!StartBoardTrip(orderTypeRowId, boardGroupKey)) return false;

		ArmChain("帶我去", orderTypeRowId);
		return true;
	}

	private bool StartBoardTrip(uint orderTypeRowId, uint boardGroupKey) {
		var controller = System.HuntAssistController;

		// A recorded spot wins over the sheets, same as the table.
		if (System.TravelAssistConfig.HuntBoardPositions.GetValueOrDefault(boardGroupKey) is { } recorded) {
			var destination = HuntBoards.BuildFromPosition(recorded.TerritoryId, recorded.ToVector3());
			return controller.GoToRecordedHuntBoard(destination, orderTypeRowId);
		}

		var boards = HuntBoards.GetBoards(orderTypeRowId);
		if (boards.Count is 0) {
			HuntAssistController.PrintMessage(Strings.HuntAssistNoBoardData);
			return false;
		}

		// One bill's boards are interchangeable; skip the teleport when already in one of their cities.
		var territory = Service.ClientState.TerritoryType;
		var board = boards.FirstOrDefault(entry => entry.TerritoryId == territory || entry.PrimaryTerritoryId == territory) ?? boards[0];

		return controller.GoToHuntBoard(board, orderTypeRowId);
	}

	/// <summary>使用者在這一頁按的出發成功了：開始（或延續）自動接續。<paramref name="what"/> 只給 log。</summary>
	private void ArmChain(string what, uint orderTypeRowId) {
		if (!SupportsAutoContinue || !System.HuntAssistConfig.AutoContinueNextBill) return;

		var controller = System.HuntAssistController;
		if (!chain.Arm(controller.TripSerial, controller.EndSerial, Environment.TickCount64)) return;

		// 只有開始之後才變成完成的那一張算數；按下之前就完成的不觸發。
		chainCompleteSeen.Clear();
		foreach (var task in Data.TaskData.DataList) {
			if (task.Complete) chainCompleteSeen.Add(task.RowId);
		}

		Service.Log.Information($"[HuntAssist] 自動接續開始：使用者按下「{what}」（通緝令 {orderTypeRowId}）；之後每打完一張通緝目標，戰鬥結束並停穩 " +
		                        $"{HuntChainMachine.CalmMilliseconds / 1000} 秒就前往下一張。");
	}

	private void StopChainByUser() {
		if (!chain.IsActive) return;

		chain.Stop();
		Service.Log.Information("[HuntAssist] 自動接續中止：使用者按了「停止自動接續」。");
	}

	private void UpdateChain() {
		if (!chain.IsActive) return;

		var newlyCompleted = TrackChainCompletions();
		var wasSettling = chain.Phase is HuntChainPhase.Settling || newlyCompleted;
		var controller = System.HuntAssistController;
		var condition = Service.Condition;
		var player = Service.ObjectTable.LocalPlayer;

		var frame = new HuntChainFrame(
			Environment.TickCount64,
			System.HuntAssistConfig.AutoContinueNextBill,
			Service.ClientState.IsLoggedIn,
			FrameConditions.IsBoundByDuty(condition),
			controller.TripSerial,
			controller.IsRunning,
			controller.EndSerial,
			controller.LastEnd,
			controller.LastEndInCombat,
			newlyCompleted,
			condition[ConditionFlag.InCombat],
			IsOccupiedElsewhere(condition),
			player is not { IsTargetable: true, IsDead: false } || IsTransient(condition),
			Service.ClientState.TerritoryType,
			player?.Position ?? Vector3.Zero);

		var depart = chain.Observe(frame, out var stop);
		if (stop is not HuntChainStop.None) {
			OnChainStopped(stop, wasSettling, controller.StatusText);
			return;
		}

		if (depart) DepartChain();
	}

	private bool TrackChainCompletions() {
		var newly = false;
		foreach (var task in Data.TaskData.DataList) {
			if (!task.Complete) {
				chainCompleteSeen.Remove(task.RowId);
				continue;
			}

			if (chainCompleteSeen.Add(task.RowId)) {
				newly = true;
				chainCompletedRow = task.RowId;
			}
		}

		return newly;
	}

	/// <summary>戰鬥結束後才出現的這些狀態＝使用者自己在做別的事。</summary>
	private static bool IsOccupiedElsewhere(ICondition condition)
		=> condition[ConditionFlag.Occupied] || condition[ConditionFlag.Occupied30] || condition[ConditionFlag.OccupiedInEvent]
		   || condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Occupied33] || condition[ConditionFlag.OccupiedInCutSceneEvent]
		   || condition[ConditionFlag.Occupied38] || condition[ConditionFlag.Occupied39] || condition[ConditionFlag.OccupiedSummoningBell]
		   || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] || condition[ConditionFlag.Crafting]
		   || condition[ConditionFlag.Gathering] || condition[ConditionFlag.ExecutingGatheringAction] || condition[ConditionFlag.Fishing]
		   || condition[ConditionFlag.UsingHousingFunctions];

	/// <summary>一下子就會結束的狀態：等它過去再算停穩。</summary>
	private static bool IsTransient(ICondition condition)
		=> condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.Casting]
		   || condition[ConditionFlag.Jumping] || condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71]
		   || condition[ConditionFlag.BeingMoved] || condition[ConditionFlag.Unconscious];

	private void OnChainStopped(HuntChainStop stop, bool wasSettling, string tripStatus) {
		var reason = stop switch {
			HuntChainStop.Disabled => "已關閉「打完自動前往下一張」",
			HuntChainStop.LoggedOut => "已登出",
			HuntChainStop.Duty => "進入副本",
			HuntChainStop.Cancelled => "按了取消或停止",
			HuntChainStop.UserTookOver => "趕路或巡邏途中手動操作，行程已交還",
			HuntChainStop.TripStopped => $"行程中止（{tripStatus}）",
			HuntChainStop.OtherTrip => "改去別的目的地（不是每週討伐頁發起的行程）",
			HuntChainStop.CombatAgain => "戰鬥結束後又進入戰鬥",
			HuntChainStop.CombatTooLong => $"完成後仍在戰鬥超過 {HuntChainMachine.CombatLimitMilliseconds / 1000} 秒",
			HuntChainStop.Moved => "完成後手動移動或換區",
			HuntChainStop.Busy => "完成後開始做別的事（對話、雇員鈴、過場等）",
			HuntChainStop.NotSettled => $"戰鬥結束後 {HuntChainMachine.SettleLimitMilliseconds / 1000} 秒仍無法行動（詠唱、讀取、倒地等）",
			HuntChainStop.Idle => $"{HuntChainMachine.IdleLimitMilliseconds / 60000} 分鐘沒有新進度",
			_ => stop.ToString(),
		};

		Service.Log.Information($"[HuntAssist] 自動接續中止：{reason}{(wasSettling ? $"；通緝令 {chainCompletedRow} 已完成、正等出發" : string.Empty)}。");

		// 已經打完、使用者在等它出發時才說；其餘情況行程自己的訊息已經講過。
		if (wasSettling) HuntAssistController.PrintMessage(Strings.HuntChainNotContinued);
	}

	/// <summary>先去已接通緝令的目標（同「前往目標地圖」／「巡邏重生點」），沒有才去下一張的告示板（同「帶我去」）。</summary>
	private void DepartChain() {
		var controller = System.HuntAssistController;
		var settledFor = (Environment.TickCount64 - chain.CombatEndedAt) / 1000.0;

		if (!HasIncompleteTrackedBill()) {
			chain.Stop();
			Service.Log.Information($"[HuntAssist] 自動接續結束：通緝令 {chainCompletedRow} 已完成，追蹤中的每週通緝令全部完成。");
			HuntAssistController.PrintMessage(Strings.HuntChainAllDone);
			return;
		}

		string leg;
		uint orderTypeRowId;
		bool started;

		if (TryGetHeldTarget(out var heldRowId, out var targetInfo)) {
			orderTypeRowId = heldRowId;
			if (Service.ClientState.TerritoryType == targetInfo.TerritoryId) {
				leg = "巡邏重生點";
				started = controller.StartPatrol(targetInfo, orderTypeRowId);
			}
			else {
				leg = "前往目標地圖";
				started = controller.GoToTargetZone(targetInfo, orderTypeRowId, System.HuntAssistConfig.PatrolAfterZoneTeleport);
			}
		}
		else if (TryGetTravelBill(out orderTypeRowId, out var boardGroupKey)) {
			leg = "告示板";
			started = StartBoardTrip(orderTypeRowId, boardGroupKey);
		}
		else {
			chain.Stop();
			Service.Log.Information("[HuntAssist] 自動接續中止：還有沒完成的通緝令，但都沒有可前往的目的地（未接的沒有告示板資料，已接的認不出目標）。");
			HuntAssistController.PrintMessage(Strings.HuntChainNoNext);
			return;
		}

		if (!started) {
			chain.Stop();
			Service.Log.Information($"[HuntAssist] 自動接續中止：下一張（通緝令 {orderTypeRowId}，{leg}）沒有出發：{controller.StatusText}");
			return;
		}

		chain.DepartStarted(controller.TripSerial, controller.EndSerial, Environment.TickCount64);
		Service.Log.Information($"[HuntAssist] 自動接續下一張：通緝令 {chainCompletedRow} 已完成，戰鬥結束後 {settledFor:F1} 秒出發；{leg}（通緝令 {orderTypeRowId}）。");
		HuntAssistController.PrintMessage(Strings.HuntChainDeparting);
	}

	private bool HasIncompleteTrackedBill() {
		foreach (var config in Config.TaskConfig.ConfigList) {
			if (config.Enabled && !(FindTaskData(config.RowId)?.Complete ?? false)) return true;
		}

		return false;
	}

	/// <summary>已接、還沒完成、認得出目標的第一張（表格順序）。</summary>
	private bool TryGetHeldTarget(out uint orderTypeRowId, out HuntTargetInfo targetInfo) {
		var huntData = MobHunt.Instance();
		if (huntData is not null) {
			foreach (var config in Config.TaskConfig.ConfigList.OrderBy(entry => entry.RowId)) {
				if (!config.Enabled) continue;
				if (FindTaskData(config.RowId)?.Complete ?? false) continue;
				if (!huntData->IsMarkBillObtained((int) config.RowId)) continue;
				if (HuntTargets.GetCurrentTarget(config.RowId) is not { } found) continue;

				orderTypeRowId = config.RowId;
				targetInfo = found;
				return true;
			}
		}

		orderTypeRowId = 0;
		targetInfo = null!;
		return false;
	}

	private bool TryGetTravelBill(out uint orderTypeRowId, out uint boardGroupKey) {
		var huntData = MobHunt.Instance();

		foreach (var config in Config.TaskConfig.ConfigList.OrderBy(entry => entry.RowId)) {
			if (!config.Enabled) continue;
			if (FindTaskData(config.RowId)?.Complete ?? false) continue;
			if (huntData is not null && huntData->IsMarkBillObtained((int) config.RowId)) continue;

			var groupKey = HuntBoards.GetBoardGroupKey(config.RowId);
			if (groupKey is 0) continue;

			orderTypeRowId = config.RowId;
			boardGroupKey = groupKey;
			return true;
		}

		orderTypeRowId = 0;
		boardGroupKey = 0;
		return false;
	}

	/// <summary>Extra settings drawn under the shortcut table; only the patrol has any.</summary>
	protected virtual void DrawAssistSettings() { }

	public override void DrawData() {
		// 狀態列只寫灰字「未知」；為什麼不知道、要去哪裡補，放在資料頁最上面一眼看得到的地方。
		if (!HasTrackedBills()) {
			using (ImRaii.PushColor(ImGuiCol.Text, KnownColor.Orange.Vector())) {
				ImGui.TextWrapped(Strings.HuntMarksNoneTracked);
			}

			ImGuiHelpers.ScaledDummy(5.0f);
		}

		base.DrawData();

		DrawHuntAssist();
	}

	/// <summary>
	/// Per-expansion shortcuts to the hunt board that issues each bill.
	/// Everything here is user-triggered: one click starts one trip and it can be cancelled at
	/// any point. Nothing runs on its own, and nothing here fights anything.
	/// </summary>
	private void DrawHuntAssist() {
		var controller = System.HuntAssistController;

		ImGuiTweaks.Header(Strings.HuntAssistSectionTitle);
		using var indent = ImRaii.PushIndent();

		ImGui.TextWrapped(Strings.HuntAssistHelp);
		ImGuiHelpers.ScaledDummy(5.0f);

		if (controller.StatusText.Length > 0) {
			var statusColor = controller.IsRunning ? KnownColor.Orange.Vector() : KnownColor.Gray.Vector();
			ImGui.TextColored(statusColor, controller.StatusText);
		}

		if (controller.IsRunning) {
			if (ImGui.Button(Strings.HuntAssistCancel, new Vector2(ImGui.GetContentRegionAvail().X, 23.0f * ImGuiHelpers.GlobalScale))) {
				controller.Cancel();
			}
		}

		if (SupportsAutoContinue && chain.IsActive) {
			ImGui.TextColored(KnownColor.Orange.Vector(), chain.Phase is HuntChainPhase.Settling ? Strings.HuntChainSettling : Strings.HuntChainActive);
			if (ImGui.Button(Strings.HuntChainStop, new Vector2(ImGui.GetContentRegionAvail().X, 23.0f * ImGuiHelpers.GlobalScale))) {
				StopChainByUser();
			}
		}

		ImGuiHelpers.ScaledDummy(5.0f);

		// Daily and weekly each draw their own table; one shared id would collide if the window
		// ever showed both at once.
		using var table = ImRaii.Table($"hunt_assist_table_{ModuleName}", 3, ImGuiTableFlags.SizingStretchProp);
		if (!table) return;

		ImGui.TableSetupColumn("##version", ImGuiTableColumnFlags.WidthStretch, 0.5f);
		ImGui.TableSetupColumn("##bill", ImGuiTableColumnFlags.WidthStretch, 2.0f);
		ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthStretch, 2.0f);

		var huntData = MobHunt.Instance();

		// RowId 在設定清單裡不重複，所以不穩定排序與 OrderBy 的結果相同。
		sortedConfigs.Clear();
		sortedConfigs.AddRange(Config.TaskConfig.ConfigList);
		sortedConfigs.Sort(ByRowId);

		foreach (var config in sortedConfigs) {
			// Daily and weekly bills of one expansion are issued by the same boards, so they share
			// this key - including the position the player recorded for it.
			var boardGroupKey = HuntBoards.GetBoardGroupKey(config.RowId);
			if (boardGroupKey is 0) continue;

			HuntBoards.FillBoards(config.RowId, rowBoards);
			var boards = rowBoards;

			ImGui.TableNextColumn();
			var version = boards.Count > 0 ? boards[0].ExpansionVersion : 0;
			if (version > 0) {
				ImGui.TextUnformatted($"{version}.x");
			}
			else {
				ImGui.TextUnformatted("-");
			}

			ImGui.TableNextColumn();
			ImGui.TextUnformatted(config.Label());

			var complete = FindTaskData(config.RowId)?.Complete ?? false;
			var obtained = huntData is not null && huntData->IsMarkBillObtained((int) config.RowId);

			if (complete) {
				ImGui.TextColored(KnownColor.Green.Vector(), Strings.Complete);
			}
			else {
				ImGui.TextColored(obtained ? KnownColor.Green.Vector() : KnownColor.Orange.Vector(),
					obtained ? Strings.HuntAssistBillObtained : Strings.HuntAssistBillAvailable);
			}

			// Only the weekly elite bill names a single mark; resolved lazily so we do not touch
			// the sheets for bills that have not been accepted.
			var targetInfo = SupportsTargetAssist && !complete && obtained ? HuntTargets.GetCurrentTarget(config.RowId) : null;
			if (targetInfo is not null) {
				ImGui.TextColored(KnownColor.Gray.Vector(), $"{targetInfo.Name} - {targetInfo.ZoneName}");
			}

			// Only drawn while actionable: with the bill in hand the board has nothing left to give.
			// Both flags are needed - `obtained` is the game's own MobHunt state and goes false again
			// once the kill is turned in, so on its own it would bring the buttons back for the rest
			// of the period; `complete` is our own tracking and does not.
			var showBoardButtons = !obtained && !complete;

			ImGui.TableNextColumn();

			var recorded = System.TravelAssistConfig.HuntBoardPositions.GetValueOrDefault(boardGroupKey);

			// "We do not know where this board is" has to be readable in the row itself, with the
			// way to fix it right next to it - a tooltip would hide the fact there is a problem.
			if (showBoardButtons && boards.Count is 0 && recorded is null) {
				ImGui.TextColored(KnownColor.Orange.Vector(), Strings.HuntAssistNoBoardData);
				ImGui.TextColored(KnownColor.Orange.Vector(), Strings.HuntAssistNotRecorded);
				DrawRecordButtons(GetRecordButtonLabels(config.RowId), boardGroupKey, null);
				continue;
			}

			using var disabled = ImRaii.Disabled(controller.IsRunning);

			if (showBoardButtons) {
				// A recorded spot wins over the sheets: the player was standing at the board.
				if (recorded is not null) {
					if (ImGui.Button($"{Strings.HuntAssistRecordedPosition}##hunt_recorded_{config.RowId}")) {
						var destination = HuntBoards.BuildFromPosition(recorded.TerritoryId, recorded.ToVector3());
						if (controller.GoToRecordedHuntBoard(destination, config.RowId)) ArmChain("告示板（記錄的位置）", config.RowId);
					}

					if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
						SetTooltipInDisabled(Strings.HuntAssistRecordedTooltip);
					}
				}
				else {
					foreach (var board in boards) {
						var label = board.ZoneName.Length > 0 ? board.ZoneName : Strings.HuntAssistGoToBoard;

						// Several daily rows share one board, so the row has to be part of the id.
						if (ImGui.Button($"{label}##hunt_board_{config.RowId}_{board.EObjectId}")) {
							if (controller.GoToHuntBoard(board, config.RowId)) ArmChain("告示板", config.RowId);
						}

						if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
							SetTooltipInDisabled(Strings.HuntAssistGoToBoard);
						}
					}
				}

				DrawRecordButtons(GetRecordButtonLabels(config.RowId), boardGroupKey, recorded);
			}

			if (targetInfo is null) {
				// The bill is held but we could not work out what it wants, and the board button
				// is gone - without this the cell would just be blank, which reads as "nothing
				// to do here" rather than "we do not know".
				if (SupportsTargetAssist && !complete && obtained) {
					ImGui.TextColored(KnownColor.Orange.Vector(), Strings.HuntAssistNoTargetData);
				}

				continue;
			}

			// These two are complementary, and always exactly one of them applies: the trip to
			// the zone only means anything from outside it, the patrol only from inside it.
			// Drawing both and refusing on click told the user nothing they could not have been
			// shown up front.
			if (Service.ClientState.TerritoryType != targetInfo.TerritoryId) {
				if (ImGui.Button($"{Strings.HuntAssistGoToTarget}##hunt_target_{config.RowId}")) {
					if (controller.GoToTargetZone(targetInfo, config.RowId, System.HuntAssistConfig.PatrolAfterZoneTeleport)) ArmChain("前往目標地圖", config.RowId);
				}

				if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
					var rankLabel = targetInfo.Rank switch {
						HuntSpawnPoints.MarkRank.A => Strings.HuntAssistRankA,
						HuntSpawnPoints.MarkRank.S => Strings.HuntAssistRankS,
						_ => Strings.HuntAssistRankB,
					};

					var tooltip = $"{targetInfo.Name} ({rankLabel})\n{targetInfo.ZoneName}";
					if (targetInfo.AetheryteName.Length > 0) tooltip += $" - {targetInfo.AetheryteName}";
					if (targetInfo.AetheryteChosenFromRoute) tooltip += $"\n{Strings.HuntAssistTargetTooltip}";
					if (System.HuntAssistConfig.PatrolAfterZoneTeleport) tooltip += $"\n{Strings.HuntAssistGoToTargetThenPatrol}";

					SetTooltipInDisabled(tooltip);
				}

				continue;
			}

			if (ImGui.Button($"{Strings.HuntAssistPatrol}##hunt_patrol_{config.RowId}")) {
				if (controller.StartPatrol(targetInfo, config.RowId)) ArmChain("巡邏重生點", config.RowId);
			}

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
				SetTooltipInDisabled(Strings.HuntAssistPatrolTooltip);
			}
		}

		table.Dispose();
		sortedConfigs.Clear();
		rowBoards.Clear();

		DrawAssistSettings();
	}

	private LuminaTaskData<MobHuntOrderType>? FindTaskData(uint rowId) {
		foreach (var entry in Data.TaskData.DataList) {
			if (entry.RowId == rowId) return entry;
		}

		return null;
	}

	// 與 ImGuiComponents.IconButton(id, icon) 組出來的標籤逐字相同，只是每一列組一次。
	private (string Record, string Clear) GetRecordButtonLabels(uint uiKey) {
		if (!recordButtonLabels.TryGetValue(uiKey, out var labels)) {
			labels = ($"{FontAwesomeIcon.MapPin.ToIconString()}##hunt_record_{uiKey}", $"{FontAwesomeIcon.Trash.ToIconString()}##hunt_clear_{uiKey}");
			recordButtonLabels[uiKey] = labels;
		}

		return labels;
	}

	/// <summary>
	/// Manual fallback for a board the game sheets cannot place (or place unhelpfully): the
	/// player stands at it and records the spot, and "take me there" uses that from then on.
	/// </summary>
	/// <param name="labels">Icon button labels of this bill row; the row is part of the ImGui id, so each row gets its own buttons.</param>
	/// <param name="boardGroupKey">Where the position is stored; shared by every bill of one board.</param>
	private static void DrawRecordButtons((string Record, string Clear) labels, uint boardGroupKey, RecordedPosition? recorded) {
		if (ImGuiComponents.IconButton(labels.Record)) {
			RecordCurrentPosition(boardGroupKey);
		}

		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
			SetTooltipInDisabled(Strings.HuntAssistRecordTooltip);
		}

		if (recorded is null) return;

		ImGui.SameLine();

		if (ImGuiComponents.IconButton(labels.Clear)) {
			System.TravelAssistConfig.HuntBoardPositions.Remove(boardGroupKey);
			System.TravelAssistConfig.Save();
		}

		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
			SetTooltipInDisabled(Strings.HuntAssistClearRecorded);
		}
	}

	// 停用區段裡（BeginDisabled 把 Style.Alpha 乘上 DisabledAlpha）打開的說明框會跟著變淡，畫說明時把 Alpha 推回 1。
	private static void SetTooltipInDisabled(string text) {
		using var tooltipAlpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1.0f);
		ImGui.SetTooltip(text);
	}

	private static void RecordCurrentPosition(uint boardGroupKey) {
		if (Service.ObjectTable.LocalPlayer is not { } player) return;

		// Copied out on the spot - the wrapper this came from is reused across frames.
		System.TravelAssistConfig.HuntBoardPositions[boardGroupKey] = new RecordedPosition {
			TerritoryId = Service.ClientState.TerritoryType,
			X = player.Position.X,
			Y = player.Position.Y,
			Z = player.Position.Z,
		};

		System.TravelAssistConfig.Save();
	}
}