using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using DailyDuty.Classes;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using DailyDuty.Windows;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using KamiLib.Classes;
using KamiLib.Extensions;
using KamiToolKit.Classes;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;
using Lumina.Excel.Sheets;
using InstanceContent = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent;
using SeStringBuilder = Lumina.Text.SeStringBuilder;

namespace DailyDuty.Modules;

public class DutyRouletteData : ModuleTaskData<ContentRoulette> {
    public int ExpertTomestones;
    public int ExpertTomestoneCap;
    public bool AtTomeCap;

    protected override void DrawModuleData() {
        DrawDataTable(
            (Strings.CurrentWeeklyTomestones, ExpertTomestones.ToString()),
            (Strings.WeeklyTomestoneLimit, ExpertTomestoneCap.ToString()),
            (Strings.AtWeeklyTomestoneLimit, AtTomeCap.ToString())
        );
        
        base.DrawModuleData();
    }
}

public class DutyRouletteConfig : ModuleTaskConfig<ContentRoulette> {
    public bool CompleteWhenCapped;
    public bool ClickableLink = true;
    public bool ColorContentFinder = true;
    public Vector4 CompleteColor = KnownColor.LimeGreen.Vector();
    public Vector4 IncompleteColor = KnownColor.OrangeRed.Vector();
    public bool ShowOpenDailyDutyButton = true;
    public bool ShowResetTimer = true;
    public Vector4 TimerColor = KnownColor.Black.Vector();
    
    protected override void DrawModuleConfig() {
        ConfigChanged |= ImGui.Checkbox(Strings.ClickableLink, ref ClickableLink);
        ConfigChanged |= ImGui.Checkbox(Strings.CompleteWhenTomeCapped, ref CompleteWhenCapped);
        ConfigChanged |= ImGui.Checkbox(Strings.ShowOpenDailyDutyButton, ref ShowOpenDailyDutyButton);

        ImGui.Spacing();

        ConfigChanged |= ImGui.Checkbox(Strings.ShowDailyResetTimerInDutyFinder, ref ShowResetTimer);

        if (ShowResetTimer) {
            ConfigChanged |= ImGuiTweaks.ColorEditWithDefault("Timer Color", ref TimerColor, ColorHelper.GetColor(7));
        }
        
        ImGui.Spacing();

        ConfigChanged |= ImGui.Checkbox(Strings.ColorDutyFinder, ref ColorContentFinder);
        
        if (ColorContentFinder) {
            ImGuiHelpers.ScaledDummy(5.0f);

            ConfigChanged |= ImGuiTweaks.ColorEditWithDefault("Complete Color", ref CompleteColor, KnownColor.LimeGreen.Vector());
            ConfigChanged |= ImGuiTweaks.ColorEditWithDefault("Incomplete Color", ref IncompleteColor, KnownColor.OrangeRed.Vector());
        }
        
        ImGuiHelpers.ScaledDummy(5.0f);
        
        base.DrawModuleConfig();
    }
}

public unsafe class DutyRoulette : BaseModules.Modules.DailyTask<DutyRouletteData, DutyRouletteConfig, ContentRoulette> {
    public override ModuleName ModuleName => ModuleName.DutyRoulette;

    public override bool HasClickableLink => Config.ClickableLink;
    
    public override PayloadId ClickableLinkPayloadId => PayloadId.OpenDutyFinderRoulette;

    public override bool HasTooltip => true;

    private TextNode? infoTextNode;
    private TextButtonNode? openDailyDutyButton;
    private TextNode? dailyResetTimer;

    private Hook<AtkComponentListItemPopulator.PopulateDelegate>? onDutyListPopulate;
    private readonly List<uint> modifiedIndexes = [];
    
    public DutyRoulette() {
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsFinder", OnContentsFinderSetup); 
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContentsFinder", OnContentsFinderFinalize);
        
        System.ContentsFinderController.OnAttach += AttachNodes;
        System.ContentsFinderController.OnDetach += DetachNodes;
        System.ContentsFinderController.OnUpdate += OnContentFinderUpdate;
    }

    public override void Dispose() {
        Service.AddonLifecycle.UnregisterListener(OnContentsFinderSetup, OnContentsFinderFinalize);
        
        System.ContentsFinderController.OnAttach -= AttachNodes;
        System.ContentsFinderController.OnDetach -= DetachNodes;
        System.ContentsFinderController.OnUpdate -= OnContentFinderUpdate;
        
        onDutyListPopulate?.Dispose();
        
        base.Dispose();
    }
    
    // 🔴 這條鏈有三跳，原本一跳都沒判。
    //    ① args.GetAddon<T>() 展開成 (T*)args.Addon.Address。這一跳**是安全的**：本處理常式是以
    //       addon 名稱註冊的（RegisterListener(..., "ContentsFinder", ...)），而 Dalamud 的
    //       AddonLifecycle.InvokeListenersSafely 對具名 listener 會先跑 args.IsAddon(name)，
    //       該方法在 Addon.IsNull 時直接回 false ⇒ 具名 listener 根本不會被叫到。所以不加判空。
    //    ② addon->DutyList 是 AtkComponentTreeList*（AddonContentsFinder +0x358）——純指標欄位，
    //       尚未建好時是 null。拿 null 當 this 呼叫 [MemberFunction] GetItemRendererByNodeId 會在
    //       原生端解參考 → AccessViolationException。
    //    ③ GetItemRendererByNodeId(6) 是照 node id 找項目算繪器的原生查詢，**找不到就回 null**
    //       （清單項目還沒填好時是常態），接著 ->Populator 就是對 null 解參考。
    //    AVE 在 .NET Core 是 corrupted-state exception，try/catch 與任何例外隔離包裝一律攔不到，
    //    只能事前擋。
    //    失敗語意：這是「開任務搜尋器」的回呼路徑、不是每幀路徑，所以記一行 Warning（使用者跑
    //    LogLevel 2 ⇒ Information 以上都收得到）後放棄掛 hook。本模組退成「不上色任務輪盤清單」，
    //    其餘功能不受影響；下次再開任務搜尋器會再觸發一次 PostSetup 重試，與 JumboCactpot 的
    //    CreateReceiveEventHook 同一個 fail-closed 形狀。
    private void OnContentsFinderSetup(AddonEvent type, AddonArgs args) {
        var addon = args.GetAddon<AddonContentsFinder>();

        var dutyList = addon->DutyList;
        if (dutyList is null) {
            Service.Log.Warning("[DutyRoulette] 任務搜尋器的清單元件尚未建立，本次不掛載清單填充 hook。");
            return;
        }

        var itemRenderer = dutyList->GetItemRendererByNodeId(6);
        if (itemRenderer is null) {
            Service.Log.Warning("[DutyRoulette] 找不到任務清單的項目算繪器（node 6），本次不掛載清單填充 hook。");
            return;
        }

        // Populator 是內嵌值型別（+0x128）不是二次指標，itemRenderer 非 null 即可安全取；
        // 但 Populate 本身是函式指標欄位，尚未安裝填充函式時是 null，交給 HookFromAddress 沒有意義。
        var populateMethod = itemRenderer->Populator.Populate;
        if (populateMethod is null) {
            Service.Log.Warning("[DutyRoulette] 任務清單項目算繪器沒有填充函式位址，本次不掛載清單填充 hook。");
            return;
        }

        onDutyListPopulate = Service.Hooker.HookFromAddress<AtkComponentListItemPopulator.PopulateDelegate>(populateMethod, OnPopulateHook);
        onDutyListPopulate?.Enable();
    }
    
    private void OnContentsFinderFinalize(AddonEvent type, AddonArgs args) {
        onDutyListPopulate?.Dispose();
        modifiedIndexes.Clear();
    }

    private void OnPopulateHook(AtkUnitBase* unitBase, AtkComponentListItemPopulator.ListItemInfo* listItemInfo, AtkResNode** nodeList) => HookSafety.ExecuteSafe(() => {
        var index = listItemInfo->ListItem->Renderer->OwnerNode->NodeId;
        
        var dutyName = listItemInfo->ListItem->StringValues[0].ToString();
        var dutyInfo = Service.DataManager.GetExcelSheet<ContentRoulette>().FirstOrNull(roulette => string.Equals(dutyName, roulette.Category.ExtractText(), StringComparison.OrdinalIgnoreCase));

        var dutyNameTextNode = (AtkTextNode*) nodeList[3];
        var levelTextNode = (AtkTextNode*) nodeList[4];

        if (Config.ColorContentFinder) {
            
            // If this is already modified
            if (modifiedIndexes.Contains(index)) {
                
                // And this is not a roulette
                if (dutyInfo is null) {
                    TryResetEntry(index, dutyNameTextNode, levelTextNode);
                }
                // else it is already colored correctly
                
            }
            
            // else, this hasn't been modified, and is a roulette
            else if (dutyInfo is { } roulette) {
                
                // If this roulette is being tracked, apply color
                if (Config.TaskConfig.FirstOrDefault(task => task.RowId == roulette.RowId) is { Enabled: true }) {
                    var isRouletteCompleted = InstanceContent.Instance()->IsRouletteComplete((byte) roulette.RowId);
                    dutyNameTextNode->TextColor = isRouletteCompleted ? Config.CompleteColor.ToByteColor() : Config.IncompleteColor.ToByteColor();

                    modifiedIndexes.Add(index);
                }
                // else leave it unmodified
                
            }
        }
        else {
            TryResetEntry(index, dutyNameTextNode, levelTextNode);
        }
        
        if (infoTextNode is not null) {
            infoTextNode.IsVisible = modifiedIndexes.Count is not 0;
        }
    
        onDutyListPopulate!.OriginalDisposeSafe(unitBase, listItemInfo, nodeList);
    }, Service.Log);

    private void TryResetEntry(uint index, AtkTextNode* nameNode, AtkTextNode* levelNode) {
        if (modifiedIndexes.Contains(index)) {
            nameNode->TextColor = levelNode->TextColor;
            modifiedIndexes.Remove(index);
        }
    }

    private void AttachNodes(AddonContentsFinder* addon) {
        var targetResNode = addon->GetNodeById(56);
        if (targetResNode is null) return;

        infoTextNode = new TextNode {
            NodeId = 1000,
            X = 16.0f,
            Y = targetResNode->GetYFloat() + 2.0f, 
            TextFlags = TextFlags.AutoAdjustNodeSize,
            AlignmentType = AlignmentType.TopLeft,
            Text = GetHintText(),
            Tooltip = Strings.DailyDutyFeatureTooltip,
            EnableEventFlags = true,
            IsVisible = false,
        };
        System.NativeController.AttachNode(infoTextNode, targetResNode, NodePosition.AfterTarget);
        
        openDailyDutyButton = new TextButtonNode {
            Position = new Vector2(50.0f, 622.0f),
            Size = new Vector2(130.0f, 28.0f),
            IsVisible = true,
            Label = Strings.OpenDailyDuty,
        };
        openDailyDutyButton.AddEvent(AddonEventType.ButtonClick, _ => System.WindowManager.GetWindow<ConfigurationWindow>()?.UnCollapseOrToggle() );
        System.NativeController.AttachNode(openDailyDutyButton, addon->RootNode);

        var targetComponent = GetListHeaderComponentNode(addon);
        if (targetComponent is not null) {
            dailyResetTimer = new TextNode {
                Position = new Vector2(targetComponent->X, targetComponent->Y),
                Size = new Vector2(targetComponent->Width, targetComponent->Height),
                AlignmentType = AlignmentType.Center,
                Tooltip = Strings.DailyResetTimerTooltip,
                Text = "0:00:00:00",
                EnableEventFlags = true,
                TextColor = Config.TimerColor,
            };
            System.NativeController.AttachNode(dailyResetTimer, targetComponent);
        }
        
        if (Config.TimerColor == Vector4.Zero) {
            Config.TimerColor = ColorHelper.GetColor(7);
            ConfigChanged = true;
        }
    }

    private void DetachNodes(AddonContentsFinder* addon) {
        System.NativeController.DetachNode(infoTextNode, () => {
            infoTextNode?.Dispose();
            infoTextNode = null;
        });
        
        System.NativeController.DetachNode(openDailyDutyButton, () => {
            openDailyDutyButton?.Dispose();
            openDailyDutyButton = null;
        });
        
        System.NativeController.DetachNode(dailyResetTimer, () => {
            dailyResetTimer?.Dispose();
            dailyResetTimer = null;
        });
    }

    private void OnContentFinderUpdate(AddonContentsFinder* addon) {
        if (openDailyDutyButton is not null) {
            openDailyDutyButton.IsVisible = Config.ShowOpenDailyDutyButton;
        }
        
        if (dailyResetTimer is not null && Config.ShowResetTimer) {
            var nextReset = Time.NextDailyReset();
            var timeRemaining = nextReset - DateTime.UtcNow;
        
            dailyResetTimer.Text = timeRemaining.FormatTimeSpanShort(System.TimersConfig.HideTimerSeconds);
            dailyResetTimer.TextColor = Config.TimerColor;
        }
        
        if (dailyResetTimer is not null) {
            dailyResetTimer.IsVisible = Config.ShowResetTimer && addon->SelectedRadioButton == 0;
        }
    }

    private AtkComponentNode* GetListHeaderComponentNode(AddonContentsFinder* addon)
        => addon->DutyList->CategoryItemRendererList->AtkComponentListItemRenderer->ComponentNode;

    private SeString GetHintText()
        => SeString.Parse(new SeStringBuilder()
            .PushColorRgba(Config.IncompleteColor)
            .Append(Strings.IncompleteTask)
            .PopColor()
            .Append("        ")
            .PushColorRgba(Config.CompleteColor)
            .Append(Strings.CompleteTask)
            .PopColor()
            .ToSeString()
            .RawData);

    protected override void UpdateTaskLists() {
        var luminaUpdater = new LuminaTaskUpdater<ContentRoulette>(this, roulette => roulette.DutyType.ExtractText() != string.Empty);
        luminaUpdater.UpdateConfig(Config.TaskConfig);
        luminaUpdater.UpdateData(Data.TaskData);
    }

    public override void Update() {
        Data.TaskData.Update(ref DataChanged, rowId => InstanceContent.Instance()->IsRouletteComplete((byte) rowId));

        Data.ExpertTomestones = TryUpdateData(Data.ExpertTomestones, InventoryManager.Instance()->GetWeeklyAcquiredTomestoneCount());
        Data.ExpertTomestoneCap = TryUpdateData(Data.ExpertTomestoneCap, InventoryManager.GetLimitedTomestoneWeeklyLimit());
        Data.AtTomeCap = TryUpdateData(Data.AtTomeCap, Data.ExpertTomestones == Data.ExpertTomestoneCap);
        
        base.Update();
    }

    public override void Reset() {
        Data.TaskData.Reset();
        
        base.Reset();
    }

    protected override ModuleStatus GetModuleStatus() {
        if (Config.CompleteWhenCapped && Data.AtTomeCap) return ModuleStatus.Complete;

        return IncompleteTaskCount == 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;
    }
    
    protected override StatusMessage GetStatusMessage() => new LinkedStatusMessage {
        Message = $"{IncompleteTaskCount} {Strings.RoulettesRemaining}", 
        LinkEnabled = Config.ClickableLink, 
        Payload = PayloadId.OpenDutyFinderRoulette,
    };
}