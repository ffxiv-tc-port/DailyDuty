using KamiLib.Configuration;

namespace DailyDuty.Models;

public class CollectableConfig {
    public bool Enabled = true;
    public bool MarkDutyList = true;

    public bool ShowMounts = true;
    public bool ShowMinions = true;
    public bool ShowOrchestrionRolls = true;
    public bool ShowTimewornOrchestrionRolls = true;
    public bool ShowTripleTriadCards;
    public bool ShowChocoboBarding = true;
    public bool ShowOther = true;

    public static CollectableConfig Load()
        => Service.PluginInterface.LoadCharacterFile(Service.ClientState.LocalContentId, "Collectables.config.json", () => new CollectableConfig());

    public void Save()
        => Service.PluginInterface.SaveCharacterFile(Service.ClientState.LocalContentId, "Collectables.config.json", this);
}
