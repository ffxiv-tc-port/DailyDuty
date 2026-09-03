using System;
using System.Collections.Generic;
using DailyDuty.Localization;
using Dalamud.Utility;
using NotificationMasterAPI;

namespace DailyDuty.Classes;

/// <summary>
/// Windows-side notification for the one DailyDuty event a player cannot see coming: the
/// daily/weekly reset landing while they are in another window.
///
/// Everything here is opt-in and display only:
/// - The per-module ModuleConfig.TrayNotificationOnReset flag defaults to false, so nothing
///   changes for existing users until they tick the box on the modules they care about.
/// - The tray balloon comes from the NotificationMaster plugin over IPC. Without that plugin
///   the call reports failure and we still flash the taskbar icon, which Dalamud does natively.
/// - Nothing here touches the game or triggers an in-game action.
/// </summary>
public class TrayNotificationController {
    private readonly NotificationMasterApi api = new(Service.PluginInterface);

    // Worth exactly one line per session: this sits on the framework tick, and a reset wave
    // would otherwise repeat the same complaint for every reset from then on.
    private bool loggedUnavailable;

    // A daily reset trips a dozen modules in the same frame. Past a handful of names a tray
    // balloon stops being readable, so the remainder is summarised as a count.
    private const int MaxNamesShown = 4;

    /// <summary>
    /// Raises one notification for a whole reset wave, but only while the game window is in
    /// the background.
    /// </summary>
    /// <param name="moduleNames">Display names of the modules that just reset.</param>
    public void NotifyReset(List<string> moduleNames) {
        if (moduleNames.Count is 0) return;

        // Util.ApplicationIsActivated() asks Windows which window is in the foreground; it reads
        // nothing out of the game. A player looking at the game already has the todo list overlay
        // and the chat message, so a tray balloon on top would only be noise.
        //
        // NotificationMaster exposes an equivalent IsGameWindowActivated over IPC, but that member
        // is private in NotificationMasterAPI - in the 1.0.0.1 package the rest of the fleet uses
        // as well as in current source - so it cannot be called from here. The Dalamud helper is
        // public, does the same foreground-window comparison, and keeps working when
        // NotificationMaster is not installed at all.
        if (Util.ApplicationIsActivated()) return;

        var shown = moduleNames.Count <= MaxNamesShown
            ? moduleNames
            : moduleNames.GetRange(0, MaxNamesShown);

        var names = string.Join(", ", shown);
        if (moduleNames.Count > MaxNamesShown) {
            names += $" (+{moduleNames.Count - MaxNamesShown})";
        }

        try {
            // Title is left to the API, which fills in this plugin's manifest name.
            var delivered = api.DisplayTrayNotification(string.Format(Strings.TrayNotificationResetBody, names));
            if (!delivered && !loggedUnavailable) {
                loggedUnavailable = true;
                Service.Log.Information("Tray notification was requested but NotificationMaster did not accept it - the plugin is probably not installed or not enabled. Falling back to flashing the taskbar icon only.");
            }

            // Always flash, delivered or not. FlashWindow is built into Dalamud, so this half keeps
            // working without NotificationMaster. Its default flashIfOpen=false re-checks the
            // foreground window, so a game that regained focus in between is left alone.
            Util.FlashWindow();
        }
        catch (Exception ex) {
            // DisplayTrayNotification reaches into another plugin over IPC. NotificationMasterApi
            // swallows IpcNotReadyError itself but nothing else, and this runs on the framework
            // tick right after the reset pass - an escaping exception here would take down the
            // whole OnFrameworkUpdate for that frame.
            Service.Log.Information(ex, "Failed to raise a Windows notification, carrying on without one.");
        }
    }
}
