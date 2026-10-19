using KamiLib.Configuration;

namespace DailyDuty.Models;

public class TimersConfig {
    public bool Enabled = false;
	
    public bool HideInDuties = true;
    public bool HideInQuestEvents = true;
    public bool HideTimerSeconds = false;

    public bool EnableDailyTimer = true;
    public bool EnableWeeklyTimer = true;
    
    public static TimersConfig Load() 
        => Service.PluginInterface.LoadCharacterFile(Service.PlayerState.ContentId, "Timers.config.json", () => new TimersConfig());

    public void Save()
        => Service.PluginInterface.SaveCharacterFile(Service.PlayerState.ContentId, "Timers.config.json", this);
}
