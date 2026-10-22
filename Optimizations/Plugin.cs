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

        public static bool ScopeReducedRate
        {
            get => Optimizations.ScopeRate.Enabled;
            set
            {
                if (Optimizations.ScopeRate.Enabled && !value) Optimizations.ScopeRate.Release();
                Optimizations.ScopeRate.Enabled = value;
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

    [BepInPlugin("com.sptoptimizer.optimizations", "SPTOptimizer.Optimizations", "0.3.1")]
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
        private ConfigEntry<bool> _scopeRate;
        private ConfigEntry<int> _scopeRateInterval;
        private ConfigEntry<float> _scopeRateTurn;
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
            Declutter.Init(Logger);
            BindConfig();

            if (_fastAnimator.Value) FastAnimator.Install(new Harmony("com.sptoptimizer.optimizations.fastanimator"), Logger);
            else Logger.LogInfo("Fast body animator: off (set [3. Experimental] BSG fast body animator = true and restart the game to try it)");
            _fastAnimator.SettingChanged += (_, __) =>
                Logger.LogWarning($"BSG fast body animator set to {_fastAnimator.Value}: this only takes effect after a full game restart (currently {(FastAnimator.Installed ? "on" : "off")})");
            try
            {
                RunawayMoveGuard.Install(new Harmony("com.sptoptimizer.optimizations.runawaymove"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Runaway move guard unavailable: " + ex);
            }
            try
            {
                FadedLightCulling.Install(new Harmony("com.sptoptimizer.optimizations.fadedlights"), Logger);
            }
            catch (System.Exception ex)
            {
                Logger.LogError("Faded light culling unavailable: " + ex);
            }
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

        /// <summary>
        /// The F12 menu, in display order. Sections are numbered so ConfigurationManager lists them in this order;
        /// fine-tuning values are advanced (shown only with "Advanced settings" ticked). Entries renamed in 0.3.x carry
        /// their old values over (see ConfigBuilder).
        /// </summary>
        private void BindConfig()
        {
            var c = new ConfigBuilder(Config, Logger);
            const string Main = "1. Optimizations (no visible change)";
            const string Visual = "2. Visual trade-offs";
            const string Exp = "3. Experimental";
            const string Diag = "4. Diagnostics";

            _gcEnabled = c.Bind(Main, "GC policy", true,
                "In raid, keep the garbage collector off while you're in the game view and on only while the inventory is open. SPT turns it on at the first inventory open and never off again, which causes 50-90 ms hitches.", was: "GC policy/Enabled");
            _gcAnyMenu = c.Bind(Main, "GC policy: collect in any menu", false,
                "Also let the GC run while other in-raid menus are open (not just the inventory).", advanced: true, was: "GC policy/Collect in any menu");
            _gcHeadroom = c.Bind(Main, "GC policy: heap headroom MB", 3072f,
                "Safety valve: if the managed heap grows this far past its raid-start size, the GC is turned on until it drops back to half of that.", advanced: true, was: "GC policy/Heap headroom MB");
            _cullEnabled = c.Bind(Main, "Shadow proxy culling", true,
                "Disable static shadow-only renderers far beyond the shadow distance: they cost culling time but never cast a visible shadow (-0.5 to -1.0 ms on Streets).", was: "Shadow proxy culling/Enabled");
            _cullDistance = c.Bind(Main, "Shadow proxy culling: distance", 100f,
                "Proxies farther than this (m, from the nearest point of their bounds) are disabled.", advanced: true, was: "Shadow proxy culling/Distance");
            _cullShadowMult = c.Bind(Main, "Shadow proxy culling: minimum multiple of shadow distance", 2.5f,
                "The distance used is at least this multiple of the current shadow distance.", advanced: true, was: "Shadow proxy culling/Minimum multiple of shadow distance");
            _cullLayers = c.Bind(Main, "Shadow proxy culling: layers", "Default,CullingMask,HighPolyCollider",
                "Layers whose shadow-only renderers are managed (applies from the next raid).", advanced: true, was: "Shadow proxy culling/Layers");
            _fadedLights = c.Bind(Main, "Faded light culling", true,
                "Switch off lights EFT has already faded to zero by distance; Unity still processes every enabled light (~4,600 on Streets). No visual change; -0.24 ms.", was: "Faded light culling/Enabled");
            _overlapsEnabled = c.Bind(Main, "Batched overlaps", true,
                "Run all characters' collision neighbour queries as one multi-threaded batch instead of one by one on the main thread. Same results. Helps where characters are simulated: self-hosted, or on the Fika host.", was: "Batched overlaps/Enabled");
            _overlapsMargin = c.Bind(Main, "Batched overlaps: margin", 0.15f,
                "Extra radius (m) on each batched sphere, so sub-step queries and movement during the frame stay inside it.", advanced: true, was: "Batched overlaps/Margin");
            _guardEnabled = c.Bind(Main, "Runaway move guard", true,
                "Skip character moves at impossible speeds. Fixes the parked BTR gunner on Streets, whose endless off-map fall costs up to 6 ms per frame until the BTR arrives. Helps where bots are simulated: self-hosted, or on the Fika host.", was: "Runaway move guard/Enabled");
            _guardMaxSpeed = c.Bind(Main, "Runaway move guard: max speed m/s", 200f,
                "Moves faster than this are skipped (sprinting ~7 m/s, long falls ~30 m/s).", advanced: true, was: "Runaway move guard/Max speed m/s");

            _scopeRate = c.Bind(Visual, "Scope camera at reduced rate", false,
                "While you look through a magnified scope, its picture is rendered only every Nth frame; the lens shows the last image in between. EFT renders the whole scene a second time for the scope: -0.9 to -1.8 ms at every 2 frames. The main view, aim and hits are unchanged; the scope picture is less smooth. With PiP-Disabler it only affects the scopes PiP-Disabler leaves to the game (variable, thermal, night vision).", was: "Trade-offs (visible)/Scope camera at reduced rate");
            _scopeRateInterval = c.Bind(Visual, "Scope reduced rate: every N frames", 2,
                "Render the scope picture once every N frames.", range: new AcceptableValueRange<int>(2, 4), was: "Trade-offs (visible)/Scope reduced rate: every N frames");
            _scopeRateTurn = c.Bind(Visual, "Scope reduced rate: full rate above turn speed", 15f,
                "While the view turns faster than this (degrees per second), render the scope every frame, where the reduced rate would show as judder. 0 = never.", advanced: true,
                range: new AcceptableValueRange<float>(0f, 360f), was: "Trade-offs (visible)/Scope reduced rate: full rate above turn speed");
            _armsLod = c.Bind(Visual, "Far player arms at reduced rate", false,
                "Fika clients only. The arms and weapon animation of bots that are off screen, or on screen but far away, is updated every Nth frame (-0.31 ms with ~20 bots). Their shots come from the network, so hits and damage are unchanged; their weapon handling looks less smooth and its sounds can start up to N-1 frames late. Never while you aim at them, never up close, never on the headless host.", was: "Trade-offs (visible)/Far player arms at reduced rate");
            _armsLodInterval = c.Bind(Visual, "Arms reduced rate: every N frames", 2,
                "Update the arms animation of qualifying players once every N frames.", range: new AcceptableValueRange<int>(1, 4), was: "Trade-offs (visible)/Arms reduced rate: every N frames");
            _armsLodHumans = c.Bind(Visual, "Arms reduced rate: human players too", false,
                "Also apply it to other human players, not only bots.", advanced: true, was: "Trade-offs (visible)/Arms reduced rate: human players too");
            _armsLodNear = c.Bind(Visual, "Arms reduced rate: near distance", 25f,
                "Players closer than this (m) always update at full rate.", advanced: true, was: "Trade-offs (visible)/Arms reduced rate: near distance");
            _armsLodFar = c.Bind(Visual, "Arms reduced rate: on-screen distance", 60f,
                "On-screen players farther than this (m) update at the reduced rate. Keep it at or above Fika's IK Cull Distance (40).", advanced: true, was: "Trade-offs (visible)/Arms reduced rate: on-screen distance");
            _declutterSmall = c.Bind(Visual, "Declutter small objects", false,
                "Stop drawing small decorative clutter nothing interacts with: paper, litter, cans, bottles, casings, debris, puddles, dirt and blood decals. Anything with a collider, trigger, sound, light or game script is kept, so gameplay is unchanged. A cleaner look rather than a speed-up (~0 ms). Can be switched mid-raid.", was: "Trade-offs (visible)/Declutter small objects");
            _declutterMedium = c.Bind(Visual, "Declutter medium objects", false,
                "Also stop drawing clutter up to 2 m that has a collider: cardboard boxes, garbage bags, bricks, scrap. Only the visuals go: the objects stay SOLID, so they become invisible obstacles that still stop bullets and players (and still give everyone else in Fika cover). ~0 ms frame time.", was: "Trade-offs (visible)/Declutter medium objects");
            _lodBias = c.Bind(Visual, "Object LOD bias override", 0f,
                "0 = use the game's Object LOD setting (minimum 2). 1 = lower-detail models switch in sooner: -0.11 ms main thread and -0.37 ms render thread on Streets, but mid-distance trees thin out and small debris at ~40-60 m disappears.",
                range: new AcceptableValueRange<float>(0f, 4f), was: "Trade-offs (visible)/Object LOD bias override");

            _animLod = c.Bind(Exp, "Distant bot animation at half rate", false,
                "Body animation of off-screen bots on a Fika client runs at half rate. Measured about 0 ms on a Fika client: it saves 0.11 ms of animation and costs about as much itself. Self-hosted it saves 0.7-1.1 ms, but only with the advanced local-bots option, which makes those bots move slower. Not recommended.", was: "Trade-offs (gameplay)/Distant bot animation at half rate");
            _animLodOffscreen = c.Bind(Exp, "Half rate: off-screen distance", 10f,
                "Off-screen bots farther than this (m) animate at half rate.", advanced: true, was: "Trade-offs (gameplay)/Half rate: off-screen distance");
            _animLodLocal = c.Bind(Exp, "Half rate: local bots too", false,
                "NOT RECOMMENDED. Also apply half rate to bots simulated on this PC (no Fika, or you are the Fika host). Unity's animator delivers ~20% less root motion on the catch-up, so those bots move ~20-25% slower while off screen.", advanced: true, was: "Trade-offs (gameplay)/Half rate: local bots too");
            _fastAnimator = c.Bind(Exp, "BSG fast body animator", false,
                "Needs a game restart. Use BSG's own lightweight body animator (UseBodyFastAnimator, normally only for online observed players) for every player. May break animation, movement or footsteps.", advanced: true, was: "Experimental/BSG fast body animator");

            _physicsDiag = c.Bind(Diag, "Physics and corpse stats", true,
                "Log what keeps EFT's physics simulation running (corpses, loot, BTR), its cost, and corpse settle times, in the 30 s Stats line. No behaviour change.", advanced: true, was: "Diagnostics/Physics and corpse stats");
            _declutterReport = c.Bind(Diag, "Declutter report", false,
                "When declutter scans a map, write SPTOptimizer\\declutter_<map>.txt: what matched each keyword, why matches were rejected, and the most common small visual-only objects no keyword matched. Makes the scan slower.", advanced: true, was: "Diagnostics/Declutter report");
            _overlapsValidate = c.Bind(Diag, "Batched overlaps: validate", false,
                "Testing only (costs time): also run the original query for every served one and log any collider the batch missed.", advanced: true, was: "Batched overlaps/Validate");

            // Removed in 0.3.0: Fika 2.4.4+ owns this value through its own "IK Cull Distance" setting.
            c.Drop("Trade-offs (visible)/Bot IK distance");
            c.Finish();
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
            Api.ScopeReducedRate = _scopeRate.Value;
            ScopeRate.Interval = _scopeRateInterval.Value;
            ScopeRate.TurnSpeed = _scopeRateTurn.Value;
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
            ScopeRate.Tick();
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
                    ScopeRate.Release();
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
