using System;

namespace DailyDuty.Classes;

/// <summary>
/// 倒數文字的單格快取。<see cref="Time.FormatTimespan"/> 的輸出只跟「截去小數的秒數」與
/// 「要不要顯示秒」有關，所以同一秒內回同一個字串，不重組。
/// </summary>
/// <remarks>不是執行緒安全的：一個計時器一份，只從遊戲主執行緒用。</remarks>
public sealed class TimeRemainingText {
    private long wholeSeconds;
    private bool hideSeconds;
    private string? text;

    public string Get(TimeSpan remaining, bool hide) {
        var seconds = remaining.Ticks / TimeSpan.TicksPerSecond;

        if (text is null || seconds != wholeSeconds || hide != hideSeconds) {
            wholeSeconds = seconds;
            hideSeconds = hide;
            text = remaining.FormatTimespan(hide);
        }

        return text;
    }
}
