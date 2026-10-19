using System.Drawing;
using System.Numerics;
using DailyDuty.Classes;
using DailyDuty.Classes.HuntAssist;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using KamiLib.Classes;

namespace DailyDuty.Modules;

public class MiniCactpotData : ModuleData {
    public int AllowancesRemaining = 3;
    
    protected override void DrawModuleData() {
        DrawDataTable([
            (Strings.AllowancesRemaining, AllowancesRemaining.ToString()),
        ]);
    }
}

public class MiniCactpotConfig : ModuleConfig {
    public bool ClickableLink = true;
    
    protected override void DrawModuleConfig() {
        ConfigChanged |= ImGui.Checkbox(Strings.ClickableLink, ref ClickableLink);
    }
}

public unsafe class MiniCactpot : BaseModules.Modules.Daily<MiniCactpotData, MiniCactpotConfig>, IGoldSaucerMessageReceiver {
    public override ModuleName ModuleName => ModuleName.MiniCactpot;
    
    public override bool HasClickableLink => Config.ClickableLink;
    
    public override PayloadId ClickableLinkPayloadId => PayloadId.MiniCactpotTravel;

    public override void Load() {
        base.Load();
        
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "LotteryDaily", LotteryDailyPreSetup);
    }

    public override void Unload() {
        base.Unload();
        
        Service.AddonLifecycle.UnregisterListener(LotteryDailyPreSetup);
    }

    public override void Reset() {
        Data.AllowancesRemaining = 3;
        
        base.Reset();
    }

    protected override ModuleStatus GetModuleStatus() 
        => Data.AllowancesRemaining == 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;

    // Once the day's three tickets are gone the broker has nothing left to offer.
    public override uint TravelDestinationObjectId
        => Data.AllowancesRemaining is 0 ? 0 : HuntBoards.MiniCactpotBrokerObjectId;

    public override void DrawData() {
        base.DrawData();

        DrawTravelAssist();
    }

    /// <summary>
    /// One click takes the character to the Mini Cactpot broker: Lifestream teleports to the
    /// Gold Saucer, vnavmesh walks the last stretch, and that is where it stops.
    ///
    /// Movement only - it never buys a ticket, never targets the broker and never opens a
    /// window. Playing the game is still the player's job.
    /// </summary>
    private void DrawTravelAssist() {
        if (TravelDestinationObjectId is 0) return;

        var controller = System.HuntAssistController;

        ImGuiTweaks.Header(Strings.TravelAssistSectionTitle);
        using var indent = ImRaii.PushIndent();

        if (controller.StatusText.Length > 0) {
            var statusColor = controller.IsRunning ? KnownColor.Orange.Vector() : KnownColor.Gray.Vector();
            ImGui.TextColored(statusColor, controller.StatusText);
        }

        var buttonSize = new Vector2(ImGui.GetContentRegionAvail().X, 23.0f * ImGuiHelpers.GlobalScale);

        // The trip may well have been started from the hunt module - one controller, one trip.
        if (controller.IsRunning) {
            if (ImGui.Button(Strings.HuntAssistCancel, buttonSize)) controller.Cancel();

            return;
        }

        if (HuntBoards.Resolve(HuntBoards.MiniCactpotBrokerObjectId) is not { } destination) {
            ImGui.TextColored(KnownColor.Orange.Vector(), Strings.TravelAssistNoDestinationData);
            return;
        }

        if (ImGui.Button(Strings.TravelAssistGoToMiniCactpot, buttonSize)) {
            controller.GoToLocation(destination, Strings.TravelAssistStatusTeleporting);
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.TravelAssistMiniCactpotTooltip);
    }

    protected override StatusMessage GetStatusMessage() => new LinkedStatusMessage {
        LinkEnabled = Config.ClickableLink,
        Message = $"{Data.AllowancesRemaining} {Strings.TicketsRemaining}",
        Payload = PayloadId.MiniCactpotTravel,
    };

    private void LotteryDailyPreSetup(AddonEvent eventType, AddonArgs addonInfo) {
        Data.AllowancesRemaining -= 1;
        DataChanged = true;
    }

    public void GoldSaucerUpdate(GoldSaucerEventArgs data) {
        if (Service.TargetManager.Target?.BaseId is not HuntBoards.MiniCactpotBrokerObjectId) return;

        if (data.EventId == 5) {
            Data.AllowancesRemaining = data.Data[4];
            DataChanged = true;
        }
        else {
            Data.AllowancesRemaining = 0;
            DataChanged = true;
        } 
    }
}