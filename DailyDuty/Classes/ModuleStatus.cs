using System;
using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Utility;

namespace DailyDuty.Classes;

public class ColorAttribute(KnownColor internalColor) : Attribute {
    private KnownColor InternalColor { get; } = internalColor;
    public Vector4 Color => InternalColor.Vector();
}

public enum ModuleStatus {
    [Description("Unknown")]
    [Color(KnownColor.Gray)] 
    Unknown,
    
    [Description("Incomplete")]
    [Color(KnownColor.Red)]  
    Incomplete,
    
    [Description("Unavailable")]
    [Color(KnownColor.Orange)]
    Unavailable,
    
    [Description("InProgress")]
    [Color(KnownColor.Aqua)]
    InProgress,
    
    [Description("Complete")]
    [Color(KnownColor.Green)]
    Complete,
    
    [Description("Suppressed")]
    [Color(KnownColor.MediumPurple)]
    Suppressed,
}

public static class ModuleStatusExtensions {
    // 資料頁每幀都讀；屬性查詢每次都要反射。不在列舉定義裡的值照舊直接查。
    private static readonly Vector4[] Colors = BuildColors();

    public static Vector4 GetColor(this ModuleStatus status) {
        var index = (int) status;
        return (uint) index < (uint) Colors.Length ? Colors[index] : status.GetAttribute<ColorAttribute>()!.Color;
    }

    private static Vector4[] BuildColors() {
        var colors = new Vector4[(int) ModuleStatus.Suppressed + 1];
        for (var index = 0; index < colors.Length; index++) {
            colors[index] = ((ModuleStatus) index).GetAttribute<ColorAttribute>()!.Color;
        }

        return colors;
    }
}