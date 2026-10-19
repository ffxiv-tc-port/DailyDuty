using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace DailyDuty.Classes;

/// <summary>
/// 登入時第一次讀寫各設定型別，要建 System.Text.Json 的型別中繼資料並 JIT，全部落在框架執行緒上。
/// 插件載入時先在背景跑一遍，登入時就只剩真正的讀寫。
/// </summary>
/// <remarks>
/// 只建立受管理的設定物件、只用 KamiLib 共用的序列化選項（執行緒安全），不碰遊戲狀態、不讀寫檔。
/// 失敗或被取消都只影響第一次登入快慢，不影響功能。
/// </remarks>
public static class SerializerWarmup {
    public static void Run(IReadOnlyCollection<Type> types, CancellationToken token) {
        var options = KamiLib.Configuration.Configuration.SerializerOptions;
        var distinct = types.Distinct().ToList();
        var stopwatch = Stopwatch.StartNew();

        try {
            foreach (var type in distinct) {
                if (token.IsCancellationRequested) return;

                // 空物件 → 序列化 → 再反序列化：讀、寫兩條路徑都走過一次。
                var instance = JsonSerializer.Deserialize("{}", type, options);
                var json = JsonSerializer.Serialize(instance, type, options);
                JsonSerializer.Deserialize(json, type, options);
            }
        }
        catch (Exception ex) {
            if (!token.IsCancellationRequested) {
                Service.Log.Warning(ex, "[SerializerWarmup] Failed; the first login will only take longer.");
            }

            return;
        }

        if (!token.IsCancellationRequested) {
            Service.Log.Debug($"[SerializerWarmup] Warmed {distinct.Count} config types in {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }
    }
}
