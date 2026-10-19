using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Threading;
using DailyDuty.CustomNodes;
using KamiToolKit.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DailyDuty.Classes;

/// <summary>
/// 介面節點的樣式檔走 Newtonsoft（KamiToolKit NodeBase.Load 的 PopulateObject）：第一次要替每個節點型別建
/// contract，讀取路徑本身也要 JIT，整個插件載入只付一次，但付在第一次登入的那一幀。這裡在背景先做。
/// </summary>
/// <remarks>
/// 只做反射，加上一次對純受管理物件的 PopulateObject；不建立任何節點（節點建構子會配置遊戲記憶體）。
/// 預設的 contract 快取由所有 JsonConvert 呼叫共用、執行緒安全，和登入同時跑也只是重複建同一份。
/// </remarks>
public static class StyleContractWarmup {
    private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // 登入時會 Load 樣式檔的節點型別；子節點型別由 ResolveTree 沿 [JsonProperty] 成員找出來。
    private static readonly Type[] StyleRoots = [typeof(TimerNode), typeof(TodoCategoryNode), typeof(TodoTaskNode), typeof(ListBoxNode)];

    private const string ShapeJson = """{"TextColor":{"X":1.0,"Y":1.0,"Z":1.0,"W":1.0},"TextFlags":9,"FontSize":12,"String":"x","Margin":{"Top":1.0,"Left":1.0,"Right":1.0,"Bottom":1.0},"Size":{"X":22.0,"Y":17.0},"IsVisible":false}""";

    public static void Run(CancellationToken token) {
        var stopwatch = Stopwatch.StartNew();
        var seen = new HashSet<Type>();

        try {
            var resolver = JsonSerializer.CreateDefault().ContractResolver;

            foreach (var root in StyleRoots) {
                if (token.IsCancellationRequested) return;

                ResolveTree(root, resolver, seen);
            }

            JsonConvert.PopulateObject(ShapeJson, new StyleShape());
        }
        catch (Exception ex) {
            if (!token.IsCancellationRequested) {
                Service.Log.Warning(ex, "[StyleContractWarmup] Failed; the first login will only take longer.");
            }

            return;
        }

        if (!token.IsCancellationRequested) {
            Service.Log.Debug($"[StyleContractWarmup] Resolved {seen.Count} style node types in {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }
    }

    // PopulateObject 逐層填既有子節點時碰到的，就是 [JsonProperty] 成員的型別。
    private static void ResolveTree(Type type, IContractResolver resolver, HashSet<Type> seen) {
        if (!seen.Add(type)) return;

        resolver.ResolveContract(type);

        for (var current = type; current is not null && current != typeof(object); current = current.BaseType) {
            foreach (var member in current.GetMembers(MemberFlags)) {
                var memberType = member switch {
                    FieldInfo field => field.FieldType,
                    PropertyInfo property => property.PropertyType,
                    _ => null,
                };

                if (memberType is null || !member.IsDefined(typeof(JsonPropertyAttribute), true)) continue;
                if (memberType.IsPrimitive || memberType.IsEnum || memberType.IsPointer || memberType == typeof(string)) continue;

                ResolveTree(memberType, resolver, seen);
            }
        }
    }

    // 與樣式檔同形（向量、巢狀物件、字串、數值、布林），只為了讓讀取路徑先 JIT 過。
    private sealed class StyleShape {
        public Vector4 TextColor { get; set; }
        public int TextFlags { get; set; }
        public uint FontSize { get; set; }
        public string String { get; set; } = string.Empty;
        public ShapeMargin Margin { get; } = new();
        public Vector2 Size { get; set; }
        public bool IsVisible { get; set; }
    }

    private sealed class ShapeMargin {
        public float Top { get; set; }
        public float Left { get; set; }
        public float Right { get; set; }
        public float Bottom { get; set; }
    }
}
