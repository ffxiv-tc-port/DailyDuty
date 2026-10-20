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

    // 每日重置之後多久內的重置算「每日重置」那一波：要蓋住晚一分鐘的 HuntMarksDaily，其他重置都差好幾小時。
    private static readonly TimeSpan DailyResetWindow = TimeSpan.FromMinutes(5);

    private DateTime lastNonDailySkipLogUtc = DateTime.MinValue;

    public ModuleController() {
        Modules = Reflection.ActivateOfType<Module>().ToList();
        goldSaucerMessageController = new GoldSaucerMessageController();
        goldSaucerMessageController.GoldSaucerUpdate += OnGoldSaucerMessage;
    }
    
    public void Dispose() {
        // 漏掉的 Dispose() 留下的是事件訂閱與原生 hook,那些會活到外掛卸載之後 ——
        // 所以一顆收不乾淨不可以讓後面的整批跳過,順序不動、最後一顆也要跑到。
        try {
            goldSaucerMessageController.GoldSaucerUpdate -= OnGoldSaucerMessage;
            goldSaucerMessageController.Dispose();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[GoldSaucerMessageController] Failed to dispose, continuing with the modules.");
        }

        foreach (var module in Modules) {
            if (module is not IDisposable disposable) continue;

            try {
                disposable.Dispose();
            }
            catch (Exception ex) {
                Service.Log.Warning(ex, $"[{module.ModuleName}] Failed to dispose module, continuing with the rest.");
            }
        }
    }

    public IEnumerable<Module> GetModules(ModuleType? type = null) => 
        type is null ? Modules : Modules.Where(module => module.ModuleType == type);

    public void UpdateModules() {
        if (!modulesLoaded) return; 
        
        foreach (var module in Modules) {
            if (!module.IsLoaded) continue;

            module.Update();
        }
    }

    /// <summary>
    /// 一顆模組載入失敗只賠掉它自己：例外不往上拋,呼叫端後面的疊加層初始化照常跑。
    /// </summary>
    /// <remarks>
    /// 失敗的模組留在清單裡但 <see cref="Module.IsLoaded"/> 為 false,每幀更新、重置、
    /// 待辦疊加層與狀態 IPC 都會跳過它 —— 它的 Data/Config 還是預設值,讀了是假的。
    /// </remarks>
    public void LoadModules() {
        var loaded = 0;
        List<string>? failed = null;

        foreach (var module in Modules) {
            module.LoadFailed = false;

            try {
                module.Load();
                module.IsLoaded = true;
                loaded++;
            }
            catch (Exception ex) {
                module.IsLoaded = false;
                module.LoadFailed = true;

                failed ??= [];
                failed.Add(module.ModuleName.ToString());

                Service.Log.Warning(ex, $"[{module.ModuleName}] Failed to load module, skipping it for this session.");
            }
        }

        // 契約:至少一顆載入成功才算「已載入」。零成功時每幀更新與重置本來就會被逐模組的
        // IsLoaded 全部擋掉,提早 return 與跑一圈空迴圈等價 —— 設定視窗的失敗標示讀的是
        // Module.LoadFailed,不經過這個旗標。
        modulesLoaded = loaded > 0;

        if (failed is null) {
            Service.Log.Verbose($"Loaded {loaded} modules");
        }
        else {
            Service.Log.Warning($"Loaded {loaded} modules ({failed.Count} failed: {string.Join(", ", failed)})");
        }
    }
    
    public void UnloadModules() {
        List<string>? failed = null;

        foreach (var module in Modules) {
            try {
                module.Unload();
            }
            catch (Exception ex) {
                // 一顆模組收不乾淨不該擋住其餘模組的 Unload()。
                failed ??= [];
                failed.Add(module.ModuleName.ToString());

                Service.Log.Warning(ex, $"[{module.ModuleName}] Failed to unload module, continuing with the rest.");
            }
            finally {
                module.IsLoaded = false;
            }
        }

        modulesLoaded = false;

        if (failed is null) {
            Service.Log.Debug($"Unloaded {Modules.Count} modules");
        }
        else {
            Service.Log.Warning($"Unloaded {Modules.Count - failed.Count} modules ({failed.Count} failed: {string.Join(", ", failed)})");
        }
    }

    public void ResetModules() {
        if (!modulesLoaded) return;
        
        // Collected rather than notified per module: a daily reset trips a dozen modules in
        // the same frame, and a dozen tray balloons in a row is worse than none at all.
        List<string>? trayNames = null;

        // 語音提醒跟系統通知的觸發條件是分開的:系統通知逐項目 opt-in(TrayNotificationOnReset),
        // 語音只問「這一波重置裡有沒有任何一個是使用者還在追的項目」。
        var activeModuleReset = false;
        List<string>? activeResetNames = null;

        foreach (var module in Modules) {
            if (!module.IsLoaded) continue;

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
                    activeResetNames ??= [];
                    activeResetNames.Add(module.ModuleName.ToString());
                }
            }
        }

        if (trayNames is not null) {
            System.TrayNotificationController.NotifyReset(trayNames);
        }

        // 🔑 出聲的理由是「你還有事情沒做」,不是「重置發生了」;全部都做完(或全部停用)的時候這裡是 0,就安靜。
        // 📌 ModuleStatus 在項目被暫時關閉時回 Suppressed 而不是 Incomplete,所以這個計數天然
        // 排除掉使用者自己按過「暫時關閉」的項目。
        // 📌 登入時的補重置走的是 Module.Load() 直接呼叫 Reset(),不經過這裡,所以不會在登入
        // 那一瞬間跟 TataruPraise 既有的「登入」情境疊成兩聲。
        if (activeModuleReset && System.SystemConfig.SpeakOnReset) {
            // TataruPraise 只有「每日重置」這個情境：其他時刻的重置送它等於念錯，所以不出聲。
            var nowUtc = DateTime.UtcNow;
            var lastDailyResetUtc = Time.NextDailyReset().AddDays(-1);
            if (nowUtc - lastDailyResetUtc >= DailyResetWindow) {
                if (nowUtc - lastNonDailySkipLogUtc >= TimeSpan.FromMinutes(1)) {
                    lastNonDailySkipLogUtc = nowUtc;
                    Service.Log.Information($"[{TataruPraiseIpc.PluginName}] 這一波重置不是每日重置（{string.Join(", ", activeResetNames ?? [])}），不念「{TataruPraiseIpc.CategoryDailyReset}」。");
                }

                return;
            }

            int outstanding;

            try {
                outstanding = Modules.Count(module => module.IsLoaded && module.IsEnabled && module.ModuleStatus is ModuleStatus.Incomplete);
            }
            catch (Exception ex) {
                // 🔴 這段跑在 OnFrameworkUpdate 上：例外往上冒會把那一幀的整個更新拉掉。
                // ModuleStatus 是各模組自己算的（會去讀遊戲狀態），而「重置的那一幀」Config.Suppressed 剛被清、
                // Data 剛被換掉；Draw() 每幀讀得過不代表在這個時機讀是安全的，所以直接包起來。
                // 🔑 fail-safe 方向＝數不出來就不出聲（漏報不誤報）。重置本身（Reset()／系統通知）在上面
                // 早就做完了，存檔在同一幀稍後的 UpdateModules()，這裡失敗只影響語音。
                if (!loggedStatusCountFailure) {
                    loggedStatusCountFailure = true;
                    Service.Log.Warning(ex, "Failed to count outstanding modules after a reset; skipping the voice notification. The reset itself already completed.");
                }

                return;
            }

            loggedStatusCountFailure = false;
            System.TataruPraiseIpc.NotifyDailyReset(outstanding, lastDailyResetUtc);
        }
    }

    public void ZoneChange(uint _) {
        foreach (var module in Modules) {
            if (!module.IsLoaded) continue;

            module.ZoneChange();
        }
    }
    
    private void OnGoldSaucerMessage(GoldSaucerEventArgs e) {
        foreach (var module in Modules) {
            if (!module.IsLoaded) continue;
            if (module is not IGoldSaucerMessageReceiver receiver) continue;

            receiver.GoldSaucerUpdate(e);
        }
    }
}