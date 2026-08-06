using System.Collections.Generic;
using System.Linq;

namespace DailyDuty.Models;

/// <summary>
///     一件收藏品對這個角色的狀態。
///     ⚠️ <see cref="Unknown" /> 刻意放在 0:record struct 的 <c>default</c> 會落在這裡,
///     而「不知道」是唯一在資訊上安全的預設值——把不知道畫成「已取得」或「未取得」都是說謊。
/// </summary>
public enum CollectableState {
    /// <summary>查不到這件道具的解鎖狀態(客戶端沒有這筆 Item row)。不能宣稱有、也不能宣稱沒有。</summary>
    Unknown = 0,

    /// <summary>確認尚未取得。</summary>
    Missing,

    /// <summary>確認已取得。</summary>
    Acquired,
}

public readonly record struct CollectableItemInfo(uint ItemId, byte Type, string Name, CollectableState State);

/// <summary>
///     一個副本的**完整**收藏品清單(不是缺口清單)。
///     舊版只算得出「還缺什麼」,全部取得時整份資訊就消失了;這裡一律帶著全部項目與各自的狀態,
///     顯示端要「只看缺的」或「全部都看」都由顯示端自己決定。
/// </summary>
public sealed class DutyCollectableInfo {
    public required uint CfcId { get; init; }
    public required string DutyName { get; init; }
    public required byte Level { get; init; }
    public required List<CollectableItemInfo> Items { get; init; }

    public int Total => Items.Count;
    public int MissingCount => Items.Count(item => item.State is CollectableState.Missing);
    public int AcquiredCount => Items.Count(item => item.State is CollectableState.Acquired);
    public int UnknownCount => Items.Count(item => item.State is CollectableState.Unknown);
}
