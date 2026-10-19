using System.Drawing;
using DailyDuty.Classes;
using DailyDuty.Classes.HuntAssist;
using DailyDuty.Localization;
using DailyDuty.Models;
using DailyDuty.Modules.BaseModules;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;

namespace DailyDuty.Modules;

public class DomanEnclaveData : ModuleData {
    public int WeeklyAllowance;
    public int DonatedThisWeek;
    public int RemainingAllowance;

    protected override void DrawModuleData() {
        DrawDataTable([
            (Strings.WeeklyAllowance, WeeklyAllowance.ToString()),
            (Strings.DonatedThisWeek, DonatedThisWeek.ToString()),
            (Strings.BudgetRemaining, RemainingAllowance.ToString()),
        ]);

        ImGuiHelpers.ScaledDummy(5.0f);
        
        if (WeeklyAllowance is 0) {
            ImGui.TextColored(KnownColor.Orange.Vector(), "Visit Doman Enclave to initialize module");
        }
    }
}

public class DomanEnclaveConfig : ModuleConfig {
    public bool ClickableLink = true;

    protected override void DrawModuleConfig() {
        ConfigChanged |= ImGui.Checkbox(Strings.ClickableLink, ref ClickableLink);
    }
}

public unsafe class DomanEnclave : BaseModules.Modules.Weekly<DomanEnclaveData, DomanEnclaveConfig> {
    public override ModuleName ModuleName => ModuleName.DomanEnclave;

    public override bool HasClickableLink => Config.ClickableLink;
    
    public override PayloadId ClickableLinkPayloadId => PayloadId.DomanEnclaveTeleport;

    public override void Update() {
        var reconstructionBoxData = DomanEnclaveManager.Instance();

        // 🔴 DomanEnclaveManager.Instance() 是 [StaticAddress(..., isPointer: true)]：遊戲尚未建立該單例時
        //    （登入前、切角色期間）回的是 null，而不是擲 InvalidOperationException。
        //    裸解參考 null 原生指標是 AVE，try/catch 攔不到 —— 只能事前擋。
        //    讀不到就整段跳過：Data 維持上次的值，等於「這一輪沒更新」，與 Allowance 為 0 同路徑。
        if (reconstructionBoxData is not null && reconstructionBoxData->State.Allowance is not 0) {
            Data.WeeklyAllowance = TryUpdateData(Data.WeeklyAllowance, reconstructionBoxData->State.Allowance);
            Data.DonatedThisWeek = TryUpdateData(Data.DonatedThisWeek, reconstructionBoxData->State.Donated);
            Data.RemainingAllowance = TryUpdateData(Data.RemainingAllowance, Data.WeeklyAllowance - Data.DonatedThisWeek);
        }
        
        base.Update();
    }

    public override void Reset() {
        Data.RemainingAllowance = Data.WeeklyAllowance;
        Data.DonatedThisWeek = 0;
        
        base.Reset();
    }

    // Unknown means the module has never seen the enclave - going there is the fix.
    public override uint TravelDestinationObjectId
        => GetModuleStatus() is ModuleStatus.Complete ? 0 : HuntBoards.DomanEnclaveDonationBoxObjectId;

    public override void DrawData() {
        base.DrawData();

        if (TravelDestinationObjectId is 0) return;

        TravelAssist.Draw(HuntBoards.DomanEnclaveDonationBoxObjectId, Strings.TravelAssistGoToDomanEnclave, Strings.TravelAssistDomanEnclaveTooltip);
    }

    protected override ModuleStatus GetModuleStatus() {
        if (Data.WeeklyAllowance is 0) return ModuleStatus.Unknown;

        return Data.RemainingAllowance is 0 ? ModuleStatus.Complete : ModuleStatus.Incomplete;
    }

    protected override StatusMessage GetStatusMessage() => new LinkedStatusMessage {
        LinkEnabled = Config.ClickableLink, 
        Message = GetModuleStatus() is ModuleStatus.Unknown ? Strings.StatusUnknown : $"{Data.RemainingAllowance} {Strings.GilRemaining}", 
        Payload = PayloadId.DomanEnclaveTeleport,
    };
}