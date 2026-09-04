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

    /// <summary>
    /// 統計未完成項目時炸過了，只記一次。
    /// </summary>
    /// <remarks>
    /// 🔴 病態情況下 <see cref="ResetModules"/> 會<b>每一幀</b>都判定成該重置
    /// （任何模組的 <c>GetNextReset()</c> 回了過去的時間就會這樣），那時例外也是每幀一次。
    /// 語音那邊的最小間隔是掛在<b>通知</b>上、在計數的<b>下游</b>，擋不到這裡的記錄洗版。
    /// </remarks>
    private bool loggedStatusCountFailure;

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

        // 語音提醒跟系統通知的觸發條件是分開的:系統通知逐項目 opt-in(TrayNotificationOnReset),
        // 語音只問「這一波重置裡有沒有任何一個是使用者還在追的項目」。
        var activeModuleReset = false;
        
        foreach (var module in Modules) {
            if (module.ShouldReset()) {
                module.Reset();

                if (module.PendingTrayNotification) {
                    trayNames ??= [];
                    trayNames.Add(module.ModuleName.GetDescription());
                }

                // ⚠️ Reset() 會把 Config.Suppressed 清掉,但不會動 ModuleEnabled —— 所以這個判斷
                // 在 Reset() 之後讀仍然是對的,不需要像 PendingTrayNotification 那樣先閂起來。
                if (module.IsEnabled) {
                    activeModuleReset = true;
                }
            }
        }

        if (trayNames is not null) {
            System.TrayNotificationController.NotifyReset(trayNames);
        }

        // 🔑 出聲的理由是「你還有事情沒做」,不是「重置發生了」:重置本身每天都會發生,不值得一聲。
        // 全部都做完(或全部停用)的時候這裡是 0,就安靜。
        // 📌 ModuleStatus 在項目被暫時關閉時回 Suppressed 而不是 Incomplete,所以這個計數天然
        // 排除掉使用者自己按過「暫時關閉」的項目——不必再多寫一個判斷。
        // 📌 登入時的補重置走的是 Module.Load() 直接呼叫 Reset(),不經過這裡,所以不會在登入
        // 那一瞬間跟 TataruPraise 既有的「登入」情境疊成兩聲。
        if (activeModuleReset && System.SystemConfig.SpeakOnReset) {
            int outstanding;

            try {
                outstanding = Modules.Count(module => module.IsEnabled && module.ModuleStatus is ModuleStatus.Incomplete);
            }
            catch (Exception ex) {
                // 🔴 這段跑在 OnFrameworkUpdate 上：例外往上冒會把那一幀的整個更新拉掉。
                // ModuleStatus 是各模組自己算的（GetModuleStatus() 會去讀遊戲狀態），而「重置的那一幀」
                // 是它從來沒被讀過的時機 —— Config.Suppressed 剛被清、Data 剛被換掉。
                // ⚠️「同一個 property 在 Draw() 每幀被讀過」證不了「在這個時機讀是安全的」，
                // 兩者的遊戲狀態不一樣。所以這裡不賭，直接包起來。
                // 🔑 fail-safe 方向＝數不出來就不出聲（漏報不誤報），與這個功能其餘部分一致。
                // 📌 重置本身（Reset()／存檔／系統通知）在上面早就做完了，這裡失敗只影響語音。
                if (!loggedStatusCountFailure) {
                    loggedStatusCountFailure = true;
                    Service.Log.Warning(ex, "Failed to count outstanding modules after a reset; skipping the voice notification. The reset itself already completed.");
                }

                return;
            }

            loggedStatusCountFailure = false;
            System.TataruPraiseIpc.NotifyReset(outstanding);
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