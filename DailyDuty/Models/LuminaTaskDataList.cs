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
    
	// 查不到列時照舊擲例外（不記住），與每幀現查的行為相同。
	private string? GetRowLabel(uint rowId) {
		switch (this) {
			case LuminaTaskDataList<ContentsNote>:
				return Service.DataManager.GetExcelSheet<ContentsNote>().GetRow(rowId).Name.ToString();

			case LuminaTaskDataList<ContentRoulette>:
				return Service.DataManager.GetExcelSheet<ContentRoulette>().GetRow(rowId).Name.ToString();

			case LuminaTaskDataList<ClassJob>:
				var classJob = Service.DataManager.GetExcelSheet<ClassJob>().GetRow(rowId);
				return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(classJob.Name.ToString());

			case LuminaTaskDataList<MobHuntOrderType>:
				var mobHuntOrderType = Service.DataManager.GetExcelSheet<MobHuntOrderType>().GetRow(rowId);
				var eventItemName = mobHuntOrderType.EventItem.Value.Name.ToString();
				if (eventItemName == string.Empty) eventItemName = mobHuntOrderType.EventItem.Value.Singular.ToString();

				return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(eventItemName);

			case LuminaTaskDataList<Addon>:
				return Service.DataManager.GetExcelSheet<Addon>().GetRow(rowId).Text.ToString();

			case LuminaTaskDataList<ContentFinderCondition>:
				return Service.DataManager.GetExcelSheet<ContentFinderCondition>().GetRow(rowId).Name.ToString();

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