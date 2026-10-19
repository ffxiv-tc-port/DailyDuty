using System;
using DailyDuty.Classes;
using DailyDuty.Localization;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using KamiLib.Extensions;

namespace DailyDuty.Models;

public class StatusMessage {
    public ModuleName SourceModule { get; set; }
    public XivChatType MessageChannel { get; set; } = XivChatType.Debug;
    public string Message { get; set; } = string.Empty;

    public virtual void PrintMessage() {
        var builder = BuildPrefix(SourceModule.GetDescription())
            .AddText(Message);

        AppendModuleLink(builder, SourceModule);

        Service.Chat.Print(new XivChatEntry {
            Type = MessageChannel,
            Message = builder.Build(),
        });
    }

    public static void PrintTaggedMessage(string message, ModuleName sourceModule) {
        var builder = BuildPrefix(sourceModule.GetDescription())
            .AddText(message);

        AppendModuleLink(builder, sourceModule);

        Service.Chat.Print(new XivChatEntry {
            Type = XivChatType.Debug,
            Message = builder.Build(),
        });
    }

    /// <summary>The shared "[DailyDuty] [Module] " prefix every chat message starts with.</summary>
    protected static SeStringBuilder BuildPrefix(string tag) {
        var dailyDutyLabel = DateTime.Today is { Month: 4, Day: 1 } ? "DankDuty" : "DailyDuty";

        return new SeStringBuilder()
            .AddUiForeground($"[{dailyDutyLabel}] ", 45)
            .AddUiForeground($"[{tag}] ", 62);
    }

    /// <summary>
    /// Appends the clickable "[Open]" link that jumps to the module that sent the message.
    ///
    /// Degrades silently: if the payload controller is not up yet, or that module's link
    /// handler failed to register, the message still goes out - just as plain text. A missing
    /// link must never cost the player a reminder.
    /// </summary>
    protected static void AppendModuleLink(SeStringBuilder builder, ModuleName sourceModule) {
        try {
            if (System.PayloadController?.GetModulePayload(sourceModule) is not { } payload) return;

            // The LinkMarker glyph is the game's own "this is clickable" cue - the same one
            // item and map links use - so the link reads as one without extra explanation.
            builder
                .AddText(" ")
                .Add(payload)
                .AddUiForeground($"{(char)SeIconChar.LinkMarker}{Strings.OpenModuleLink}", 576)
                .Add(RawPayload.LinkTerminator);
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, $"[StatusMessage] Could not attach the module link for {sourceModule}");
        }
    }

    public static implicit operator StatusMessage(string message) => new() { Message = message };
}
