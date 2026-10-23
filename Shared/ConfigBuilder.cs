using BepInEx.Configuration;

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
    /// Binds config entries in the order they should appear in ConfigurationManager (F12 lists higher Order first).
    /// Shared by both plugins.
    /// </summary>
    internal sealed class ConfigBuilder
    {
        private readonly ConfigFile _config;
        private int _order = 10000;

        public ConfigBuilder(ConfigFile config)
        {
            _config = config;
        }

        /// <param name="advanced">Hidden in ConfigurationManager unless "Advanced settings" is ticked.</param>
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description,
            bool advanced = false, AcceptableValueBase range = null)
        {
            return _config.Bind(section, key, defaultValue, new ConfigDescription(description, range,
                new ConfigurationManagerAttributes { Order = _order--, IsAdvanced = advanced ? true : (bool?)null }));
        }
    }
}
