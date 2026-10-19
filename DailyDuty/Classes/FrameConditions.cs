using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using KamiLib.Extensions;

namespace DailyDuty.Classes;

/// <summary>
/// 每幀都會問的條件旗標。與 KamiLib <see cref="ConditionExtensions"/> 的同名方法讀同樣的旗標、
/// 同樣的短路順序；差別只在不經 <c>Any(params)</c>，所以不會每次配置一個陣列。
/// </summary>
public static class FrameConditions {
    public static bool IsBoundByDuty(ICondition condition)
        => condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];

    public static bool IsInQuestEvent(ICondition condition)
        => condition[ConditionFlag.OccupiedInQuestEvent] || ConditionExtensions.IsIslandDoingSomethingMode();
}
