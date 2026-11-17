using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Lumina.Excel;
using DailyDuty.Localization;
using Lumina.Excel.Sheets;

namespace DailyDuty.Models;

public class LuminaTaskDataList<T> : ICollection<LuminaTaskData<T>> where T : struct, IExcelRow<T> {
	public List<LuminaTaskData<T>> DataList = [];

	// Implement ICollection
	public IEnumerator<LuminaTaskData<T>> GetEnumerator() => DataList.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	public void Add(LuminaTaskData<T> item) => DataList.Add(item);
	public void Clear() => DataList.Clear();
	public bool Contains(LuminaTaskData<T> item) => DataList.Contains(item);
	public void CopyTo(LuminaTaskData<T>[] array, int arrayIndex) => DataList.CopyTo(array, arrayIndex);
	public bool Remove(LuminaTaskData<T> item) => DataList.Remove(item);
	public int Count => DataList.Count;
	public bool IsReadOnly => false;
	// End ICollection
    
	public void Draw() {
		if (ImGui.BeginTable("##TaskDataTable", 2, ImGuiTableFlags.SizingStretchSame)) {
			switch (this) {
				case LuminaTaskDataList<ContentsNote>:
				case LuminaTaskDataList<ContentRoulette>:
				case LuminaTaskDataList<ClassJob>:
				case LuminaTaskDataList<MobHuntOrderType>:
				case LuminaTaskDataList<Addon>:
					DrawStandardDataList();
					break;
                
				case LuminaTaskDataList<ContentFinderCondition>:
					DrawContentFinderCondition();
					break;
                
				default:
					ImGui.TableNextColumn();
					ImGui.Text("Invalid Data Type");
					break;
			}
            
			ImGui.EndTable();
		}
	}

	public void Sort() 
		=> DataList = DataList.OrderBy(e => e.RowId).ToList();

	private void DrawStandardDataList() {
		foreach (var dataEntry in DataList) {
			ImGui.TableNextColumn();
			if ((dataEntry.DisplayLabel ??= GetRowLabel(dataEntry.RowId)) is { } label) {
				ImGui.Text(label);
			}
            
			ImGui.TableNextColumn();
			var color = dataEntry.Complete ? KnownColor.Green.Vector() : KnownColor.Orange.Vector();
			var text = dataEntry.Complete ? Strings.Complete : Strings.Incomplete;
			ImGui.TextColored(color, text);
		}
	}
    
	// 資料清單只增不減，跨版本殘留的列號可能已不在表裡：查不到就顯示「?<id>」（與 LuminaTaskConfig.Label() 同一套退路），不擲例外。
	private string? GetRowLabel(uint rowId) {
		var missing = $"?{rowId}";

		switch (this) {
			case LuminaTaskDataList<ContentsNote>:
				return Service.DataManager.GetExcelSheet<ContentsNote>().GetRowOrDefault(rowId)?.Name.ToString() ?? missing;

			case LuminaTaskDataList<ContentRoulette>:
				return Service.DataManager.GetExcelSheet<ContentRoulette>().GetRowOrDefault(rowId)?.Name.ToString() ?? missing;

			case LuminaTaskDataList<ClassJob>:
				if (Service.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(rowId) is not { } classJob) return missing;
				return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(classJob.Name.ToString());

			case LuminaTaskDataList<MobHuntOrderType>:
				if (Service.DataManager.GetExcelSheet<MobHuntOrderType>().GetRowOrDefault(rowId) is not { } mobHuntOrderType) return missing;
				if (mobHuntOrderType.EventItem.ValueNullable is not { } eventItem) return missing;

				var eventItemName = eventItem.Name.ToString();
				if (eventItemName == string.Empty) eventItemName = eventItem.Singular.ToString();

				return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(eventItemName);

			case LuminaTaskDataList<Addon>:
				return Service.DataManager.GetExcelSheet<Addon>().GetRowOrDefault(rowId)?.Text.ToString() ?? missing;

			case LuminaTaskDataList<ContentFinderCondition>:
				return Service.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(rowId)?.Name.ToString() ?? missing;

			default:
				return null;
		}
	}

	private void DrawContentFinderCondition() {
		ImGui.TableNextColumn();
		ImGui.TextColored(KnownColor.Gray.Vector(), Strings.DutyName);

		ImGui.TableNextColumn();
		ImGui.TextColored(KnownColor.Gray.Vector(), Strings.CurrentNumDrops);
                            
		if (DataList.Count > 0) {
			foreach (var data in DataList) {
				ImGui.TableNextColumn();
				ImGui.Text(data.DisplayLabel ??= GetRowLabel(data.RowId)!);

				ImGui.TableNextColumn();
				ImGui.Text($"{data.CurrentCount}");
			}
		}
		else {
			ImGui.TableNextColumn();
			ImGui.TextColored(KnownColor.Orange.Vector(), Strings.NothingToTrack);
		}
	}

	public void Update(ref bool dataChanged, Func<uint, bool> getTaskStatusFunction) {
		foreach (var task in DataList) {
			var status = getTaskStatusFunction(task.RowId);

			if (task.Complete != status) {
				task.Complete = status;
				dataChanged = true;
			}
		}
	}

	public void Reset() {
		foreach (var task in DataList) {
			task.Complete = false;
			task.CurrentCount = 0;
		}
	}
}