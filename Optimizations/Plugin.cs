using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Runtime switches for other plugins (the profiler's A/B experiments flip these via reflection).
    /// Setting a value overrides the config until the config entry changes again.
    /// </summary>
    public static class Api
    {
        internal static Plugin Instance;

        public static bool ShadowProxyCulling
        {
            get => Instance != null && Instance.Culling.Enabled;
            set { if (Instance != null) Instance.Culling.Enabled = value; }
        }

        public static bool GcPolicy
        {
            get => Instance != null && Instance.Gc.Enabled;
            set
            {
                if (Instance == null) return;
                if (Instance.Gc.Enabled && !value) Instance.Gc.Release();
                Instance.Gc.Enabled = value;
            }
        }

        public static int ShadowProxiesDisabled => Instance?.Culling.DisabledCount ?? 0;

        public static bool BatchedOverlaps
        {
            get => Instance != null && Instance.Overlaps != null && Instance.Overlaps.Enabled;
            set { if (Instance?.Overlaps != null) Instance.Overlaps.Enabled = value; }
        }

        public static bool FadedLightCulling
        {
            get => Optimizations.FadedLightCulling.Enabled;
            set => Optimizations.FadedLightCulling.Enabled = value;
        }

        public static bool BotAnimLod
        {
            get => Optimizations.BotAnimLod.Enabled;
            set
            {
                if (Optimizations.BotAnimLod.Enabled && !value) Optimizations.BotAnimLod.ReleaseAll();
                Optimizations.BotAnimLod.Enabled = value;
            }
        }

        public static bool ObservedArmsLod
        {
            get => Optimizations.ObservedArmsLod.Enabled;
            set => Optimizations.ObservedArmsLod.Enabled = value;
        }

        public static bool RunawayMoveGuard
        {
            get => Optimizations.RunawayMoveGuard.Enabled;
            set => Optimizations.RunawayMoveGuard.Enabled = value;
        }

        public static bool DeclutterSmall
        {
            get => Optimizations.Declutter.Small;
            set => Optimizations.Declutter.Small = value;
        }

        public static bool DeclutterMedium
        {
            get => Optimizations.Declutter.Medium;
            set => Optimizations.Declutter.Medium = value;
        }

        public static int DeclutterHidden => Optimizations.Declutter.HiddenObjects;
    }

    [BepInPlugin("com.sptoptimizer.optimizations", "SPTOptimizer.Optimizations", "0.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal GcPolicy Gc;
        internal ShadowProxyCulling Culling;
        internal BatchedOverlaps Overlaps;

        private ConfigEntry<bool> _gcEnabled;
        private ConfigEntry<bool> _gcAnyMenu;
        private ConfigEntry<float> _gcHeadroom;
        private ConfigEntry<bool> _cullEnabled;
        private ConfigEntry<float> _cullDistance;
        private ConfigEntry<float> _cullShadowMult;
        private ConfigEntry<string> _cullLayers;
        private ConfigEntry<bool> _fastAnimator;
        private ConfigEntry<bool> _overlapsEnabled;
        private ConfigEntry<float> _overlapsMargin;
        private ConfigEntry<bool> _overlapsValidate;
        private ConfigEntry<bool> _guardEnabled;
        private ConfigEntry<float> _guardMaxSpeed;
        private ConfigEntry<bool> _fadedLights;
        private ConfigEntry<float> _lodBias;
        private ConfigEntry<bool> _animLod;
        private ConfigEntry<float> _animLodOffscreen;
        private ConfigEntry<bool> _animLodLocal;
        private ConfigEntry<bool> _armsLod;
        private ConfigEntry<bool> _armsLodHumans;
        private ConfigEntry<int> _armsLodInterval;
        private ConfigEntry<float> _armsLodNear;
        private ConfigEntry<float> _armsLodFar;
        private ConfigEntry<bool> _physicsDiag;
        private ConfigEntry<bool> _declutterSmall;
        private ConfigEntry<bool> _declutterMedium;
        private ConfigEntry<bool> _declutterReport;
        private float _raidSeenAt = -1f;
        private bool _raidStateLogged, _raidLightsSwept;

        private void Awake()
        {
            Gc = new GcPolicy(Logger);
            Culling = new ShadowProxyCulling(Logger);

            _gcEnabled = Config.Bind("GC policy", "Enabled", true,
                "In raid, keep the GC off while you're in the game view and on only while a menu is open (SPT turns it on at the first inventory open and never off again, causing 50-90 ms hitches).");
            _gcAnyMenu = Config.Bind("GC policy", "Collect in any menu", false,
                "Also let the GC run while other in-raid menus are open (not just the Inventory).");
            _gcHeadroom = Config.Bind("GC policy", "Heap headroom MB", 3072f,
                "Safety valve: if the managed heap grows this far past its raid-start size, the GC is turned on until it drops back to half of that.");
            _cullEnabled = Config.Bind("Shadow proxy culling", "Enabled", true,
                "Disable static ShadowsOnly renderers far beyond the shadow distance; they cost culling time but never cast visible shadows.");
            _cullDistance = Config.Bind("Shadow proxy culling", "Distance", 100f,
                "Proxies farther than this (m, from the nearest point of their bounds) are disabled.");
            _cullShadowMult = Config.Bind("Shadow proxy culling", "Minimum multiple of shadow distance", 2.5f,
                "The distance used is at least this multiple of the current shadow distance.");
            _cullLayers = Config.Bind("Shadow proxy culling", "Layers", "Default,CullingMask,HighPolyCollider",
                "Layers whose ShadowsOnly renderers are managed (applies to the next raid).");

            _fastAnimator = Config.Bind("Experimental", "BSG fast body animator", false,
                new ConfigDescription("EXPERIMENTAL, needs a game restart. Use BSG's own playable-based body animator (UseBodyFastAnimator, normally only for online observed players) for every player: root motion from a precomputed table and no body animation for off-screen players. May break animation, movement or footsteps.",
                    null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            if (_fastAnimator.Value) FastAnimator.Install(new Harmony("com.sptoptimizer.optimizations.fastanimator"), Logger);
            else Logger.LogInfo("Fast body animator: off (set [Experimental] BSG fast body animator = true and restart the game to try it)");
            _fastAnimator.SettingChanged += (_, __) =>
                Logger.LogWarning($"BSG fast body animator set to {_fastAnimator.Value}: this only takes effect after a full game restart (currently {(FastAnimator.Installed ? "on" : "off")})");

            _overlapsEnabled = Config.Bind("Batched overlaps", "Enabled", true,
                "Run all players' character-controller neighbour queries (OverlapSphere) as one multi-threaded batch instead of one by one on the main thread. Same queries; falls back to the original whenever the batch doesn't cover a query.");
            _overlapsMargin = Config.Bind("Batched overlaps", "Margin", 0.15f,
                "Extra radius (m) on each batched sphere so that sub-step queries and nearby movement during the frame stay inside it.");
            _guardEnabled = Config.Bind("Runaway move guard", "Enabled", true,
                "Skip character-controller moves at impossible speeds. Fixes the parked BTR gunner on Streets, whose endless off-map 'fall' makes EFT run hundreds of collision sub-steps per frame until the BTR arrives.");
            _guardMaxSpeed = Config.Bind("Runaway move guard", "Max speed m/s", 200f,
                "Moves faster than this are skipped (sprinting ~7 m/s, long falls ~30 m/s).");
            try
            {
                RunawayMoveGuard.Install(new Harmony("com.sptoptimizer.optimizations.runawaymove"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Runaway move guard unavailable: " + ex);
            }

            _fadedLights = Config.Bind("Faded light culling", "Enabled", true,
                "Switch off lights that EFT has already faded to intensity 0 by distance (Unity still processes every enabled light, ~4,600 on Streets). No visual change (verified by screenshot comparison); ~0.24 ms less camera culling.");
            _lodBias = Config.Bind("Trade-offs (visible)", "Object LOD bias override", 0f,
                new ConfigDescription("0 = use the game's Object LOD setting (minimum 2). 1 = lower-detail models switch in sooner: on Streets −0.11 ms main thread, −0.37 ms render thread, −390 draws, but mid-distance trees thin out and small debris at ~40–60 m disappears.",
                    new AcceptableValueRange<float>(0f, 4f)));
            _physicsDiag = Config.Bind("Diagnostics", "Physics and corpse stats", true,
                "Log what keeps EFT's physics simulation running (corpses, loot, BTR), its cost, and corpse settle times, in the 30 s 'Stats:' line. No behaviour change.");
            Declutter.Init(Logger);
            _declutterSmall = Config.Bind("Trade-offs (visible)", "Declutter small objects", false,
                "Stop drawing small decorative clutter that nothing in the game interacts with: paper, litter, cans, bottles, casings, debris bits, puddles, dirt and blood mesh decals. Anything with a collider, trigger, sound, light or game script is kept, so bullets, movement, AI, footstep sounds and loot are unchanged. Measured ~0 ms frame time on Streets (−2% draw calls): a cleaner look rather than a speed-up. Can be switched mid-raid.");
            _declutterMedium = Config.Bind("Trade-offs (visible)", "Declutter medium objects", false,
                "Also stop drawing clutter up to 2 m that has a collider: cardboard boxes, garbage bags, bricks, scrap. Only the visuals go: the objects stay SOLID, so they become invisible obstacles that still stop bullets, players and grenades (and still give everyone else in Fika cover). Measured −7% draw calls, −0.25 ms render thread, no measurable frame-time change on Streets.");
            _declutterReport = Config.Bind("Diagnostics", "Declutter report", false,
                "When declutter scans a map, write SPTOptimizer\\declutter_<map>.txt: what matched each keyword, why matches were rejected, and the most common small visual-only objects no keyword matched (for tuning). Makes the scan slower.");
            try
            {
                FadedLightCulling.Install(new Harmony("com.sptoptimizer.optimizations.fadedlights"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Faded light culling unavailable: " + ex);
            }
            // The bot IK distance option (EFTHardSettings.CULL_GROUNDER) was removed in 0.3.0: Fika 2.4.4+ owns that value
            // through its "IK Cull Distance" setting, and re-applying ours on every config change silently undid Fika's.
            _animLod = Config.Bind("Trade-offs (gameplay)", "Distant bot animation at half rate", false,
                "EXPERIMENTAL. Body animators of off-screen bots beyond the off-screen distance are skipped every other frame and catch up on the next (~0.7-1.1 ms on Streets self-hosted). Applies only to bots on a Fika client, whose position comes from the host, so they move normally. Never on the headless host. On-screen bots are never touched.");
            _animLodOffscreen = Config.Bind("Trade-offs (gameplay)", "Half rate: off-screen distance", 10f, "Off-screen bots farther than this (m) animate at half rate (EFT already stops writing their bones beyond 10 m).");
            _animLodLocal = Config.Bind("Trade-offs (gameplay)", "Half rate: local bots too", false,
                new ConfigDescription("NOT RECOMMENDED. Also apply half rate to bots simulated on this PC (no Fika, or you are the Fika host). Unity's animator delivers ~20% less root motion on the catch-up, so those bots move ~20-25% slower while off-screen.",
                    null, new ConfigurationManagerAttributes { IsAdvanced = true }));
            _armsLod = Config.Bind("Trade-offs (visible)", "Far players' arms at reduced rate", false,
                "Fika clients only. The arms animator of bots that are off-screen, or on screen but far away, is evaluated every Nth frame and catches up in one step. Their shots come from the network, so hits and damage are unchanged; their weapon handling looks less smooth and its sounds can start up to N-1 frames late. Never while you aim down sights at them, never within the near distance, never on the headless host.");
            _armsLodHumans = Config.Bind("Trade-offs (visible)", "Arms reduced rate: human players too", false,
                "Also apply it to other human players, not only bots.");
            _armsLodInterval = Config.Bind("Trade-offs (visible)", "Arms reduced rate: every N frames", 2,
                new ConfigDescription("Evaluate the arms animator of qualifying players once every N frames.", new AcceptableValueRange<int>(1, 4)));
            _armsLodNear = Config.Bind("Trade-offs (visible)", "Arms reduced rate: near distance", 25f,
                "Players closer than this (m) always update at full rate.");
            _armsLodFar = Config.Bind("Trade-offs (visible)", "Arms reduced rate: on-screen distance", 60f,
                "On-screen players farther than this (m) update at the reduced rate. Keep it at or above Fika's IK Cull Distance (40).");
            try
            {
                ObservedArmsLod.Install(new Harmony("com.sptoptimizer.optimizations.observedarmslod"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Observed arms reduced rate unavailable: " + ex);
            }
            try
            {
                BotAnimLod.Install(new Harmony("com.sptoptimizer.optimizations.botanimlod"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Distant bot animation half rate unavailable: " + ex);
            }
            try
            {
                LodBiasOverride.Install(new Harmony("com.sptoptimizer.optimizations.lodbias"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("LOD bias override unavailable: " + ex);
            }
            try
            {
                PhysicsDiag.Install(new Harmony("com.sptoptimizer.optimizations.physicsdiag"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Physics diagnostics unavailable: " + ex);
            }

            _overlapsValidate = Config.Bind("Batched overlaps", "Validate", false,
                "Testing only (costs time): also run the original query for every served one and log any collider the batch missed. Also logs the controller query rate while the prototype is off, for comparison.");
            Overlaps = new BatchedOverlaps(Logger);
            try
            {
                Overlaps.Install(new Harmony("com.sptoptimizer.optimizations.overlaps"));
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Batched overlaps unavailable: " + ex);
                Overlaps = null;
            }

            ApplyConfig();
            Config.SettingChanged += (_, __) => ApplyConfig();
            Api.Instance = this;
            Logger.LogInfo($"Loaded. GC policy {(Gc.Enabled ? "on" : "off")}, shadow proxy culling {(Culling.Enabled ? "on" : "off")} at {_cullDistance.Value} m");
        }

        private void ApplyConfig()
        {
            if (Gc.Enabled && !_gcEnabled.Value) Gc.Release();
            Gc.Enabled = _gcEnabled.Value;
            Gc.CollectInAnyMenu = _gcAnyMenu.Value;
            Gc.HeadroomMB = _gcHeadroom.Value;
            Culling.Enabled = _cullEnabled.Value;
            Culling.Distance = _cullDistance.Value;
            Culling.ShadowDistanceMultiplier = _cullShadowMult.Value;
            Culling.Layers = _cullLayers.Value.Split(',');
            RunawayMoveGuard.Enabled = _guardEnabled.Value;
            PhysicsDiag.Enabled = _physicsDiag.Value;
            LodBiasOverride.Value = _lodBias.Value;
            Api.BotAnimLod = _animLod.Value;
            BotAnimLod.OffscreenDistance = _animLodOffscreen.Value;
            BotAnimLod.LocalBots = _animLodLocal.Value;
            ObservedArmsLod.Enabled = _armsLod.Value;
            ObservedArmsLod.IncludeHumans = _armsLodHumans.Value;
            ObservedArmsLod.Interval = _armsLodInterval.Value;
            ObservedArmsLod.NearDistance = _armsLodNear.Value;
            ObservedArmsLod.FarDistance = _armsLodFar.Value;
            // Faded light culling sweeps the scene when switched on; in a raid only (the raid hook sweeps otherwise).
            if (!_fadedLights.Value || Raid.InRaid(out _)) FadedLightCulling.Enabled = _fadedLights.Value;
            RunawayMoveGuard.MaxSpeed = _guardMaxSpeed.Value;
            Declutter.WriteReport = _declutterReport.Value;
            Declutter.Small = _declutterSmall.Value;
            Declutter.Medium = _declutterMedium.Value;
            if (Overlaps != null)
            {
                Overlaps.Enabled = _overlapsEnabled.Value;
                Overlaps.Margin = _overlapsMargin.Value;
                Overlaps.Validate = _overlapsValidate.Value;
            }
        }

        private void Update()
        {
            Gc.Tick();
            Culling.Tick();
            Overlaps?.Tick();
            FadedLightCulling.Tick();
            BotAnimLod.Tick();
            ObservedArmsLod.Tick();
            Declutter.Tick();
            Stats.Tick(Logger);

            // Once per raid, report which body animator the players actually got.
            if (Raid.InRaid(out _))
            {
                if (_raidSeenAt < 0f) _raidSeenAt = Time.realtimeSinceStartup;
                PhysicsDiag.Tick();
                // Lamps settle their fade values within a few seconds of spawning; sweep after that.
                if (!_raidLightsSwept && Time.realtimeSinceStartup - _raidSeenAt > 5f)
                {
                    _raidLightsSwept = true;
                    FadedLightCulling.OnRaidStart();  // forget the last raid's lights first, then switch on (which sweeps)
                    FadedLightCulling.Enabled = _fadedLights.Value;
                }
                if (!_raidStateLogged && Time.realtimeSinceStartup - _raidSeenAt > 15f)
                {
                    _raidStateLogged = true;
                    FastAnimator.LogRaidState();
                }
            }
            else
            {
                if (_raidSeenAt >= 0f)
                {
                    // raid over: drop per-player state
                    BotAnimLod.ReleaseAll();
                    ObservedArmsLod.ReleaseAll();
                }
                _raidSeenAt = -1f;
                _raidStateLogged = _raidLightsSwept = false;
            }
        }
        private void OnDestroy()
        {
            Overlaps?.Dispose();
        }
    }
}
