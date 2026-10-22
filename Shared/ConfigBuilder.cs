using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SPTOptimizer
{
    /// <summary>ConfigurationManager reads this by name via reflection (no reference to its assembly needed).</summary>
#pragma warning disable 0649
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
        public int? Order;
    }
#pragma warning restore 0649

    /// <summary>
    /// Binds config entries in the order they should appear in ConfigurationManager (F12 lists higher Order first),
    /// and carries values over from entries renamed in an earlier version. BepInEx keeps entries it doesn't know as
    /// "orphans" (ConfigFile.OrphanedEntries) until the file is saved; an orphan under an entry's old name becomes the
    /// new entry's value, unless the file already has a value under the new name. Shared by both plugins.
    /// </summary>
    internal sealed class ConfigBuilder
    {
        private readonly ConfigFile _config;
        private readonly ManualLogSource _log;
        private readonly Dictionary<ConfigDefinition, string> _orphans;
        private int _order = 10000;
        private int _carried, _dropped;

        public ConfigBuilder(ConfigFile config, ManualLogSource log)
        {
            _config = config;
            _log = log;
            _orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(config) as Dictionary<ConfigDefinition, string>;
        }

        /// <param name="advanced">Hidden in ConfigurationManager unless "Advanced settings" is ticked.</param>
        /// <param name="was">The entry's old "section/key", if it was renamed.</param>
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description,
            bool advanced = false, AcceptableValueBase range = null, string was = null)
        {
            bool inFile = _orphans != null && _orphans.ContainsKey(new ConfigDefinition(section, key));
            var entry = _config.Bind(section, key, defaultValue, new ConfigDescription(description, range,
                new ConfigurationManagerAttributes { Order = _order--, IsAdvanced = advanced ? true : (bool?)null }));
            if (was != null && _orphans != null)
            {
                int slash = was.IndexOf('/');
                var old = new ConfigDefinition(was.Substring(0, slash), was.Substring(slash + 1));
                if (_orphans.TryGetValue(old, out string value))
                {
                    if (!inFile) entry.SetSerializedValue(value);
                    _orphans.Remove(old);
                    _carried++;
                }
            }
            return entry;
        }

        /// <summary>Forget an entry that no longer exists, so it disappears from the file.</summary>
        public void Drop(string sectionSlashKey)
        {
            int slash = sectionSlashKey.IndexOf('/');
            if (_orphans != null && _orphans.Remove(new ConfigDefinition(sectionSlashKey.Substring(0, slash), sectionSlashKey.Substring(slash + 1))))
                _dropped++;
        }

        /// <summary>Write the file once if anything was carried over or dropped.</summary>
        public void Finish()
        {
            if (_carried + _dropped == 0) return;
            _config.Save();
            _log.LogInfo($"Config: carried {_carried} settings over from their old names, removed {_dropped} obsolete ones");
        }
    }
}
