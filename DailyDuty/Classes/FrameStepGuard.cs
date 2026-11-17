using System;
using System.Collections.Generic;

namespace DailyDuty.Classes;

/// <summary>
/// 每幀路徑的逐步隔離：一步擲例外只賠掉它自己那一步，同幀其餘步驟照跑。
/// 同一個 key 第一次照記，之後每分鐘最多一行並附上期間略過的次數。
/// </summary>
/// <remarks>只在框架執行緒使用（無鎖）；不用 EzThrottler（本插件不引 ECommons）。</remarks>
public static class FrameStepGuard {
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly Dictionary<string, (DateTime LastUtc, int Suppressed)> State = [];

    public static void Run(string key, Action step) {
        try {
            step();
        }
        catch (Exception ex) {
            Report(key, ex);
        }
    }

    public static void Report(string key, Exception ex) {
        var now = DateTime.UtcNow;
        State.TryGetValue(key, out var state);

        if (state.LastUtc != default && now - state.LastUtc < Interval) {
            State[key] = (state.LastUtc, state.Suppressed + 1);
            return;
        }

        State[key] = (now, 0);

        if (state.Suppressed > 0) {
            Service.Log.Error(ex, $"[{key}] 每幀更新仍在擲例外，本步略過、其餘照跑（上一分鐘另有 {state.Suppressed} 次未記）。");
        }
        else {
            Service.Log.Error(ex, $"[{key}] 每幀更新擲例外，本步略過、其餘照跑。");
        }
    }
}
