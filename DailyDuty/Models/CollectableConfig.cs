using KamiLib.Configuration;

namespace DailyDuty.Models;

public class CollectableConfig {
    public bool Enabled = true;

    // ⚠️ 舊欄位 MarkDutyList(在原生任務列表逐列畫金星)已於 v7.20.0.25 移除。
    // 使用者設定檔裡殘留的那個鍵會被 Newtonsoft 忽略,不需要遷移。

    public bool ShowMounts = true;
    public bool ShowMinions = true;
    public bool ShowOrchestrionRolls = true;
    public bool ShowTimewornOrchestrionRolls = true;
    public bool ShowTripleTriadCards;
    public bool ShowChocoboBarding = true;
    public bool ShowOther = true;

    public static CollectableConfig Load()
        => Service.PluginInterface.LoadCharacterFile(Service.PlayerState.ContentId, "Collectables.config.json", () => new CollectableConfig());

    public void Save()
        => Service.PluginInterface.SaveCharacterFile(Service.PlayerState.ContentId, "Collectables.config.json", this);
}
