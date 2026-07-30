using DailyDuty.Classes;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using KamiLib.Extensions;

namespace DailyDuty.Models;

public class LinkedStatusMessage : StatusMessage {
    public PayloadId Payload { get; init; }

    public required bool LinkEnabled { get; init; }

    public override void PrintMessage() {
        var builder = BuildPrefix(SourceModule.GetDescription());

        if (LinkEnabled) {
            builder
                .Add(System.PayloadController.GetPayload(Payload))
                .AddUiForeground(Message, 576)
                .Add(RawPayload.LinkTerminator);
        }
        else {
            builder.AddUiForeground(Message, 576);
        }

        // Second, separate link: the module's own settings. Distinct from the action link
        // above, which does whatever that module's payload does (open the duty finder, ...).
        AppendModuleLink(builder, SourceModule);

        Service.Chat.Print(new XivChatEntry {
            Type = MessageChannel,
            Message = builder.Build(),
        });
    }
}
