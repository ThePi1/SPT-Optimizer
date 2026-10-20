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

    [BepInPlugin("com.sptoptimizer.optimizations", "SPTOptimizer.Optimizations", "0.1.0")]
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
        private ConfigEntry<float> _ikDistance;
        private ConfigEntry<bool> _animLod;
        private ConfigEntry<float> _animLodOffscreen;
        private float _savedCullGrounder = -1f;
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
            _ikDistance = Config.Bind("Trade-offs (visible)", "Bot IK distance", 0f,
                new ConfigDescription("0 = game default (EFTHardSettings.CULL_GROUNDER, 90 m). Visible bots farther than this skip foot IK and procedural weapon animation. Measured: 20 m saved ~0.3-0.7 ms with 2-4 bots on screen at 20-90 m.",
                    new AcceptableValueRange<float>(0f, 200f)));
            _animLod = Config.Bind("Trade-offs (gameplay)", "Distant bot animation at half rate", false,
                "EXPERIMENTAL, NOT RECOMMENDED. Body animators of off-screen bots beyond the off-screen distance are skipped every other frame and catch up on the next. Saves ~0.7-1.1 ms on Streets, BUT Unity's animator delivers ~20% less root motion on the catch-up, so affected (off-screen) bots move ~20-25% slower. On-screen bots are never touched.");
            _animLodOffscreen = Config.Bind("Trade-offs (gameplay)", "Half rate: off-screen distance", 10f, "Off-screen bots farther than this (m) animate at half rate (EFT already stops writing their bones beyond 10 m).");
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
            ApplyIkDistance();
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

        /// <summary>EFTHardSettings is an asset: remember the game's value once and restore it when the option is 0.</summary>
        private void ApplyIkDistance()
        {
            var hs = EFTHardSettings.Instance;
            if (hs == null) return;
            if (_savedCullGrounder < 0f) _savedCullGrounder = hs.CULL_GROUNDER;
            float want = _ikDistance.Value > 0f ? _ikDistance.Value : _savedCullGrounder;
            if (!Mathf.Approximately(hs.CULL_GROUNDER, want)) hs.CULL_GROUNDER = want;
        }

        private void Update()
        {
            Gc.Tick();
            Culling.Tick();
            Overlaps?.Tick();
            FadedLightCulling.Tick();
            BotAnimLod.Tick();
            Declutter.Tick();
            Stats.Tick(Logger);

            // Once per raid, report which body animator the players actually got.
            if (Raid.InRaid(out _))
            {
                if (_raidSeenAt < 0f) _raidSeenAt = Time.realtimeSinceStartup;
                PhysicsDiag.Tick();
                if (_ikDistance.Value > 0f && Time.frameCount % 120 == 0) ApplyIkDistance();
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
                if (_raidSeenAt >= 0f) BotAnimLod.ReleaseAll(); // raid over: drop per-bot state
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
