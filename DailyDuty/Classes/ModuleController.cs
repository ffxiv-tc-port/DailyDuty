using System;
using System.Collections.Generic;
using System.Linq;
using DailyDuty.Modules.BaseModules;
using KamiLib.Classes;
using KamiLib.Extensions;

namespace DailyDuty.Classes;

public class ModuleController : IDisposable {
    public List<Module> Modules { get; }
    private readonly GoldSaucerMessageController goldSaucerMessageController;
    private bool modulesLoaded;

    public ModuleController() {
        Modules = Reflection.ActivateOfType<Module>().ToList();
        goldSaucerMessageController = new GoldSaucerMessageController();
        goldSaucerMessageController.GoldSaucerUpdate += OnGoldSaucerMessage;
    }
    
    public void Dispose() {
        goldSaucerMessageController.GoldSaucerUpdate -= OnGoldSaucerMessage;
        goldSaucerMessageController.Dispose();

        foreach (var module in Modules.OfType<IDisposable>()) {
            module.Dispose();
        }
    }

    public IEnumerable<Module> GetModules(ModuleType? type = null) => 
        type is null ? Modules : Modules.Where(module => module.ModuleType == type);

    public void UpdateModules() {
        if (!modulesLoaded) return; 
        
        foreach (var module in Modules) {
            module.Update();
        }
    }

    public void LoadModules() {
        foreach (var module in Modules) {
            module.Load();
        }

        modulesLoaded = true;
        Service.Log.Debug("All Modules Loaded");
    }
    
    public void UnloadModules() {
        foreach (var module in Modules) {
            module.Unload();
        }

        modulesLoaded = false;
    }

    public void ResetModules() {
        if (!modulesLoaded) return;
        
        // Collected rather than notified per module: a daily reset trips a dozen modules in
        // the same frame, and a dozen tray balloons in a row is worse than none at all.
        List<string>? trayNames = null;
        
        foreach (var module in Modules) {
            if (module.ShouldReset()) {
                module.Reset();

                if (module.PendingTrayNotification) {
                    trayNames ??= [];
                    trayNames.Add(module.ModuleName.GetDescription());
                }
            }
        }

        if (trayNames is not null) {
            System.TrayNotificationController.NotifyReset(trayNames);
        }
    }

    public void ZoneChange(uint _) {
        foreach (var module in Modules) {
            module.ZoneChange();
        }
    }
    
    private void OnGoldSaucerMessage(GoldSaucerEventArgs e) {
        foreach (var module in Modules.OfType<IGoldSaucerMessageReceiver>()) {
            module.GoldSaucerUpdate(e);
        }
    }
}