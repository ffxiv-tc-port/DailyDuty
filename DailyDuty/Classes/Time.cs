using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace DailyDuty.Classes;

public static class Time {
    private static DateTime GetNextDateTimeForHour(int hours)
        => DateTime.UtcNow.Hour < hours ? DateTime.UtcNow.Date.AddHours(hours) : DateTime.UtcNow.Date.AddDays(1).AddHours(hours);

    public static DateTime NextDailyReset()
        => GetNextDateTimeForHour(15);

    public static DateTime NextWeeklyReset()
        => NextDayOfWeek(DayOfWeek.Tuesday, 8);

    public static DateTime NextFashionReportReset()
        => NextWeeklyReset().AddDays(3);

    public static DateTime NextGrandCompanyReset()
        => GetNextDateTimeForHour(20);

    public static DateTime NextLeveAllowanceReset() {
        var now = DateTime.UtcNow;

        return now.Hour < 12 ? now.Date.AddHours(12) : now.Date.AddDays(1);
    }

    private static DateTime NextDayOfWeek(DayOfWeek weekday, int hour) {
        var today = DateTime.UtcNow;

        if (today.Hour < hour && today.DayOfWeek == weekday) {
            return today.Date.AddHours(hour);
        }
        var nextReset = today.AddDays(1);

        while (nextReset.DayOfWeek != weekday) {
            nextReset = nextReset.AddDays(1);
        }

        return nextReset.Date.AddHours(hour);
    }

    public class DatacenterException : Exception;
    
    public static unsafe DateTime NextJumboCactpotReset() {
        // AgentLobby.Instance() 是 CS 的 [Agent] 產生器版本，展開後逐字是
        // `agentModule == null ? null : (AgentLobby*)agentModule->GetAgentByInternalId(...)`
        // ——兩層都合法會回 null（UIModule 尚未建立、代理人尚未配置）。這支經由
        // JumboCactpot.GetNextReset() 在模組重設流程裡被呼叫，登入流程早期就會跑到。
        // 解參考 null 是 AccessViolation，而 AVE 在 .NET Core 是 corrupted-state exception，
        // try/catch 攔不到 ⇒ 只能在解參考之前擋。
        // 退化路徑沿用本方法既有的失敗語意：擲 DatacenterException，唯一呼叫端已經
        // 接住它並退成「一天後再算一次」，不需要新的例外型別或新的呼叫端處理。
        var lobby = AgentLobby.Instance();
        if (lobby is null) throw new DatacenterException();

        var worldId = lobby->LobbyData.HomeWorldId;
        var world = Service.DataManager.GetExcelSheet<World>().GetRow(worldId);
        var region = Service.DataManager.GetExcelSheet<WorldDCGroupType>().GetRow(world.DataCenter.RowId).Region;

        return region switch {
            // Japan
            1 => NextDayOfWeek(DayOfWeek.Saturday, 12),

            // North America
            2 => NextDayOfWeek(DayOfWeek.Sunday, 2),

            // Europe
            3 => NextDayOfWeek(DayOfWeek.Saturday, 19),

            // Australia
            4 => NextDayOfWeek(DayOfWeek.Saturday, 9),
            
            // Cloud
            7 => NextDayOfWeek(DayOfWeek.Sunday, 2),

            // 台灣（陸行鳥 DataCenter=151，WorldDCGroupType.Region=8）
            // ⚠️ NextDayOfWeek 收的時數是 UTC（它用 DateTime.UtcNow），不是當地時間。
            // 台服「仙人仙彩」開獎為當地週六 21:00（使用者實機確認），台灣 UTC+8 → UTC 週六 13:00。
            // 與上面日服那筆互相印證：日服同樣是當地週六 21:00、UTC+9，表裡寫的就是 12；台服晚一小時＝13。
            8 => NextDayOfWeek(DayOfWeek.Saturday, 13),

            // Unknown Region
            _ => throw new DatacenterException(),
        };
    }

    public static string FormatTimespan(this TimeSpan timeSpan, bool hideSeconds = false)
        => hideSeconds ? 
               $"{timeSpan.Days:0}.{timeSpan.Hours:00}:{timeSpan.Minutes:00}" : 
               $"{timeSpan.Days:0}.{timeSpan.Hours:00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
    
    public static string FormatTimeSpanShort(this TimeSpan timeSpan, bool hideSeconds = false)
        => hideSeconds ? 
               $"{timeSpan.Hours:00}:{timeSpan.Minutes:00}" : 
               $"{timeSpan.Hours:00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}"; 
}