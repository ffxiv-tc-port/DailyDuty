using System;
using System.Globalization;
using DailyDuty.Localization;
using KamiLib.Extensions;

namespace DailyDuty.Classes;

public class LocalizationController : IDisposable {
    public LocalizationController() {
        OnLanguageChange(Service.PluginInterface.UiLanguage);
        Service.PluginInterface.LanguageChanged += OnLanguageChange;

        EnumExtensions.GetCultureInfoFunc = () => Strings.Culture;
        EnumExtensions.GetResourceManagerFunc = () => Strings.ResourceManager;
    }
    
    public void Dispose() {
        Service.PluginInterface.LanguageChanged -= OnLanguageChange;
    }

    private void OnLanguageChange(string languageCode) {
        try {
            // TC Dalamud reports UiLanguage as "tw", which CultureInfo resolves to Twi (Ghana),
            // not Traditional Chinese - map it (and any zh* variant) onto the shipped zh-Hant satellite.
            if (languageCode is "tw" || languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) {
                languageCode = "zh-Hant";
            }

            Service.Log.Information($"Loading Localization for {languageCode}");
            Strings.Culture = new CultureInfo(languageCode);
        }
        catch (Exception ex) {
            Service.Log.Error(ex, "Unable to Load Localization");
        }
    }
}