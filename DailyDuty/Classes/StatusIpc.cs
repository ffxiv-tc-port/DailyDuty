using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DailyDuty.Classes.HuntAssist;
using DailyDuty.Localization;
using DailyDuty.Modules.BaseModules;
using Dalamud.Plugin.Ipc;
using KamiLib.Extensions;

namespace DailyDuty.Classes;

/// <summary>
/// DailyDuty 的狀態 IPC：讓別的外掛問「這個角色今天還有什麼沒做」，並替使用者按下模組頁面上的「帶我去」。
/// </summary>
/// <remarks>
/// 🔴 <b>端點跑在呼叫端的執行緒上</b>（Dalamud CallGate 的契約），而各模組的
/// <c>ModuleStatus</c> 要去讀非執行緒安全的 Data 物件 ⇒ 端點<b>絕不</b>當場去問模組。
/// 正解＝framework 執行緒每 N 幀拍一份快照、整份換參考，端點只讀快照就地回。
/// <para>
/// 🔴 這裡<b>不放任何會阻塞的閘門</b>：消費端可能每幀輪詢，阻塞式等待會把它們一起拖住。
/// 會動遊戲狀態的（出發、取消）用 <c>IFramework.Run</c> 排到下一幀，端點不等結果。
/// </para>
/// <para>
/// 📌 已發佈的快照實例<b>永不就地修改</b>，所以讀取端不需要鎖。
/// </para>
/// </remarks>
public sealed class StatusIpc : IDisposable {
    /// <summary>消費端用 <c>&gt;=</c> 判相容。新增端點時 +1（2＝多了名稱、類型與「帶我去」；3＝多了「不知道」清單）；只加狀態碼不動它。</summary>
    public const int ApiVersion = 3;

    // 🔴 對外狀態碼。0 必須是「問不到」——消費端把 default(int) 讀成「已完成」會是災難。
    // 這組數字與內部的 ModuleStatus 列舉「刻意不同」：那個的 0 是 Unknown、4 是 Complete。
    public const int StatusUnavailableToQuery = 0;
    public const int StatusIncomplete = 1;
    public const int StatusComplete = 2;
    public const int StatusUnavailable = 3;
    public const int StatusUnknownOrSuppressed = 4;
    public const int StatusInProgress = 5;

    // 模組類型碼，0 同樣是「問不到」。
    public const int TypeUnavailableToQuery = 0;
    public const int TypeDaily = 1;
    public const int TypeWeekly = 2;
    public const int TypeSpecial = 3;

    // TravelToModule 的回傳值。0 是「不可用」：消費端把 default(int) 讀成「已受理」會是災難。
    public const int TravelUnavailable = 0;
    public const int TravelRequested = 1;
    public const int TravelNoDestination = 2;
    public const int TravelBusy = 3;

    private const string TagApiVersion = "DailyDuty.StatusApiVersion";
    private const string TagIncompleteModules = "DailyDuty.GetIncompleteModules";
    private const string TagUnknownModules = "DailyDuty.GetUnknownModules";
    private const string TagModuleStatus = "DailyDuty.GetModuleStatus";
    private const string TagModuleDisplayName = "DailyDuty.GetModuleDisplayName";
    private const string TagModuleType = "DailyDuty.GetModuleType";
    private const string TagTravelModules = "DailyDuty.GetTravelModules";
    private const string TagTravelToModule = "DailyDuty.TravelToModule";
    private const string TagIsTravelRunning = "DailyDuty.IsTravelRunning";
    private const string TagCancelTravel = "DailyDuty.CancelTravel";

    /// <summary>重拍快照的間隔。模組狀態以分鐘為單位變動，每幀重算只是白燒 CPU。</summary>
    private const int RefreshIntervalFrames = 30;

    private readonly ICallGateProvider<int> apiVersionProvider;
    private readonly ICallGateProvider<List<string>> incompleteModulesProvider;
    private readonly ICallGateProvider<List<string>> unknownModulesProvider;
    private readonly ICallGateProvider<string, int> moduleStatusProvider;
    private readonly ICallGateProvider<string, string> moduleDisplayNameProvider;
    private readonly ICallGateProvider<string, int> moduleTypeProvider;
    private readonly ICallGateProvider<List<string>> travelModulesProvider;
    private readonly ICallGateProvider<string, int> travelToModuleProvider;
    private readonly ICallGateProvider<bool> isTravelRunningProvider;
    private readonly ICallGateProvider<bool> cancelTravelProvider;

    // 🔴 這三個欄位被兩種執行緒碰：framework 執行緒寫（整份換參考）、IPC 端點從呼叫端執行緒讀。
    // volatile 保證讀得到的一定是某一次完整發佈的值，不會是半份。
    private volatile Snapshot snapshot = Snapshot.Empty;
    private volatile bool travelRunning;
    private volatile bool disposed;

    // 還沒執行的出發請求的號碼，0＝沒有。取消、登出、卸載會清掉它；排隊的請求輪到時對不上號碼就不出發。
    private long pendingTravelTicket;
    private long travelTicketCounter;

    private int framesUntilRefresh;

    // 只在 framework 執行緒用；第一次拍快照時才建立。
    private PendingSnapshot? pending;
    private bool loggedSnapshotFailure;
    private bool loggedTravelCheckFailure;

    public StatusIpc() {
        apiVersionProvider = Service.PluginInterface.GetIpcProvider<int>(TagApiVersion);
        incompleteModulesProvider = Service.PluginInterface.GetIpcProvider<List<string>>(TagIncompleteModules);
        unknownModulesProvider = Service.PluginInterface.GetIpcProvider<List<string>>(TagUnknownModules);
        moduleStatusProvider = Service.PluginInterface.GetIpcProvider<string, int>(TagModuleStatus);
        moduleDisplayNameProvider = Service.PluginInterface.GetIpcProvider<string, string>(TagModuleDisplayName);
        moduleTypeProvider = Service.PluginInterface.GetIpcProvider<string, int>(TagModuleType);
        travelModulesProvider = Service.PluginInterface.GetIpcProvider<List<string>>(TagTravelModules);
        travelToModuleProvider = Service.PluginInterface.GetIpcProvider<string, int>(TagTravelToModule);
        isTravelRunningProvider = Service.PluginInterface.GetIpcProvider<bool>(TagIsTravelRunning);
        cancelTravelProvider = Service.PluginInterface.GetIpcProvider<bool>(TagCancelTravel);

        apiVersionProvider.RegisterFunc(GetApiVersion);
        incompleteModulesProvider.RegisterFunc(GetIncompleteModules);
        unknownModulesProvider.RegisterFunc(GetUnknownModules);
        moduleStatusProvider.RegisterFunc(GetModuleStatus);
        moduleDisplayNameProvider.RegisterFunc(GetModuleDisplayName);
        moduleTypeProvider.RegisterFunc(GetModuleType);
        travelModulesProvider.RegisterFunc(GetTravelModules);
        travelToModuleProvider.RegisterFunc(TravelToModule);
        isTravelRunningProvider.RegisterFunc(IsTravelRunning);
        cancelTravelProvider.RegisterFunc(CancelTravel);
    }

    public void Dispose() {
        // 排隊中的出發／取消輪到時先看這個旗標：卸載之後一律不動。
        disposed = true;

        // 先讓端點消失再清資料：端點不存在時消費端接得到 IpcNotReadyError，會乾淨落回自己的 fail-safe。
        apiVersionProvider.UnregisterFunc();
        incompleteModulesProvider.UnregisterFunc();
        unknownModulesProvider.UnregisterFunc();
        moduleStatusProvider.UnregisterFunc();
        moduleDisplayNameProvider.UnregisterFunc();
        moduleTypeProvider.UnregisterFunc();
        travelModulesProvider.UnregisterFunc();
        travelToModuleProvider.UnregisterFunc();
        isTravelRunningProvider.UnregisterFunc();
        cancelTravelProvider.UnregisterFunc();

        Clear();
    }

    /// <summary>丟掉快照，端點改回「問不到」。登出時必須呼叫：別讓上一個角色的答案活過角色邊界。</summary>
    public void Clear() {
        snapshot = Snapshot.Empty;
        travelRunning = false;
        Interlocked.Exchange(ref pendingTravelTicket, 0);
        framesUntilRefresh = 0;
    }

    /// <summary>重拍快照。<b>只能從 framework 執行緒呼叫。</b></summary>
    public void Update() {
        // 每幀發佈、不等快照：消費端拿它決定要畫「出發」還是「取消」。
        travelRunning = System.HuntAssistController.IsRunning;

        if (--framesUntilRefresh > 0) return;
        framesUntilRefresh = RefreshIntervalFrames;

        var next = pending ??= new PendingSnapshot();
        next.Begin(Strings.Culture);

        try {
            foreach (var module in System.ModuleController.Modules) {
                // 這次沒載起來的模組狀態是預設值,不是真的,不要對外報告。
                if (!module.IsLoaded) continue;

                // 使用者沒在追的項目不對外報告：問不到與「沒開啟」對消費端是同一件事。
                if (!module.IsEnabled) continue;

                var name = next.InternalName(module.ModuleName);
                var moduleStatus = module.ModuleStatus;
                var status = ToIpcStatus(moduleStatus);
                var canTravel = HasTravelDestination(module);

                next.Modules.Add(new(name, new ModuleEntry(status, ToIpcType(module.ModuleType), next.DisplayName(module.ModuleName), canTravel)));
                if (status is StatusIncomplete) next.Incomplete.Add(name);

                // 對外狀態碼 4 把「不知道」與「暫時關閉」併在一起；這份清單只收前者。
                if (moduleStatus is ModuleStatus.Unknown) next.Unknown.Add(name);
                if (canTravel) next.Travel.Add(name);
            }
        }
        catch (Exception ex) {
            // 🔴 這裡跑在 OnFrameworkUpdate 上：例外冒出去會把那一幀其餘的更新全拉掉。
            // ModuleStatus 是各模組自己算的（會讀剛被換掉的 Data），重置的那一幀沒被驗過；
            // 同一個理由已經讓 ModuleController.ResetModules 包了一層。
            // 失敗就留著上一份快照（下一輪自然補回來），不要發佈半份。
            if (!loggedSnapshotFailure) {
                loggedSnapshotFailure = true;
                Service.Log.Warning(ex, "Failed to build the DailyDuty status IPC snapshot; keeping the previous one.");
            }

            return;
        }

        loggedSnapshotFailure = false;

        // 內容與已發佈的那份相同就沿用。空快照代表「還沒拍過」，登入後第一份一定要換掉它，
        // 即使內容同樣是空的（端點靠「是不是那一個空快照」分辨問不到與沒有目的地）。
        var published = snapshot;
        if (!ReferenceEquals(published, Snapshot.Empty) && next.Matches(published)) return;

        snapshot = next.ToSnapshot();
    }

    private static int GetApiVersion() => ApiVersion;

    /// <summary>目前啟用、且狀態正好是 Incomplete 的模組內部名稱。問不到就回空清單。</summary>
    /// <remarks>
    /// 📌 回的是<b>複本</b>：CallGate 對型別相符的回傳值是直接傳參考的，
    /// 把快照原件交出去等於讓消費端有機會改壞它。
    /// </remarks>
    private List<string> GetIncompleteModules() => [..snapshot.IncompleteNames];

    /// <summary>目前啟用、但 DailyDuty 不知道狀態的模組內部名稱（例：通緝令一張都沒勾）；暫時關閉的不算。問不到回空清單。</summary>
    private List<string> GetUnknownModules() => [..snapshot.UnknownNames];

    /// <summary>單一模組的狀態碼。名稱不分大小寫；查不到／沒啟用／未登入一律回 0。</summary>
    private int GetModuleStatus(string moduleName)
        => TryGetEntry(snapshot, moduleName, out var entry) ? entry.Status : StatusUnavailableToQuery;

    /// <summary>模組在目前介面語言下的顯示名稱。問不到回空字串。</summary>
    private string GetModuleDisplayName(string moduleName)
        => TryGetEntry(snapshot, moduleName, out var entry) ? entry.DisplayName : string.Empty;

    /// <summary>模組類型碼。問不到回 0。</summary>
    private int GetModuleType(string moduleName)
        => TryGetEntry(snapshot, moduleName, out var entry) ? entry.Type : TypeUnavailableToQuery;

    /// <summary>此刻模組頁面上畫得出「帶我去」的模組內部名稱（同樣回複本）。</summary>
    private List<string> GetTravelModules() => [..snapshot.TravelNames];

    private bool IsTravelRunning() => travelRunning;

    /// <summary>替使用者按下某個模組的「帶我去」。只排隊、不等結果；出發與否由 DailyDuty 自己印到聊天視窗。</summary>
    private int TravelToModule(string moduleName) {
        if (disposed || Service.Framework.IsFrameworkUnloading) return TravelUnavailable;

        // 空快照＝未登入，或登入後還沒拍過第一份。
        var current = snapshot;
        if (ReferenceEquals(current, Snapshot.Empty)) return TravelUnavailable;

        if (!TryGetEntry(current, moduleName, out var entry) || !entry.CanTravel) return TravelNoDestination;
        if (travelRunning) return TravelBusy;

        var ticket = Interlocked.Increment(ref travelTicketCounter);
        if (Interlocked.CompareExchange(ref pendingTravelTicket, ticket, 0) is not 0) return TravelBusy;

        try {
            _ = Service.Framework.Run(() => RunTravelRequest(moduleName, ticket));
        }
        catch (Exception ex) {
            Interlocked.CompareExchange(ref pendingTravelTicket, 0, ticket);
            Service.Log.Warning(ex, $"[StatusIpc] Could not queue the travel request for {moduleName}.");
            return TravelUnavailable;
        }

        return TravelRequested;
    }

    /// <summary>取消目前的行程（不論是誰發起的）與還沒執行的出發請求。回傳呼叫當下有沒有東西可取消。</summary>
    private bool CancelTravel() {
        if (disposed || Service.Framework.IsFrameworkUnloading) return false;

        var hadPending = Interlocked.Exchange(ref pendingTravelTicket, 0) is not 0;
        var hadTrip = travelRunning;

        try {
            // 沒在跑時 Cancel() 什麼都不做；照樣排，免得錯過同一幀才出發的行程。
            _ = Service.Framework.Run(() => RunCancelRequest());
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[StatusIpc] Could not queue the travel cancel request.");
        }

        return hadPending || hadTrip;
    }

    /// <summary>排隊的出發請求，在 framework 執行緒上跑。</summary>
    private void RunTravelRequest(string moduleName, long ticket) {
        // 號碼對不上＝已經被取消、登出或卸載撤掉。
        if (Interlocked.CompareExchange(ref pendingTravelTicket, 0, ticket) != ticket) return;
        if (disposed || !Service.ClientState.IsLoggedIn) return;

        try {
            if (System.HuntAssistController.IsRunning) {
                HuntAssistController.PrintMessage(Strings.HuntAssistAlreadyRunning);
                return;
            }

            if (FindModule(moduleName) is not { HasTravelDestination: true } module) {
                Service.Log.Information($"[StatusIpc] Travel request for {moduleName} dropped: it has no destination any more.");
                return;
            }

            var started = module.StartTravel();
            Service.Log.Information($"[StatusIpc] Travel request for {module.ModuleName}: {(started ? "started" : "not started")}.");
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, $"[StatusIpc] Travel request for {moduleName} failed.");
        }
    }

    private void RunCancelRequest() {
        if (disposed) return;

        try {
            System.HuntAssistController.Cancel();
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "[StatusIpc] Travel cancel request failed.");
        }
    }

    // 自己吞例外：一個模組算不出來，不可以拖垮整份狀態快照。
    private bool HasTravelDestination(Module module) {
        try {
            return module.HasTravelDestination;
        }
        catch (Exception ex) {
            if (!loggedTravelCheckFailure) {
                loggedTravelCheckFailure = true;
                Service.Log.Warning(ex, $"[StatusIpc] Failed to check {module.ModuleName} for a travel destination; reporting none.");
            }

            return false;
        }
    }

    private static Module? FindModule(string moduleName) {
        foreach (var module in System.ModuleController.Modules) {
            if (!module.IsLoaded || !module.IsEnabled) continue;
            if (string.Equals(module.ModuleName.ToString(), moduleName, StringComparison.OrdinalIgnoreCase)) return module;
        }

        return null;
    }

    private static bool TryGetEntry(Snapshot from, string moduleName, out ModuleEntry entry) {
        if (string.IsNullOrEmpty(moduleName)) {
            entry = default;
            return false;
        }

        return from.Modules.TryGetValue(moduleName, out entry);
    }

    /// <summary>內部 <see cref="ModuleStatus"/> 對到公開狀態碼。沒見過的值回「問不到」而不是猜。</summary>
    private static int ToIpcStatus(ModuleStatus status) => status switch {
        ModuleStatus.Incomplete => StatusIncomplete,
        ModuleStatus.Complete => StatusComplete,
        ModuleStatus.Unavailable => StatusUnavailable,
        ModuleStatus.InProgress => StatusInProgress,
        ModuleStatus.Unknown or ModuleStatus.Suppressed => StatusUnknownOrSuppressed,
        _ => StatusUnavailableToQuery,
    };

    private static int ToIpcType(ModuleType type) => type switch {
        ModuleType.Daily => TypeDaily,
        ModuleType.Weekly => TypeWeekly,
        ModuleType.Special => TypeSpecial,
        _ => TypeUnavailableToQuery,
    };

    private readonly record struct ModuleEntry(int Status, int Type, string DisplayName, bool CanTravel);

    private sealed class Snapshot(IReadOnlyDictionary<string, ModuleEntry> modules, IReadOnlyList<string> incompleteNames, IReadOnlyList<string> unknownNames, IReadOnlyList<string> travelNames) {
        public static readonly Snapshot Empty = new(new Dictionary<string, ModuleEntry>(StringComparer.OrdinalIgnoreCase), [], [], []);

        public IReadOnlyDictionary<string, ModuleEntry> Modules { get; } = modules;

        public IReadOnlyList<string> IncompleteNames { get; } = incompleteNames;

        public IReadOnlyList<string> UnknownNames { get; } = unknownNames;

        public IReadOnlyList<string> TravelNames { get; } = travelNames;
    }

    /// <summary>
    /// 這一輪算出來的內容，先放在重複使用的清單裡，與已發佈的快照不同才配置新的一份。
    /// </summary>
    /// <remarks>
    /// 只在 framework 執行緒用。<see cref="ToSnapshot"/> 一律複製出去：已發佈的快照不可以共用這裡的清單。
    /// </remarks>
    private sealed class PendingSnapshot {
        public readonly List<KeyValuePair<string, ModuleEntry>> Modules = [];
        public readonly List<string> Incomplete = [];
        public readonly List<string> Unknown = [];
        public readonly List<string> Travel = [];

        private readonly Dictionary<ModuleName, string> internalNames = [];

        // 顯示名稱跟著介面語言走：語言一換（Strings.Culture 換成另一個物件）整份作廢。
        private readonly Dictionary<ModuleName, string> displayNames = [];
        private CultureInfo? displayNamesCulture;

        public void Begin(CultureInfo? culture) {
            Modules.Clear();
            Incomplete.Clear();
            Unknown.Clear();
            Travel.Clear();

            if (!ReferenceEquals(culture, displayNamesCulture)) {
                displayNames.Clear();
                displayNamesCulture = culture;
            }
        }

        public string InternalName(ModuleName module) {
            if (!internalNames.TryGetValue(module, out var text)) {
                text = module.ToString();
                internalNames[module] = text;
            }

            return text;
        }

        public string DisplayName(ModuleName module) {
            if (!displayNames.TryGetValue(module, out var text)) {
                text = module.GetDescription();
                displayNames[module] = text;
            }

            return text;
        }

        public bool Matches(Snapshot published) {
            if (published.Modules.Count != Modules.Count) return false;

            foreach (var (name, entry) in Modules) {
                if (!published.Modules.TryGetValue(name, out var existing) || existing != entry) return false;
            }

            return SameNames(published.IncompleteNames, Incomplete)
                   && SameNames(published.UnknownNames, Unknown)
                   && SameNames(published.TravelNames, Travel);
        }

        public Snapshot ToSnapshot() {
            var modules = new Dictionary<string, ModuleEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, entry) in Modules) modules[name] = entry;

            return new Snapshot(modules, new List<string>(Incomplete), new List<string>(Unknown), new List<string>(Travel));
        }

        private static bool SameNames(IReadOnlyList<string> published, List<string> current) {
            if (published.Count != current.Count) return false;

            for (var i = 0; i < current.Count; i++) {
                if (!string.Equals(published[i], current[i], StringComparison.Ordinal)) return false;
            }

            return true;
        }
    }
}
