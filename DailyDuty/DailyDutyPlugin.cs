using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DailyDuty.Classes;
using DailyDuty.Classes.HuntAssist;
using Dalamud.Plugin;
using DailyDuty.Models;
using DailyDuty.Windows;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using KamiLib.Classes;
using KamiLib.CommandManager;
using KamiLib.Window;
using KamiToolKit;

namespace DailyDuty;

public sealed class DailyDutyPlugin : IDalamudPlugin {
    private readonly CancellationTokenSource serializerWarmupCancel = new();

    public DailyDutyPlugin(IDalamudPluginInterface pluginInterface) {
        pluginInterface.Create<Service>();

        // Load placeholder SystemConfig, we will load the correct one for the player once they log in.
        System.SystemConfig = new SystemConfig();
        System.NativeController = new NativeController(Service.PluginInterface);

        System.Teleporter = new Teleporter(Service.PluginInterface);
        
        System.CommandManager = new CommandManager(Service.PluginInterface, "dd", "dailyduty");
        System.LocalizationController = new LocalizationController();
        System.PayloadController = new PayloadController();
        System.ContentsFinderController = new AddonController<AddonContentsFinder>("ContentsFinder");
        System.CollectableConfig = new CollectableConfig();
        System.CollectableController = new CollectableController();
        System.HuntAssistConfig = new HuntAssistConfig();
        System.HuntAssistController = new HuntAssistController();

        // Account-wide, so it loads here rather than in OnLogin: the positions it holds
        // describe the world, not the character.
        System.TravelAssistConfig = TravelAssistConfig.Load();

        System.TrayNotificationController = new TrayNotificationController();
        System.TataruPraiseIpc = new TataruPraiseIpc();
        System.ModuleController = new ModuleController();
        System.StatusIpc = new StatusIpc();
        System.TodoListController = new TodoListController();
        System.TimersController = new TimersController();
        System.OverlayController = new OverlayController();

        System.ConfigurationWindow = new ConfigurationWindow();
        System.WindowManager = new WindowManager(Service.PluginInterface);
        System.WindowManager.AddWindow(System.ConfigurationWindow, WindowFlags.IsConfigWindow | WindowFlags.RequireLoggedIn);

        StartSerializerWarmup();

        System.CharacterFileWarmup = new CharacterFileWarmup(Service.PluginInterface.ConfigDirectory.FullName);

        if (Service.ClientState.IsLoggedIn) {
            Service.Framework.RunOnFrameworkThread(() => {
                OnLogin();

                // 登入中的角色隨時可能存檔，它的目錄不讀；認不出是誰就整個不做。
                var current = Service.PlayerState.ContentId;
                if (current is not 0) {
                    System.CharacterFileWarmup.Start(current);
                }
            });
        }
        else {
            System.CharacterFileWarmup.Start(0);
        }
        
        Service.Framework.Update += OnFrameworkUpdate;
        Service.ClientState.Login += OnLogin;
        Service.ClientState.Logout += OnLogout;
        Service.ClientState.TerritoryChanged += OnZoneChange;
    }

    // 卸載的每一步各自隔離:一步失敗不可以讓後面幾步整批跳過。事件訂閱與原生 hook 漏掉
    // 會活到組件卸載之後,下一幀就是崩潰。順序不動。
    public void Dispose() {
        try {
            serializerWarmupCancel.Cancel();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[SerializerWarmup] Dispose step failed, continuing with the rest.");
        }

        try {
            System.CharacterFileWarmup.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CharacterFileWarmup] Dispose step failed, continuing with the rest.");
        }

        try {
            Service.Framework.Update -= OnFrameworkUpdate;
            Service.ClientState.Login -= OnLogin;
            Service.ClientState.Logout -= OnLogout;
            Service.ClientState.TerritoryChanged -= OnZoneChange;
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[Events] Dispose step failed, continuing with the rest.");
        }

        // Never leave vnavmesh walking the character around after we are gone.
        try {
            System.HuntAssistController.Cancel();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[HuntAssist] Dispose step failed, continuing with the rest.");
        }

        try {
            System.StatusIpc.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[StatusIpc] Dispose step failed, continuing with the rest.");
        }

        try {
            System.WindowManager.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[WindowManager] Dispose step failed, continuing with the rest.");
        }

        try {
            System.LocalizationController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[Localization] Dispose step failed, continuing with the rest.");
        }

        try {
            System.PayloadController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[Payload] Dispose step failed, continuing with the rest.");
        }

        try {
            System.OverlayController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[Overlay] Dispose step failed, continuing with the rest.");
        }

        try {
            System.ContentsFinderController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[ContentsFinder] Dispose step failed, continuing with the rest.");
        }

        try {
            System.CollectableController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CollectableController] Dispose step failed, continuing with the rest.");
        }

        try {
            System.ModuleController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[ModuleController] Dispose step failed, continuing with the rest.");
        }

        try {
            System.CommandManager.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CommandManager] Dispose step failed, continuing with the rest.");
        }

        try {
            System.TodoListController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[TodoList] Dispose step failed, continuing with the rest.");
        }

        try {
            System.TimersController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[Timers] Dispose step failed, continuing with the rest.");
        }

        try {
            System.NativeController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[NativeController] Dispose step failed, continuing with the rest.");
        }
    }
    
    private void StartSerializerWarmup() {
        List<Type> types = [typeof(SystemConfig), typeof(CollectableConfig), typeof(HuntAssistConfig), typeof(TodoConfig), typeof(TimersConfig)];
        foreach (var module in System.ModuleController.Modules) {
            types.Add(module.GetData().GetType());
            types.Add(module.GetConfig().GetType());
        }

        var token = serializerWarmupCancel.Token;
        Task.Run(() => {
            SerializerWarmup.Run(types, token);
            StyleContractWarmup.Run(token);
        }, token);
    }

    private static void OnFrameworkUpdate(IFramework framework) {
        if (!Service.ClientState.IsLoggedIn) return;

        // Check for reset, and reset modules that need it 
        System.ModuleController.ResetModules();
        
        // Update All Modules
        System.ModuleController.UpdateModules();
        
        System.TodoListController.Update();
        System.TimersController.Update();
        System.HuntAssistController.Update();
        System.StatusIpc.Update();
    }
    
    // 每一步各自隔離:任何一步擲例外只賠掉它自己,後面幾步照跑。
    // 三個設定檔讀取失敗時,欄位留在建構子放的預設實例,不會變成 null。
    private static void OnLogin() {
        // 必須在任何讀寫角色檔之前:背景還開著檔時,這個角色的存檔會失敗。
        try {
            System.CharacterFileWarmup.Stop();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CharacterFileWarmup] Login step failed, continuing with the rest.");
        }

        try {
            System.SystemConfig = SystemConfig.Load();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[SystemConfig] Login step failed, continuing with the rest.");
        }

        try {
            System.CollectableConfig = CollectableConfig.Load();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CollectableConfig] Login step failed, continuing with the rest.");
        }

        // 收藏品解鎖狀態是逐角色的,而總表快取沒有時間到期 —— 換角後不清掉,
        // 新角色會看到上一個角色的收藏進度。剛載入的 CollectableConfig 也會改變
        // 清單內容(類型開關),兩個理由都要求在這裡失效。
        try {
            System.CollectableController.InvalidateCache();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CollectableCache] Login step failed, continuing with the rest.");
        }

        try {
            System.HuntAssistConfig = HuntAssistConfig.Load();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[HuntAssistConfig] Login step failed, continuing with the rest.");
        }

        try {
            System.ModuleController.LoadModules();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[LoadModules] Login step failed, continuing with the rest.");
        }

        try {
            System.ContentsFinderController.Enable();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[ContentsFinder] Login step failed, continuing with the rest.");
        }

        try {
            System.OverlayController.Enable();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[Overlay] Login step failed, continuing with the rest.");
        }
    }
    
    // 同 OnLogin 逐步隔離:早一步失敗不可以讓模組卸載整段跳過,
    // 那會讓上一個角色的狀態活過角色邊界。
    private static void OnLogout(int type, int code) {
        // 登出就丟掉總表快取,不要讓它活過角色邊界(見 OnLogin 的說明)。
        try {
            System.CollectableController.InvalidateCache();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CollectableCache] Logout step failed, continuing with the rest.");
        }

        try {
            System.StatusIpc.Clear();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[StatusIpc] Logout step failed, continuing with the rest.");
        }

        try {
            System.HuntAssistController.Cancel();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[HuntAssist] Logout step failed, continuing with the rest.");
        }

        try {
            HuntTargets.InvalidateCache();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[HuntTargets] Logout step failed, continuing with the rest.");
        }

        try {
            System.OverlayController.Disable();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[Overlay] Logout step failed, continuing with the rest.");
        }

        try {
            System.ContentsFinderController.Disable();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[ContentsFinder] Logout step failed, continuing with the rest.");
        }

        try {
            System.ModuleController.UnloadModules();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[UnloadModules] Logout step failed, continuing with the rest.");
        }

        // 放在最後:模組已卸載、介面節點已釋放,之後到下一次登入之前沒有路徑會寫角色檔。
        // 剛登出的角色這一輪寫過的檔也在內,它下次登入時讀到的就是掃過的檔。
        try {
            System.CharacterFileWarmup.Start(0);
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[CharacterFileWarmup] Logout step failed, continuing with the rest.");
        }
    }
    
    private static void OnZoneChange(ushort territoryTypeId) {
        if (Service.ClientState.IsPvP) return;
        if (!Service.ClientState.IsLoggedIn) return;
        
        System.ModuleController.ZoneChange(territoryTypeId);
    }
}