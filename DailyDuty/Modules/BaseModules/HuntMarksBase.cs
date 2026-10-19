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
    
	public override void Update() {
		// 直接走底下的 List：經 TaskData 自己的 GetEnumerator() 每次都會裝箱一個列舉器。
		foreach (var task in Data.TaskData.DataList) {
			// If we have the active mark bill
			var availableMarkId = HuntData->GetAvailableHuntOrderRowId((byte) task.RowId);
			var obtainedMarkId = HuntData->GetObtainedHuntOrderRowId((byte) task.RowId);
                
			if (availableMarkId == obtainedMarkId && !task.Complete) {
				var orderData = Service.DataManager.GetExcelSheet<MobHuntOrderType>().GetRow(task.RowId);
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
        
		base.Update();
	}

	private bool IsEliteMarkComplete(uint targetRow, uint markId) {
		var eliteTargetInfo = Service.DataManager.GetSubrowExcelSheet<MobHuntOrder>().GetSubrow(targetRow, 0);

		return HuntData->CurrentKills[(int) markId][0] == eliteTargetInfo.NeededKills;
	}

	private bool IsNormalMarkComplete(uint targetRow, uint markId) {
		for (var index = 0; index < 5; index++) {
			var huntData = Service.DataManager.GetSubrowExcelSheet<MobHuntOrder>().GetSubrow(targetRow, (ushort) index);

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

	/// <summary>
	/// 模組層級的「帶我去」＝追蹤中、還沒完成、也還沒接的第一張單（表格順序）的告示板，
	/// 目的地是表格那一列按鈕的其中一個。已經接了的單要去的是標的不是告示板，不算。
	/// </summary>
	public override bool HasTravelDestination => TryGetTravelBill(out _, out _);

	public override bool StartTravel() {
		if (!TryGetTravelBill(out var orderTypeRowId, out var boardGroupKey)) return false;

		var controller = System.HuntAssistController;

		// A recorded spot wins over the sheets, same as the table.
		if (System.TravelAssistConfig.HuntBoardPositions.GetValueOrDefault(boardGroupKey) is { } recorded) {
			var destination = HuntBoards.BuildFromPosition(recorded.TerritoryId, recorded.ToVector3());
			return controller.GoToLocation(destination, Strings.TravelAssistStatusTeleporting, Strings.HuntAssistBoard);
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
						controller.GoToLocation(destination, Strings.TravelAssistStatusTeleporting, Strings.HuntAssistBoard);
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
							controller.GoToHuntBoard(board, config.RowId);
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
					controller.GoToTargetZone(targetInfo, config.RowId);
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

					SetTooltipInDisabled(tooltip);
				}

				continue;
			}

			if (ImGui.Button($"{Strings.HuntAssistPatrol}##hunt_patrol_{config.RowId}")) {
				controller.StartPatrol(targetInfo, config.RowId);
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