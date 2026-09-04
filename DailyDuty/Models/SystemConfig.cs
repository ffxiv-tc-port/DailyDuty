using FFXIVClientStructs.FFXIV.Client.Game.UI;
using KamiLib.Configuration;

namespace DailyDuty.Models;

public unsafe class SystemConfig : CharacterConfiguration {
    public bool HideDisabledModules = false;

    /// <summary>
    /// 重置之後如果還有沒做完的項目,請 TataruPraise 念一句。
    /// </summary>
    /// <remarks>
    /// 預設開啟。這不是「多一個提示音」——沒裝 TataruPraise 的人整條路徑是 no-op,
    /// 裝了的人也可以在 TataruPraise 自己的設定視窗單獨關掉「每日重置」這個情境。
    /// <para>
    /// 📌 這裡放的是<b>欄位初始值</b>而不是 [DefaultValue]:設定檔走 System.Text.Json,
    /// 既有使用者的 json 裡沒有這個鍵時反序列化不會動到欄位,初始值就是他們拿到的值。
    /// </para>
    /// </remarks>
    public bool SpeakOnReset = true;

    public static SystemConfig Load() {
        var config = Service.PluginInterface.LoadCharacterFile(PlayerState.Instance()->ContentId, "System.config.json", () => {
            var newConfig = new SystemConfig();
            newConfig.UpdateCharacterData();

            return newConfig;
        });
        
        Service.Log.Debug($"[DailyDutySystem] Logging into character: {PlayerState.Instance()->CharacterNameString}, updating System.config.json");
        config.UpdateCharacterData();
        config.Save();

        return config;
    }

    public void Save()
        => Service.PluginInterface.SaveCharacterFile(PlayerState.Instance()->ContentId, "System.config.json", this);
}