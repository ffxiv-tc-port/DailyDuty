using System;
using System.Globalization;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace DailyDuty.Models;

// ReSharper disable once UnusedTypeParameter
// Type is used in reflection to display the correct lumina info
public class LuminaTaskConfig<T> {
    public required uint RowId { get; init; }
    public required bool Enabled { get; set; }
    public required int TargetCount { get; set; }

    public string Label() => this switch {
        LuminaTaskConfig<ContentRoulette> => RowLabel<ContentRoulette>(static row => row.Name.ToString()),
        LuminaTaskConfig<ClassJob> => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(RowLabel<ClassJob>(static row => row.Name.ToString())),
        LuminaTaskConfig<MobHuntOrderType> => GetMobHuntOrderTypeString(RowId),
        LuminaTaskConfig<Addon> => RowLabel<Addon>(static row => row.Text.ToString()),
        LuminaTaskConfig<ContentFinderCondition> => RowLabel<ContentFinderCondition>(static row => row.Name.ToString()),
        LuminaTaskConfig<ContentsNote> => RowLabel<ContentsNote>(static row => row.Name.ToString()),
        _ => throw new Exception("Data Type Not Registered"),
    };

    /// <summary>
    /// TaskConfig 是持久化的設定，只增不減，所以 RowId 可能是跨版本殘留、在目前的資料表裡
    /// 根本不存在的列。裸 GetRow 查無此列時 Lumina 會擲例外，而 Label() 全部都在 Draw
    /// 路徑上被呼叫，一擲整個模組視窗就不見。查不到時把 id 直接顯示成「?<id>」，
    /// 讓使用者看得見是哪一筆設定失效了。這裡刻意寫成泛型，所有 T 走的都是同一條退路。
    /// </summary>
    private string RowLabel<TRow>(Func<TRow, string> selector) where TRow : struct, IExcelRow<TRow> {
        var row = Service.DataManager.GetExcelSheet<TRow>().GetRowOrDefault(RowId);
        return row is null ? $"?{RowId}" : selector(row.Value);
    }

    private static string GetMobHuntOrderTypeString(uint row) {
        if (!Service.DataManager.GetExcelSheet<MobHuntOrderType>().TryGetRow(row, out var itemInfo)) return $"?{row}";

        // EventItem 的 RowRef 也可能指向不存在的列，.Value 一樣會擲例外，改用 ValueNullable。
        if (itemInfo.EventItem.ValueNullable is not { } eventItemRow) return $"?{row}";

        var eventItem = eventItemRow.Name.ToString();
        if(eventItem == string.Empty) eventItem = eventItemRow.Singular.ToString();

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(eventItem);
    }
}
