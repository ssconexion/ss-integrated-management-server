using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Discord;
using Discord.Interactions;
using ss.Internal.Management.Server.Resources;

public class ResxLocalizationManager : ILocalizationManager
{
    private readonly string[] supportedLocales = { "en-US", "es-ES" };

    public string GetLocalizedString(string entry, LocalizationTarget target, CultureInfo culture)
    {
        return Strings.ResourceManager.GetString(entry, culture) ?? entry;
    }

    public IDictionary<string, string> GetAllNames(IList<string> key, LocalizationTarget destinationType)
    {
        string resourceKey = key.Last();
        return ExtractTranslations(resourceKey);
    }

    public IDictionary<string, string> GetAllDescriptions(IList<string> key, LocalizationTarget destinationType)
    {
        string resourceKey = key.Last();
        return ExtractTranslations(resourceKey);
    }
    
    private IDictionary<string, string> ExtractTranslations(string resourceKey)
    {
        var translations = new Dictionary<string, string>();

        foreach (var locale in supportedLocales)
        {
            var culture = new CultureInfo(locale);
            
            string translation = Strings.ResourceManager.GetString(resourceKey, culture) ?? string.Empty;
            
            if (!string.IsNullOrWhiteSpace(translation))
            {
                translations[locale] = translation;
            }
        }

        return translations;
    }
}