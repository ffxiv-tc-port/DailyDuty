using System.Collections.Generic;
using System.Linq;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace DailyDuty.Modules.BaseModules;

public class RaidsConfig : ModuleTaskConfig<ContentFinderCondition> {
	public bool ClickableLink = true;
	
	protected override void DrawModuleConfig() {
		ConfigChanged |= ImGui.Checkbox(Strings.ClickableLink, ref ClickableLink);
		
		ImGuiHelpers.ScaledDummy(5.0f);
		
		base.DrawModuleConfig();
	}
}

public abstract unsafe class RaidsBase : Modules.WeeklyTask<ModuleTaskData<ContentFinderCondition>, RaidsConfig, ContentFinderCondition> {
	protected override ModuleStatus GetModuleStatus() => IncompleteTaskCount == 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;
	private static AgentContentsFinder* Agent => AgentContentsFinder.Instance();
	
	public override bool HasClickableLink => Config.ClickableLink;

	protected abstract List<ContentFinderCondition> RaidDuties { get; set; }

	protected RaidsBase() {
		Service.GameInventory.ItemAdded += OnItemEvent;
		Service.GameInventory.ItemChanged += OnItemEvent;
	}

	public override void Dispose() {
		Service.GameInventory.ItemAdded -= OnItemEvent;
		Service.GameInventory.ItemChanged -= OnItemEvent;
	}

	protected override void UpdateTaskLists() {
		CheckForDutyListUpdate(RaidDuties);
	}

	public override void Update() {
		if (Agent is not null && Agent->IsAgentActive()) {
			var selectedDuty = Agent->SelectedDuty.Id;
			var task = Data.TaskData.FirstOrDefault(task => task.RowId == selectedDuty);
			var numRewards = Agent->NumCollectedRewards;
            
			if (task is not null && task.CurrentCount != numRewards) {
				task.CurrentCount = numRewards;
				DataChanged = true;
			}
		}
        
		base.Update();
	}

	public override void Reset() {
		Data.TaskData.Reset();
        
		base.Reset();
	}

	private void OnItemEvent(GameInventoryEvent type, InventoryEventArgs data) {
		// If the item event is not for main inventory, we don't care.
		if (data.Item.ContainerType is not (GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or GameInventoryType.Inventory3 or GameInventoryType.Inventory4)) return;
		
		// If we are not in a tracked zone, return
		if (GetDataForCurrentZone() is not { } trackedRaid) return;

		// If we can't get the exd data for this item, return
		// ⚠️ data.Item.ItemId 是「已套用旗標」的原始 id：HQ 會 +1,000,000、收藏品 +500,000
		//    （Dalamud 的 GameInventoryItem.ItemId 走 InventoryItem.GetItemId()「with flags applied」；
		//      正規化過的那顆叫 BaseItemId）。台服 Item 表只有 0..49200 且無空洞，
		//      所以在追蹤中的團隊任務區域裡收到任何 HQ／收藏品的 ItemAdded/ItemChanged，
		//      舊寫法的 GetRow 會擲 ArgumentOutOfRangeException，而下一行的 `item.RowId is 0`
		//      根本等不到——那個守衛從一開始就檢查了錯的東西。
		//    這裡刻意維持「查不到就跳過」＝與原本被例外中斷後的實際結果相同（計數不變），
		//    只是不再擲例外洗 log。⚠️ 不可改用 BaseItemId：那會讓 HQ 製作裝備開始被算進
		//    掉落計數（ItemUICategory 34~38 會命中），屬於回退既有行為。
		if (!Service.DataManager.GetExcelSheet<Item>().TryGetRow(data.Item.ItemId, out var item)) return;
		if (item.RowId is 0) return;
		
		Service.Log.Debug($"InventoryEvent: {type}: {item.Name}");

		// If the item is a limited type that we care about, increment the current count
		switch (item.ItemUICategory.RowId) {
			case 34: // Head
			case 35: // Body
			case 36: // Legs
			case 37: // Hands
			case 38: // Feet
			case 61 when item.ItemAction.RowId == 0: // Miscellany with no itemAction
				trackedRaid.CurrentCount += 1;
				DataChanged = true;
				break;
		}
	}
    
	private LuminaTaskData<ContentFinderCondition>? GetDataForCurrentZone()
		=> Data.TaskData.FirstOrDefault(task => task.RowId == GameMain.Instance()->CurrentContentFinderConditionId);

	private bool IsDataStale(ICollection<ContentFinderCondition> dutyList) {
		// Are there any new duties that we might need to add?
		var newDutiesAvailable = dutyList.Any(duty => !Data.TaskData.Any(task => task.RowId == duty.RowId));
		
		// Are there any duties that we might have that we need to remove?
		var tooManyDuties = false;
		
		// Check every duty in saved task data
		foreach (var taskDataDuty in Data.TaskData) {
			
			// If this duty doesn't match ANY of the new duties, then we have too many.
			if (!dutyList.Any(newDuty => newDuty.RowId == taskDataDuty.RowId)) {
				tooManyDuties = true;
			}
			// else this duty is in the list, it's good to keep.
		}
		
		return newDutiesAvailable || tooManyDuties;
	}

	private void CheckForDutyListUpdate(List<ContentFinderCondition> dutyList) { 
		if (IsDataStale(dutyList) || Config.TaskConfig.Count is 0 || Data.TaskData.Count is 0) {
			Config.TaskConfig.Clear();
			Data.TaskData.Clear();
	
			foreach (var duty in dutyList) {
				Config.TaskConfig.Add(new LuminaTaskConfig<ContentFinderCondition> {
					RowId = duty.RowId,
					Enabled = false,
					TargetCount = 0,
				});
			             
				Data.TaskData.Add(new LuminaTaskData<ContentFinderCondition> {
					RowId = duty.RowId,
					Complete = false,
					CurrentCount = 0,
				});
			             
				SaveConfig();
				SaveData();
			}
		}
	}
}