using System;
using System.Diagnostics;
using System.Globalization;
using DailyDuty.Classes;
using DailyDuty.Classes.HuntAssist;
using DailyDuty.CustomNodes;
using DailyDuty.Localization;
using DailyDuty.Models;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using KamiLib.Classes;
using KamiLib.Configuration;
using KamiLib.Extensions;

namespace DailyDuty.Modules.BaseModules;

public abstract class Module : IDisposable {
    public abstract ModuleName ModuleName { get; }
    
    public abstract ModuleType ModuleType { get; }

    public abstract ModuleConfig GetConfig();

    public abstract ModuleData GetData();
    
    public virtual void Dispose() { }

    public virtual bool HasTooltip => false;

    public virtual string TooltipText { get; protected set; } = string.Empty;
    
    public virtual bool HasClickableLink => false;

    public virtual PayloadId ClickableLinkPayloadId => PayloadId.Unknown;

    public virtual ModuleStatus ModuleStatus => ModuleStatus.Unknown;
    
    public virtual bool IsEnabled => false;
    public TodoTaskNode? TodoTaskNode;

    public abstract void DrawConfig();

    public abstract void DrawData();

    public abstract void Update();

    public abstract void Load();

    public abstract void Unload();

    public abstract void Reset();

    public abstract void ZoneChange();

    public abstract void SaveConfig();

    public abstract void SaveData();
    
    public abstract DateTime GetNextReset();

    public TimeSpan GetTimeRemaining() => GetData().NextReset - DateTime.UtcNow;
    
    protected abstract ModuleStatus GetModuleStatus();
    
    protected abstract StatusMessage GetStatusMessage();

    protected virtual void UpdateTaskLists() { }

    public virtual bool ShouldReset() => DateTime.UtcNow >= GetData().NextReset;

    /// <summary>
    /// Set by Reset() when this module wants a Windows tray notification for that reset.
    /// It has to be latched during Reset() rather than read afterwards: Reset() clears
    /// Config.Suppressed partway through, so a check made after the fact would notify for
    /// modules the player had deliberately snoozed. ModuleController reads it immediately
    /// after calling Reset(), and the next Reset() overwrites it.
    /// </summary>
    public bool PendingTrayNotification { get; protected set; }

    /// <summary>
    /// true 只在 Load() 整個跑完之後、Unload() 之前成立；登入前一律 false。
    /// 唯一的寫入者是 ModuleController(framework 執行緒)。
    /// </summary>
    public bool IsLoaded { get; internal set; }

    /// <summary>
    /// Load() 擲了例外 ⇒ Data/Config 可能還是預設值,不是玩家的存檔。
    /// 與「還沒登入」是兩回事,所以不能用 !IsLoaded 反推。
    /// </summary>
    public bool LoadFailed { get; internal set; }

    /// <summary>「帶我去」此刻的目的地（Level 的 Object id），0＝此刻沒有。</summary>
    /// <remarks>模組頁面的按鈕與狀態 IPC 共用這個判斷；讀的是模組自己的 Data，只能從遊戲主執行緒讀。</remarks>
    public virtual uint TravelDestinationObjectId => 0;

    /// <summary>此刻有沒有「帶我去」可以出發。只能從遊戲主執行緒讀。</summary>
    public virtual bool HasTravelDestination => TravelDestinationObjectId is not 0;

    /// <summary>替使用者按下「帶我去」。只能從遊戲主執行緒呼叫。</summary>
    /// <returns>false＝沒有出發；原因已印到聊天視窗，或寫在 <see cref="HuntAssistController.StatusText"/>。</returns>
    public virtual bool StartTravel() {
        var destination = TravelDestinationObjectId;
        return destination is not 0 && TravelAssist.Start(destination);
    }
}

public abstract class Module<T, TU> : Module where T : ModuleData, new() where TU : ModuleConfig, new() {
    protected T Data { get; private set; } = new();

    protected TU Config { get; private set; } = new();

    public override bool IsEnabled => Config.ModuleEnabled;

    public override ModuleStatus ModuleStatus => Config.Suppressed ? ModuleStatus.Suppressed : GetModuleStatus();
    
    protected XivChatType GetChatChannel() => Config.UseCustomChannel ? Config.MessageChatChannel : Service.PluginInterface.GeneralChatType;

    public override ModuleConfig GetConfig() => Config;

    public override ModuleData GetData() => Data;

    private readonly Stopwatch statusMessageLockout = new();

    // 資料頁每幀都畫；下次重置的時間文字只在時間或文化變了才重組。
    private bool hasNextResetText;
    private long nextResetTextKey;
    private CultureInfo? nextResetTextCulture;
    private string nextResetText = string.Empty;
    private Action? toggleSuppressed;

    /// <summary>清單型模組一項都沒選。這種 Unknown 只在模組清單與資料頁提示，登入／換區不發聊天狀態訊息。</summary>
    protected virtual bool NothingSelectedToTrack => false;

    protected virtual void UpdateTaskData() { }
        
    protected bool DataChanged;
    protected bool ConfigChanged;

    public override void DrawConfig() {
        Config.DrawConfigUi(this);

        // Config.ConfigChanged is a latch that must be cleared the instant it is consumed.
        // Every write to it happens inside DrawConfigUi(), so clearing it here - in the same frame,
        // on the same call path - cannot drop a pending change. Clearing it in Update() instead
        // would race: Update() runs on the framework tick, before Draw, so it could wipe a flag
        // that DrawConfig() has not consumed yet and the setting would never be persisted.
        ConfigChanged |= Config.ConfigChanged;
        Config.ConfigChanged = false;
    }

    public override void DrawData() {
        ImGuiTweaks.Header(Strings.CurrentStatus);
        using (var _ = ImRaii.PushIndent()) {
           DrawModuleCurrentStatusUi();
        }

        if (Data.NextReset != DateTime.MaxValue) {
            ImGuiTweaks.Header(Strings.ModuleData);
            using var _ = ImRaii.PushIndent();
            
            DrawModuleResetDataUi();
        }
    
        Data.DrawDataUi();
        
        ImGuiTweaks.Header(Strings.ModuleSuppression);
        ImGuiHelpers.CenteredText(Strings.ModuleSuppressionHelp);

        ImGuiTweaks.DisabledButton(!Config.Suppressed ? Strings.Snooze : Strings.UnSnooze, toggleSuppressed ??= ToggleSuppressed);
    }

    private void ToggleSuppressed() {
        Config.Suppressed = !Config.Suppressed;
        ConfigChanged = true;
    }

    private string GetNextResetText(DateTime nextReset) {
        var culture = CultureInfo.CurrentCulture;
        var key = nextReset.ToBinary();

        if (!hasNextResetText || key != nextResetTextKey || !ReferenceEquals(culture, nextResetTextCulture)) {
            nextResetText = nextReset.ToLocalTime().ToString(culture);
            nextResetTextKey = key;
            nextResetTextCulture = culture;
            hasNextResetText = true;
        }

        return nextResetText;
    }

    private void DrawModuleCurrentStatusUi() {
        using var table = ImRaii.Table("module_data_table", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table) return;

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Strings.CurrentStatus);

        ImGui.TableNextColumn();
        var message = ModuleStatus switch {
            ModuleStatus.Suppressed => Strings.Suppressed,
            ModuleStatus.Incomplete => Strings.Incomplete,
            ModuleStatus.InProgress => Strings.InProgress,
            ModuleStatus.Unavailable => Strings.Unavailable,
            ModuleStatus.Complete => Strings.Complete,
            ModuleStatus.Unknown => Strings.Unknown,
            _ => "ERROR, Report this bug.",
        };
        
        ImGui.TextColored(ModuleStatus.GetColor(), message);
    }
    
    private void DrawModuleResetDataUi() {
        using var table = ImRaii.Table("module_data_table", 2, ImGuiTableFlags.SizingStretchSame);
        if (!table) return;

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Strings.NextReset);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetNextResetText(GetData().NextReset));

        var timeRemaining = GetTimeRemaining();
        if (timeRemaining > TimeSpan.Zero) {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Strings.TimeRemaining);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{timeRemaining.Days}.{timeRemaining.Hours:00}:{timeRemaining.Minutes:00}:{timeRemaining.Seconds:00}");
        }
    }
    
    public override void Update() {
        if (DataChanged || ConfigChanged) {
            UpdateTaskLists();
            UpdateTaskData();
            UpdateOverlays();
        }
        
        if (DataChanged) SaveData();
        if (ConfigChanged) SaveConfig();
        
        DataChanged = false;
        ConfigChanged = false;
    }

    public override void Load() {
        Service.Log.Verbose($"[{ModuleName}] Loading Module");
        Data = LoadData();
        Config = LoadConfig();

        if (DateTime.UtcNow > Data.NextReset) {
            Reset();
        }
        
        UpdateTaskLists();
        UpdateTaskData();
        UpdateOverlays();
        
        Update();
        
        if (Config is { OnLoginMessage: true, ModuleEnabled: true, Suppressed: false }) {
            SendStatusMessage();
        }
    }

    public override void Unload() {
        Service.Log.Verbose($"[{ModuleName}] Unloading Module");
        
        statusMessageLockout.Stop();
        statusMessageLockout.Reset();
    }

    public override void Reset() {
        Service.Log.Debug($"[{ModuleName}] Resetting Module, Next Reset: {GetNextReset().ToLocalTime()}");

        // Same gate SendResetMessage() applies, evaluated here because Config.Suppressed is
        // cleared further down this method.
        PendingTrayNotification = Config is { TrayNotificationOnReset: true, ModuleEnabled: true, Suppressed: false };

        SendResetMessage();

        // 只立旗標不存檔：兩個呼叫端（Load() 與 ModuleController.ResetModules()）之後都緊接著
        // 跑 Update()，由它依旗標各存一次。Suppressed 本來就是 false 時設定沒變，不必重寫設定檔。
        Data.NextReset = GetNextReset();
        DataChanged = true;

        if (Config.Suppressed) {
            Config.Suppressed = false;
            ConfigChanged = true;
        }
    }

    public override void ZoneChange() {
        if (Config is { OnZoneChangeMessage: true, ModuleEnabled: true, Suppressed: false }) {
            SendStatusMessage();
        }
    }

    private T LoadData()
        => Service.PluginInterface.LoadCharacterFile(Service.PlayerState.ContentId, $"{ModuleName}.data.json", () => new T());
    
    private TU LoadConfig()
        => Service.PluginInterface.LoadCharacterFile(Service.PlayerState.ContentId, $"{ModuleName}.config.json", () => new TU());
    
    public override void SaveConfig() 
        => Service.PluginInterface.SaveCharacterFile(Service.PlayerState.ContentId, $"{ModuleName}.config.json", Config);
    
    public override void SaveData() 
        => Service.PluginInterface.SaveCharacterFile(Service.PlayerState.ContentId, $"{ModuleName}.data.json", Data);

    private void UpdateOverlays() {
        System.TodoListController.Refresh();
    }

    private void SendStatusMessage() {
        if (GetModuleStatus() is not (ModuleStatus.Incomplete or ModuleStatus.Unknown)) return;

        // 其他原因的 Unknown（例：多瑪飛地還不知道配額）照發；「沒選」要靠使用者去設定，不在聊天裡反覆提醒。
        if (NothingSelectedToTrack) {
            Service.Log.Debug($"[{ModuleName}] Not sending status message: nothing is selected for tracking");
            return;
        }

        if (Service.Condition.IsBoundByDuty()) return;
        if (statusMessageLockout.Elapsed < TimeSpan.FromMinutes(5) && statusMessageLockout.IsRunning) {
            Service.Log.Debug($"[{ModuleName}] Suppressing Status Message: {TimeSpan.FromMinutes(5) - statusMessageLockout.Elapsed}");
            return;
        }
        
        Service.Log.Debug($"[{ModuleName}] Sending Status Message");
        
        var statusMessage = GetStatusMessage();
        if (Config.UseCustomStatusMessage && GetModuleStatus() != ModuleStatus.Unknown) {
            statusMessage.Message = Config.CustomStatusMessage;
        }
        statusMessage.SourceModule = ModuleName;
        statusMessage.MessageChannel = GetChatChannel();
        statusMessage.PrintMessage();
        
        statusMessageLockout.Restart();
    }
    
    private void SendResetMessage() {
        if (Config is not { ResetMessage: true, ModuleEnabled: true, Suppressed: false }) return;
        if (DateTime.UtcNow - Data.NextReset >= TimeSpan.FromMinutes(5)) return;
        
        var statusMessage = GetStatusMessage();
        statusMessage.Message = Config.UseCustomResetMessage ? Config.CustomResetMessage : Strings.ModuleReset;
        statusMessage.SourceModule = ModuleName;
        statusMessage.MessageChannel = GetChatChannel();
        statusMessage.PrintMessage();
    }

    protected TV TryUpdateData<TV>(TV value, TV newValue) where TV : IEquatable<TV> {
        if (!value.Equals(newValue)) {
            DataChanged = true;
            return newValue;
        }

        return value;
    }
}