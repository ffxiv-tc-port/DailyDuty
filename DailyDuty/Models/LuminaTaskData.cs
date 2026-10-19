using System.Text.Json.Serialization;

namespace DailyDuty.Models;

// ReSharper disable once UnusedTypeParameter
// Type is used in reflection to display the correct lumina info
public class LuminaTaskData<T> {
    public required uint RowId { get; init; }
    public required bool Complete { get; set; }
    public required int CurrentCount { get; set; }

    /// <summary>資料頁顯示用的列名，第一次畫時算好；不存檔。</summary>
    [JsonIgnore] internal string? DisplayLabel;
}