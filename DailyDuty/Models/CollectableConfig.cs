using KamiLib.Configuration;

namespace DailyDuty.Models;

public class CollectableConfig {
    public bool Enabled = true;

    // ⚠️ 使用者設定檔裡可能殘留舊欄位 MarkDutyList 的鍵(功能已移除);
    // 反序列化會忽略它,不需要遷移。

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
