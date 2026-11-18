using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace DailyDuty.Classes;

/// <summary>
/// 第一次登入那一幀要 JIT 模組、疊加層節點（含 KamiToolKit）的程式碼，還要第一次開登入路徑用到的資料表。
/// 插件載入時先在背景做掉，登入幀只剩真正的讀檔與建節點。
/// </summary>
/// <remarks>
/// 契約：只呼叫 <see cref="RuntimeHelpers.PrepareMethod(RuntimeMethodHandle)"/>（只編譯、不執行方法本體，也不跑靜態建構子）
/// 與讀 Lumina 資料表（快取是並行字典）；不碰遊戲記憶體、不建節點、不呼叫別的插件。
/// 失敗、被取消或還沒跑完就登入，都只影響第一次登入快慢，不影響功能。
/// </remarks>
public static class LoginWarmup {
    private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly string[] RootNamespaces = ["DailyDuty.Modules", "DailyDuty.Modules.BaseModules", "DailyDuty.Models", "DailyDuty.CustomNodes"];

    private static readonly Type[] RootTypes = [
        typeof(ModuleController), typeof(TodoListController), typeof(TimersController), typeof(OverlayController),
        typeof(PayloadController), typeof(Time), typeof(TimeRemainingText),
        typeof(LuminaTaskUpdater<ContentsNote>), typeof(LuminaTaskUpdater<ContentRoulette>), typeof(LuminaTaskUpdater<ClassJob>),
        typeof(LuminaTaskUpdater<MobHuntOrderType>), typeof(LuminaTaskUpdater<Addon>),
    ];

    private static long finishedTimestamp;

    /// <summary>0＝還沒跑完（或失敗、被取消）；否則是跑完那一刻的 <see cref="Stopwatch.GetTimestamp"/>。</summary>
    public static long FinishedTimestamp => Interlocked.Read(ref finishedTimestamp);

    public static void Run(CancellationToken token) {
        var stopwatch = Stopwatch.StartNew();

        try {
            var methods = PrepareJit(token);
            if (token.IsCancellationRequested) return;

            WarmSheets();

            if (token.IsCancellationRequested) return;

            Interlocked.Exchange(ref finishedTimestamp, Stopwatch.GetTimestamp());
            Service.Log.Debug($"[LoginWarmup] Compiled {methods} methods and opened login sheets in {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex) {
            if (!token.IsCancellationRequested) {
                Service.Log.Warning(ex, "[LoginWarmup] Failed; the first login will only take longer.");
            }
        }
    }

    /// <summary>只做反射與 JIT，不經過 Dalamud 服務（離線探針直接呼叫這一段）。回傳編譯成功的方法數。</summary>
    public static int PrepareJit(CancellationToken token) {
        var plugin = typeof(LoginWarmup).Assembly;
        var allowed = new HashSet<Assembly> { plugin, typeof(KamiToolKit.NativeController).Assembly, typeof(KamiLib.Classes.Reflection).Assembly };

        Type?[] pluginTypes;
        try {
            pluginTypes = plugin.GetTypes();
        }
        catch (ReflectionTypeLoadException ex) {
            pluginTypes = ex.Types;
        }

        var pending = new Queue<Type>(RootTypes);
        foreach (var type in pluginTypes) {
            if (type?.Namespace is { } ns && RootNamespaces.Contains(ns)) pending.Enqueue(type);
        }

        var seen = new HashSet<Type>();
        var prepared = 0;

        while (pending.TryDequeue(out var type)) {
            if (token.IsCancellationRequested) return prepared;
            if (type.ContainsGenericParameters || !allowed.Contains(type.Assembly) || !seen.Add(type)) continue;

            try {
                prepared += PrepareType(type, pending);
            }
            catch (Exception) {
                // 型別本身反射不了（相依載不到之類）就跳過它，其餘照做。
            }
        }

        return prepared;
    }

    private static int PrepareType(Type type, Queue<Type> pending) {
        // 登入路徑會碰到的型別大致就是「基底鏈＋欄位型別」閉包：模組→設定／資料清單、疊加層→KamiToolKit 子節點。
        if (type.BaseType is { } baseType) pending.Enqueue(baseType);
        foreach (var field in type.GetFields(MemberFlags)) {
            var fieldType = field.FieldType;
            pending.Enqueue(fieldType.IsArray ? fieldType.GetElementType()! : fieldType);
            if (fieldType.IsGenericType) {
                foreach (var argument in fieldType.GetGenericArguments()) pending.Enqueue(argument);
            }
        }

        var prepared = 0;
        var instantiation = type.IsGenericType ? type.GetGenericArguments().Select(argument => argument.TypeHandle).ToArray() : null;
        foreach (var method in type.GetMethods(MemberFlags).Cast<MethodBase>().Concat(type.GetConstructors(MemberFlags))) {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;

            try {
                if (instantiation is null) {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                else {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle, instantiation);
                }

                prepared++;
            }
            catch (Exception) {
                // 個別方法編不了（例：UnmanagedCallersOnly）就留給第一次呼叫時再編。
            }
        }

        return prepared;
    }

    // 登入路徑上各模組讀的表；Where／ToList 與 LuminaTaskUpdater 用的是同一組泛型實例化（每個列型別各 JIT 一份）。
    private static void WarmSheets() {
        Scan<ContentsNote>();
        Scan<ContentRoulette>();
        Scan<MobHuntOrderType>();
        Scan<TreasureHuntRank>();
        Open<ClassJob>();
        Open<Addon>();
        Open<World>();
        Open<WorldDCGroupType>();
        Open<ContentFinderCondition>();
        Service.DataManager.GetSubrowExcelSheet<MobHuntOrder>().TryGetSubrow(0, 0, out _);
    }

    private static void Scan<T>() where T : struct, IExcelRow<T>
        => _ = Service.DataManager.GetExcelSheet<T>().Where(static _ => false).ToList();

    private static void Open<T>() where T : struct, IExcelRow<T>
        => Service.DataManager.GetExcelSheet<T>().TryGetRow(0, out _);
}
