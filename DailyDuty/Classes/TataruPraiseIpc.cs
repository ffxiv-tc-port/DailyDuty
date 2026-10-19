using System;
using Dalamud.Plugin.Ipc.Exceptions;

namespace DailyDuty.Classes;

/// <summary>
/// 單向橋接到「塔塔露誇獎」(TataruPraise)：日課／週課重置之後，如果還有沒做完的事，請它念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約（本外掛沒有 ECommons），
/// 對方沒安裝時本檔的每一條路徑都是 no-op，DailyDuty 這邊完全無感。
/// <para>
/// 🔴 契約名與情境鍵逐字取自 TataruPraise 的 <c>IpcContract.cs</c> 與 <c>Core/PraiseCategory.cs</c>
/// （<c>PraiseCategory.DailyReset</c>）。CallGate 是純字串比對，名字打錯不會有任何錯誤訊息，
/// 只會永遠得到「這個頻道沒有人註冊」——<b>靜默斷線</b>。所以字串都寫成常數，不散在呼叫點上。
/// </para>
/// <para>
/// 🔴 <b>只能從主執行緒（framework tick）呼叫。</b>IPC 的實作是在<b>呼叫端</b>的執行緒上跑的。
/// 目前唯一的呼叫點是 <see cref="ModuleController.ResetModules"/>，它掛在
/// <c>DailyDutyPlugin.OnFrameworkUpdate</c> 上。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響 DailyDuty 的任何流程，不重試，
/// 也不會因此改變任何一個項目的狀態。
/// </para>
/// </remarks>
public class TataruPraiseIpc {
    /// <summary>對方外掛的內部名稱，只用在記錄檔的措辭上；判斷在不在一律靠 IPC 本身。</summary>
    public const string PluginName = "TataruPraise";

    /// <summary><c>Func&lt;string, bool&gt;</c>：<b>這一個情境</b>現在出不出得了聲（總開關＋這個情境的開關＋這個情境有已合成的語音）。</summary>
    /// <remarks>📌 刻意<b>不</b>看冷卻：冷卻是「這一次剛好不出聲」，不是「不能出聲」。</remarks>
    public const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句來念。</summary>
    public const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 送過去的情境字串。<b>逐字對應 TataruPraise 內建情境 <c>PraiseCategory.DailyReset</c>。</b>
    /// ⚠️ 這同時是對方 <c>pool.json</c> 的鍵：查不到這個鍵時 <c>Praise</c> 只回 <c>false</c>
    /// （不出聲、不報錯）。
    /// </summary>
    public const string CategoryDailyReset = "每日重置";

    /// <summary>
    /// 兩次出聲之間的最小間隔。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>防呆</b>不是節流：正常情況一天只會走到這裡一兩次。
    /// 但 <see cref="ModuleController.ResetModules"/> 是掛在 framework tick 上的，
    /// 只要有任何一個項目的 <c>GetNextReset()</c> 回了過去的時間，它就會<b>每一幀</b>都判定成「該重置」。
    /// 沒有這道閘門的話，那個情況的失敗形式是語音一直念個不停。
    /// </remarks>
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(1);

    /// <summary>「對方沒安裝」只寫一次記錄，不要每天都刷一行。</summary>
    private bool loggedNotInstalled;

    private DateTime lastSpokenUtc = DateTime.MinValue;

    /// <summary>
    /// 重置之後還有沒做完的事，請塔塔露念一句。
    /// </summary>
    /// <param name="outstandingCount">重置之後仍然「未完成」的項目數，只寫進記錄，不送給對方。</param>
    /// <remarks>
    /// 🔴 <b>呼叫端必須自己確定站在「剛剛才重置」這個邊緣上。</b>這個方法只有時間閘門，
    /// 沒有辦法分辨「重置了」和「每一幀都在輪詢」。
    /// </remarks>
    public void NotifyReset(int outstandingCount) {
        if (outstandingCount <= 0) return;

        var now = DateTime.UtcNow;
        if (now - lastSpokenUtc < MinimumGap) {
            Service.Log.Information($"[{PluginName}] 距離上次出聲不到 {MinimumGap.TotalSeconds} 秒，這次不念（未完成 {outstandingCount} 項）。");
            return;
        }

        try {
            // 先問 IsAvailableFor(情境)：問的是「這一個情境」出不出得了聲——總開關關著、
            // 使用者把這個情境關掉、或這個情境一句已合成的都沒有，都在這裡擋掉。
            // 🔴 不要退回去問 IsAvailable：那個問的是「整池」，於是「別的情境有句子、
            //    我這個情境一句都沒有」時它照樣回 true，這道閘門等於白做。
            // 這一步同時兼作「對方在不在」的探測——沒註冊就會在這裡擲 IpcNotReadyError。
            if (!Service.PluginInterface.GetIpcSubscriber<string, bool>(TagIsAvailableFor).InvokeFunc(CategoryDailyReset)) {
                return;
            }

            var accepted = Service.PluginInterface.GetIpcSubscriber<string, bool>(TagPraise).InvokeFunc(CategoryDailyReset);
            loggedNotInstalled = false;
            lastSpokenUtc = now;

            // 📌 Information 級：這是「使用者說重置了卻沒出聲」時唯一問得出真相的一行。
            // ⚠️ 回傳 false 不是錯誤：可能還在冷卻，也可能「每日重置」這個情境在池裡一句都沒有。
            Service.Log.Information($"[{PluginName}] 重置後還有 {outstandingCount} 項未完成：Praise(「{CategoryDailyReset}」) 回傳 {accepted}。");
        }
        catch (IpcNotReadyError) {
            // 對方沒安裝／還沒載入。這是完全正常的狀態，不是錯誤。
            if (!loggedNotInstalled) {
                loggedNotInstalled = true;
                Service.Log.Information($"[{PluginName}] 想請它在重置後念一句，但它沒有安裝或尚未載入（IPC「{TagIsAvailableFor}」沒有人註冊）。這個功能會維持靜默，DailyDuty 其餘流程完全不受影響。");
            }
        }
        catch (Exception e) {
            // 對方版本不合、簽名對不上、或它自己的回呼裡爆掉。這裡跑在 OnFrameworkUpdate 上，
            // 讓例外往上冒會把那一幀的整個更新拉掉——絕對不可以。
            Service.Log.Information(e, $"[{PluginName}] 呼叫 IPC 失敗，這次不念。");
        }
    }
}
