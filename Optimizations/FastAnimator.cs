using System;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// EXPERIMENTAL: turns on BSG's own "fast" body animator (ApplicationConfigClass.UseBodyFastAnimator) for all
    /// players, which EFT only uses for observed players in online games.
    ///
    /// With the standard animator every bot's 22-layer body Animator is evaluated every frame even off-screen,
    /// because movement is root motion (MovementState.ProcessAnimatorMovement reads Animator.deltaPosition) and
    /// footsteps come from the "FootStep" curve. Measured on Streets: ~65 µs per off-screen bot, ~1.3 ms for 19 bots.
    /// The fast animator runs the body state machine in managed code (FastAnimatorProcessorClass), takes root
    /// motion from a precomputed RootMotionBlendTable, and stops the visual PlayableGraph while the player is
    /// off-screen (the fast-animator culler culler).
    ///
    /// The flag is read when player pool objects and players are created, so it is set once, right after
    /// TarkovApplication.ConfigureApplication builds the config, and changing it needs a game restart.
    /// The root-motion tables are only preloaded for the spirit system, so they are added to EFT's raid/hideout
    /// preload list.
    /// </summary>
    internal static class FastAnimator
    {
        private static readonly string[] ExtraBundles =
        {
            "assets/content/characters/rootmotiontable/playerrootmotiontable.asset.bundle",
            "assets/content/characters/rootmotiontable/zombierootmotiontable.bundle",
        };

        private static ManualLogSource _log;
        public static bool Installed { get; private set; }
        public static string Status { get; private set; } = "off";

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            try
            {
                var field = AccessTools.Field(typeof(EFT.InGameBundles), "BUNDLES_TO_PRELOAD");
                var current = (string[])field.GetValue(null);
                var merged = current.Concat(ExtraBundles.Where(b => !current.Contains(b))).ToArray();
                field.SetValue(null, merged);
                if (!ReferenceEquals(field.GetValue(null), merged))
                {
                    Status = "failed: could not extend BUNDLES_TO_PRELOAD";
                    log.LogError("Fast body animator: " + Status + "; leaving it off");
                    return;
                }

                // TarkovApplication.ConfigureApplication calls the generic base first; patch the concrete override.
                harmony.Patch(AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.ConfigureApplication)),
                    postfix: new HarmonyMethod(typeof(FastAnimator), nameof(ConfigurePostfix)));
                Installed = true;
                Status = "waiting for application config";

                // If the application was configured before this plugin loaded, set the flag now (no players can exist yet).
                if (AppEnvironment.Config != null) Apply("plugin load");
                log.LogWarning($"Fast body animator (EXPERIMENTAL) installed: {ExtraBundles.Length} root-motion bundles added to preload ({merged.Length} total)");
            }
            catch (Exception ex)
            {
                Status = "failed: " + ex.Message;
                log.LogError("Fast body animator install failed: " + ex);
            }
        }

        private static void ConfigurePostfix()
        {
            Apply("ConfigureApplication");
        }

        private static void Apply(string when)
        {
            var config = AppEnvironment.Config;
            if (config == null) return;
            config.UseBodyFastAnimator = true;
            Status = "on";
            _log.LogWarning($"Fast body animator: UseBodyFastAnimator = true (set at {when})");
        }

        /// <summary>Log which body animator each player in the raid actually got.</summary>
        public static void LogRaidState()
        {
            if (!Singleton<GameWorld>.Instantiated) return;
            var players = Singleton<GameWorld>.Instance.AllAlivePlayersList;
            var groups = players.Where(p => p != null && p.BodyAnimatorCommon != null)
                .GroupBy(p => p.BodyAnimatorCommon.GetType().Name)
                .Select(g => $"{g.Key} x{g.Count()}");
            _log.LogInfo($"Fast body animator: flag {AppEnvironment.Config?.UseBodyFastAnimator}, body animators in raid: {string.Join(", ", groups)}");
        }
    }
}
