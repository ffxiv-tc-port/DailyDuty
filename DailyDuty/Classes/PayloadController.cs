using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiLib.Extensions;
using Lumina.Excel.Sheets;

namespace DailyDuty.Classes;

public enum PayloadId : uint {
    OpenWondrousTailsBook,
    IdyllshireTeleport,
    DomanEnclaveTeleport,
    OpenDutyFinderRoulette,
    OpenDutyFinderRaid,
    OpenDutyFinderAllianceRaid,
    GoldSaucerTeleport,
    OpenPartyFinder,
    UldahTeleport,
    Unknown,
    OpenChallengeLog,
}

public unsafe class PayloadController : IDisposable {
    /// <summary>
    /// Command id base for the per-module "open this module" links. Kept well clear of the
    /// <see cref="PayloadId"/> values, which start at 0 - the two ranges share one namespace.
    /// </summary>
    private const uint ModulePayloadBase = 0x1000;

    private readonly Dictionary<PayloadId, DalamudLinkPayload> payloads = new();
    private readonly Dictionary<ModuleName, DalamudLinkPayload> modulePayloads = new();

    public PayloadController() {
        foreach (var payload in Enum.GetValues<PayloadId>()) {
            payloads.Add(payload, RegisterPayload(payload));
        }

        RegisterModulePayloads();
    }

    public void Dispose() {
        foreach (var registeredPayload in payloads) {
            Service.Chat.RemoveChatLinkHandler((uint)registeredPayload.Key);
        }

        foreach (var registeredPayload in modulePayloads) {
            Service.Chat.RemoveChatLinkHandler(ModulePayloadBase + (uint)registeredPayload.Key);
        }
    }

    public DalamudLinkPayload GetPayload(PayloadId id) {
        if (payloads.TryGetValue(id, out var value)) {
            return value;
        }

        throw new Exception("Tried to get payload that isn't registered.");
    }

    /// <summary>
    /// Link payload that opens the configuration window on <paramref name="moduleName"/>.
    /// Returns null when that module has no registered handler - callers must fall back to
    /// plain text rather than skipping the message.
    /// </summary>
    public DalamudLinkPayload? GetModulePayload(ModuleName moduleName)
        => modulePayloads.GetValueOrDefault(moduleName);

    /// <summary>
    /// One chat link handler per module, so any module's chat reminder can offer a link
    /// straight to its settings. Registration failures are logged and skipped - a module
    /// without a payload still gets its reminder, just without the link.
    /// </summary>
    private void RegisterModulePayloads() {
        foreach (var moduleName in Enum.GetValues<ModuleName>()) {
            if (moduleName is ModuleName.Unknown or ModuleName.TestModule) continue;

            try {
                var payload = Service.Chat.AddChatLinkHandler(ModulePayloadBase + (uint)moduleName, (_, _) => OpenModule(moduleName));
                modulePayloads.Add(moduleName, payload);
            }
            catch (Exception ex) {
                Service.Log.Warning(ex, $"[PayloadController] Could not register the module link for {moduleName}, its reminders will be plain text.");
            }
        }
    }

    /// <summary>
    /// Reads System.ConfigurationWindow at click time rather than capturing it: the payload
    /// controller is constructed before the window exists.
    /// </summary>
    private static void OpenModule(ModuleName moduleName) {
        try {
            System.ConfigurationWindow?.OpenToModule(moduleName);
        }
        catch (Exception ex) {
            Service.Log.Error(ex, $"[PayloadController] Failed to open the window for {moduleName}");
        }
    }

    private static DalamudLinkPayload RegisterPayload(PayloadId id) 
        => AddHandler(id, GetDelegateForPayload(id));

    public static Action<uint, SeString> GetDelegateForPayload(PayloadId payload) => payload switch {
        PayloadId.OpenWondrousTailsBook => (_, _) => {
            const uint wondrousTailsBookItemId = 2002023;
                
            if (InventoryManager.Instance()->GetInventoryItemCount(wondrousTailsBookItemId) == 1) {
                AgentInventoryContext.Instance()->UseItem(wondrousTailsBookItemId);
            }
        },
        PayloadId.IdyllshireTeleport => (_, _) => {
            System.Teleporter.Teleport(75);
        },
        PayloadId.DomanEnclaveTeleport => (_, _) => {
            System.Teleporter.Teleport(127);
        },
        PayloadId.OpenDutyFinderRoulette => (_, _) => {
            AgentContentsFinder.Instance()->OpenRouletteDuty(1);
            ClearDutyFinderSelection();
        },
        PayloadId.OpenDutyFinderRaid => (_, _) => {
            var currentRaid = Service.DataManager.GetLimitedNormalRaidDuties().LastOrDefault();

            AgentContentsFinder.Instance()->OpenRegularDuty(currentRaid.RowId); 
            ClearDutyFinderSelection();
        },
        PayloadId.OpenDutyFinderAllianceRaid => (_, _) => {
            // 原本自己用 Unknown33/Unknown28 重查一次再 .Last()，序列為空時直接丟例外
            // ——點擊團隊任務提醒連結就會炸。改用模組本身在用的同一個 helper，
            // 並比照隔壁的 OpenDutyFinderRaid 用 LastOrDefault + RowId 守衛。
            var currentAllianceRaid = Service.DataManager.GetLimitedAllianceRaidDuties().LastOrDefault();

            if (currentAllianceRaid.RowId is 0) {
                Service.Log.Warning("[PayloadController] 找不到任何週限團隊任務，略過開啟任務搜尋器。");
                return;
            }

            AgentContentsFinder.Instance()->OpenRegularDuty(currentAllianceRaid.RowId);
            ClearDutyFinderSelection();
        },
        PayloadId.GoldSaucerTeleport => (_, _) => {
            System.Teleporter.Teleport(62);
        },
        PayloadId.OpenPartyFinder => (_, _) => {
                Framework.Instance()->GetUIModule()->ExecuteMainCommand(57);
        },
        PayloadId.UldahTeleport => (_, _) => {
            System.Teleporter.Teleport(9);
        },
        PayloadId.Unknown => (_, _) => {
            Service.Log.Debug("Executed Unknown Payload.");
        },
        PayloadId.OpenChallengeLog => (_, _) => {
            Framework.Instance()->GetUIModule()->ExecuteMainCommand(60);
        },
        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, null),
    };
    
    private static DalamudLinkPayload AddHandler(PayloadId payloadId, Action<uint, SeString> action)
        => Service.Chat.AddChatLinkHandler((uint) payloadId, action);

    private static void ClearDutyFinderSelection() {
        var returnValue = stackalloc AtkValue[1];
        var command = stackalloc AtkValue[2];
        command[0].SetInt(12);
        command[1].SetInt(1);
                
        AgentContentsFinder.Instance()->ReceiveEvent(returnValue, command, 2, 0);
    }
}