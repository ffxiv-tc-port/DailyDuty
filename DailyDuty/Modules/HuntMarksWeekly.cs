using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using DailyDuty.Classes;
using DailyDuty.Classes.HuntAssist;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using KamiLib.Classes;
using Lumina.Excel.Sheets;

namespace DailyDuty.Modules;

public class HuntMarksWeekly : HuntMarksBase {
	public override ModuleName ModuleName => ModuleName.HuntMarksWeekly;

	public override ModuleType ModuleType => ModuleType.Weekly;

	public override DateTime GetNextReset() => Time.NextWeeklyReset() + TimeSpan.FromMinutes(1);

	protected override void UpdateTaskLists() {
		var luminaUpdater = new LuminaTaskUpdater<MobHuntOrderType>(this, order => order.Type is 2);
		luminaUpdater.UpdateConfig(Config.TaskConfig);
		luminaUpdater.UpdateData(Data.TaskData);
	}

	public override void DrawData() {
		base.DrawData();

		DrawHuntAssist();
	}

	/// <summary>
	/// Per-expansion shortcuts to the hunt board that issues each weekly elite bill.
	/// Everything here is user-triggered: one click starts one trip and it can be cancelled at
	/// any point. Nothing runs on its own, and nothing here fights anything.
	/// </summary>
	private unsafe void DrawHuntAssist() {
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

		using var table = ImRaii.Table("hunt_assist_table", 3, ImGuiTableFlags.SizingStretchProp);
		if (!table) return;

		ImGui.TableSetupColumn("##version", ImGuiTableColumnFlags.WidthStretch, 0.5f);
		ImGui.TableSetupColumn("##bill", ImGuiTableColumnFlags.WidthStretch, 2.0f);
		ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthStretch, 2.0f);

		var huntData = MobHunt.Instance();

		foreach (var config in Config.TaskConfig.ConfigList.OrderBy(entry => entry.RowId)) {
			if (!HuntBoards.IsWeeklyOrderTypeSupported(config.RowId)) continue;

			var boards = HuntBoards.GetBoards(config.RowId);

			ImGui.TableNextColumn();
			var version = boards.Count > 0 ? boards[0].ExpansionVersion : 0;
			ImGui.TextUnformatted(version > 0 ? $"{version}.x" : "-");

			ImGui.TableNextColumn();
			ImGui.TextUnformatted(config.Label());

			var complete = Data.TaskData.DataList.FirstOrDefault(entry => entry.RowId == config.RowId)?.Complete ?? false;
			var obtained = huntData is not null && huntData->IsMarkBillObtained((int) config.RowId);

			if (complete) {
				ImGui.TextColored(KnownColor.Green.Vector(), Strings.Complete);
			}
			else {
				ImGui.TextColored(obtained ? KnownColor.Green.Vector() : KnownColor.Orange.Vector(),
					obtained ? Strings.HuntAssistBillObtained : Strings.HuntAssistBillAvailable);
			}

			// Once the bill is in hand the board is no longer interesting - what matters is
			// where the mark lives. Resolve it lazily so we do not touch the sheets for bills
			// that have not been accepted.
			var targetInfo = complete || !obtained ? null : HuntTargets.GetCurrentTarget(config.RowId);
			if (targetInfo is not null) {
				ImGui.TextColored(KnownColor.Gray.Vector(), $"{targetInfo.Name} - {targetInfo.ZoneName}");
			}

			// Buttons are only drawn while they are actually actionable. Once the bill is in hand
			// the board has nothing left to give, and once the week's mark is done neither does it -
			// a shortcut that always fails is worse than no shortcut, and a greyed-out one still
			// costs a row of screen space to say nothing.
			// `complete` is our weekly tracking and `obtained` is the game's own MobHunt state:
			// after the kill is turned in the bill is no longer held, so `obtained` alone would let
			// the board buttons come back for the rest of the week.
			var showBoardButtons = !obtained && !complete;

			ImGui.TableNextColumn();

			// Missing board data is only worth saying while the board is still the next step.
			if (showBoardButtons && boards.Count is 0) {
				ImGui.TextColored(KnownColor.Orange.Vector(), Strings.HuntAssistNoBoardData);
				continue;
			}

			using var disabled = ImRaii.Disabled(controller.IsRunning);

			if (showBoardButtons) {
				foreach (var board in boards) {
					var label = board.ZoneName.Length > 0 ? board.ZoneName : Strings.HuntAssistGoToBoard;

					if (ImGui.Button($"{label}##hunt_board_{board.EObjectId}")) {
						controller.GoToHuntBoard(board, config.RowId);
					}

					if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
						ImGui.SetTooltip(Strings.HuntAssistGoToBoard);
					}
				}
			}

			if (targetInfo is null) {
				// The bill is held but we could not work out what it wants, and the board button
				// is gone - without this the cell would just be blank, which reads as "nothing
				// to do here" rather than "we do not know".
				if (!complete && obtained) {
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

					ImGui.SetTooltip(tooltip);
				}

				continue;
			}

			if (ImGui.Button($"{Strings.HuntAssistPatrol}##hunt_patrol_{config.RowId}")) {
				controller.StartPatrol(targetInfo, config.RowId);
			}

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
				ImGui.SetTooltip(Strings.HuntAssistPatrolTooltip);
			}
		}

		table.Dispose();

		DrawPatrolSettings();
	}

	private static void DrawPatrolSettings() {
		var config = System.HuntAssistConfig;
		var configChanged = false;

		ImGuiTweaks.Header(Strings.HuntAssistPatrolSettings);
		using var indent = ImRaii.PushIndent();

		configChanged |= ImGui.Checkbox(Strings.HuntAssistUseFlying, ref config.UseFlying);

		ImGui.TextUnformatted(Strings.HuntAssistDetectionRadius);
		ImGuiComponents.HelpMarker(Strings.HuntAssistDetectionRadiusHelp);

		var radius = config.DetectionRadius;
		ImGuiTweaks.SetFullWidth();
		if (ImGui.SliderFloat("##DetectionRadius", ref radius, HuntAssistConfig.MinimumDetectionRadius, HuntAssistConfig.MaximumDetectionRadius, "%.0f")) {
			config.DetectionRadius = radius;
		}

		// SliderFloat reports true every frame while dragging - only persist once it settles.
		if (ImGui.IsItemDeactivatedAfterEdit()) configChanged = true;

		if (configChanged) config.Save();
	}
}
