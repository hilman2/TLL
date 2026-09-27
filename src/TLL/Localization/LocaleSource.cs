using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Colossal;
using Newtonsoft.Json;
using TLL.Core.Planning;

namespace TLL.Localization
{
    /// <summary>
    /// Feeds one language to the game's localization manager.
    ///
    /// Texts live in Localization/&lt;locale&gt;.json, embedded in the DLL.
    /// Keys starting with "Settings." and "Enum." are translated to the IDs
    /// the options screen asks for; all other keys are passed through with a
    /// "TLL." prefix for the in-game panel. A key missing in a language falls
    /// back to the English text, so a partial translation never shows IDs.
    /// </summary>
    internal sealed class LocaleSource : IDictionarySource
    {
        private const string kResourcePrefix = "TLL.Localization.";
        private const string kFallbackLocale = "en-US";

        private readonly Dictionary<string, string> m_Entries;

        private LocaleSource(Dictionary<string, string> entries)
        {
            m_Entries = entries;
        }

        /// <summary>Registers every embedded language with the game.</summary>
        public static void RegisterAll(Setting setting)
        {
            Assembly assembly = typeof(LocaleSource).Assembly;
            Dictionary<string, string> fallback = Read(assembly, kFallbackLocale) ?? new Dictionary<string, string>();
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(kResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
                    continue;
                string locale = resource.Substring(kResourcePrefix.Length, resource.Length - kResourcePrefix.Length - ".json".Length);
                Dictionary<string, string> texts = Read(assembly, locale);
                if (texts == null)
                    continue;
                foreach (KeyValuePair<string, string> entry in fallback)
                {
                    if (!texts.ContainsKey(entry.Key))
                        texts[entry.Key] = entry.Value;
                }
                Game.SceneFlow.GameManager.instance.localizationManager.AddSource(locale, new LocaleSource(ToGameIds(texts, setting)));
            }
        }

        private static Dictionary<string, string> Read(Assembly assembly, string locale)
        {
            using (Stream stream = assembly.GetManifestResourceStream(kResourcePrefix + locale + ".json"))
            {
                if (stream == null)
                    return null;
                using (var reader = new StreamReader(stream))
                {
                    try
                    {
                        return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
                    }
                    catch (JsonException e)
                    {
                        Mod.Log.Error(e, $"Localization file {locale}.json is broken and was skipped.");
                        return null;
                    }
                }
            }
        }

        private static Dictionary<string, string> ToGameIds(Dictionary<string, string> texts, Setting setting)
        {
            var result = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> entry in texts)
            {
                string id = GameId(entry.Key, setting);
                if (id != null)
                    result[id] = entry.Value;
            }
            return result;
        }

        /// <summary>The game's ID for one of our keys, or null for a key without a place.</summary>
        private static string GameId(string key, Setting setting)
        {
            string[] parts = key.Split('.');
            if (parts[0] == "Settings")
            {
                if (parts.Length == 2 && parts[1] == "Title")
                    return setting.GetSettingsLocaleID();
                if (parts.Length == 2 && parts[1] == "BindingMap")
                    return setting.GetBindingMapLocaleID();
                if (parts.Length == 3 && parts[1] == "Binding")
                    return setting.GetBindingKeyLocaleID(parts[2]);
                if (parts.Length != 3)
                    return null;
                switch (parts[1])
                {
                    case "Tab":
                        return setting.GetOptionTabLocaleID(parts[2]);
                    case "Group":
                        return setting.GetOptionGroupLocaleID(parts[2]);
                }
                switch (parts[2])
                {
                    case "Label":
                        return setting.GetOptionLabelLocaleID(parts[1]);
                    case "Description":
                        return setting.GetOptionDescLocaleID(parts[1]);
                    case "Warning":
                        return setting.GetOptionWarningLocaleID(parts[1]);
                }
                return null;
            }
            if (parts[0] == "Enum" && parts.Length == 3)
            {
                switch (parts[1])
                {
                    case nameof(AutoControlMode):
                        return Enum.TryParse(parts[2], out AutoControlMode mode) ? setting.GetEnumValueLocaleID(mode) : null;
                    case nameof(PlanStrategy):
                        return Enum.TryParse(parts[2], out PlanStrategy strategy) ? setting.GetEnumValueLocaleID(strategy) : null;
                    case nameof(AutoLayout):
                        return Enum.TryParse(parts[2], out AutoLayout layout) ? setting.GetEnumValueLocaleID(layout) : null;
                }
                return null;
            }
            return "TLL." + key;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return m_Entries;
        }

        public void Unload()
        {
        }
    }
}
