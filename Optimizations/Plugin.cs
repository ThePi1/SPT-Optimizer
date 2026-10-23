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
            else Logger.LogInfo("Fast body animator: off (set [3. Experimental / Other] BSG fast body animator = true and restart the game to try it)");
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
        /// fine-tuning values are advanced (shown only with "Advanced settings" ticked).
        /// </summary>
        private void BindConfig()
        {
            var c = new ConfigBuilder(Config);
            const string Main = "1. Standard Optimizations";
            const string Visual = "2. Extra Optimizations (potential visible impact)";
            const string Exp = "3. Experimental / Other";
            const string Diag = "4. Diagnostics";

            _gcEnabled = c.Bind(Main, "Modify GC policy", true,
                "In raid, keep the garbage collector off while you're in the game view and on only while the inventory is open.");
            _gcAnyMenu = c.Bind(Main, "GC policy: collect in any menu", false,
                "Also let the GC run while other in-raid menus are open (not just the inventory).", advanced: true);
            _gcHeadroom = c.Bind(Main, "GC policy: heap headroom MB", 3072f,
                "Safety valve: if the managed heap grows this far past its raid-start size, the GC is turned on until it drops back to half of that.", advanced: true);
            _cullEnabled = c.Bind(Main, "Shadow proxy culling", true,
                "Disable static shadow-only renderers far beyond the shadow distance.");
            _cullDistance = c.Bind(Main, "Shadow proxy culling: distance", 100f,
                "Proxies farther than this (from the nearest point of their bounds) are disabled.", advanced: true);
            _cullShadowMult = c.Bind(Main, "Shadow proxy culling: minimum multiple of shadow distance", 2.5f,
                "The distance used is at least this multiple of the current shadow distance.", advanced: true);
            _cullLayers = c.Bind(Main, "Shadow proxy culling: layers", "Default,CullingMask,HighPolyCollider",
                "Layers whose shadow-only renderers are managed (applies on next raid).", advanced: true);
            _fadedLights = c.Bind(Main, "Faded light culling", true,
                "Switch off lights already faded to zero by distance.");
            _overlapsEnabled = c.Bind(Main, "Batched overlaps", true,
                "Batch characters' collision neighbour queries.");
            _overlapsMargin = c.Bind(Main, "Batched overlaps: margin", 0.15f,
                "Extra radius (m) on each batched sphere so that sub-step queries and movement during the frame stay inside it.", advanced: true);
            _guardEnabled = c.Bind(Main, "Runaway move guard", true,
                "Skip character moves at impossible speeds. Fixes an issue with the parked BTR gunner on Streets.");
            _guardMaxSpeed = c.Bind(Main, "Runaway move guard: max speed m/s", 200f,
                "Moves faster than this are skipped (sprinting is roughly 7 m/s).", advanced: true);

            _scopeRate = c.Bind(Visual, "Scope camera at reduced rate", false,
                "While you look through a magnified scope, its picture is rendered only every other frame.\nWhen used with the PiP-Disabler mod, it only affects the scopes PiP-Disabler leaves to the game (variable, thermal, night vision).");
            _scopeRateInterval = c.Bind(Visual, "Scope reduced rate: every N frames", 2,
                "Render the scope picture once every N frames.", range: new AcceptableValueRange<int>(2, 4));
            _scopeRateTurn = c.Bind(Visual, "Scope reduced rate: full rate speed threshold", 15f,
                "While the view turns faster than this (degrees per second), render the scope every frame, where the reduced rate would show as judder. 0 = never.", advanced: true,
                range: new AcceptableValueRange<float>(0f, 360f));
            _armsLod = c.Bind(Visual, "Far player arms at reduced rate", false,
                "Update the arms and weapon animation of bots every Nth frame (Fika clients only).");
            _armsLodInterval = c.Bind(Visual, "Arms reduced rate: every N frames", 2,
                "Update the arms animation of qualifying players once every N frames.", range: new AcceptableValueRange<int>(1, 4));
            _armsLodHumans = c.Bind(Visual, "Arms reduced rate: human players too", false,
                "Also apply this setting to other human players, not only bots.", advanced: true);
            _armsLodNear = c.Bind(Visual, "Arms reduced rate: near distance", 25f,
                "Players closer than this (m) always update at full rate.", advanced: true);
            _armsLodFar = c.Bind(Visual, "Arms reduced rate: on-screen distance", 60f,
                "On-screen players farther than this (m) update at the reduced rate. Keep it at or above Fika's IK Cull Distance (40 by default).", advanced: true);
            _declutterSmall = c.Bind(Visual, "Declutter small objects", false,
                "Stop drawing small decorative clutter (this skips anything with a collider).\nNot likely to improve performance.");
            _declutterMedium = c.Bind(Visual, "Declutter medium objects", false,
                "Stop drawing medium (<2m) objects. They still have collision.\nNot likely to improve performance.");
            _lodBias = c.Bind(Visual, "Object LOD bias override", 0f,
                "Set the LOD bias level manually, 0 = use the game's Object LOD setting.",
                range: new AcceptableValueRange<float>(0f, 4f));

            _animLod = c.Bind(Exp, "Distant bot animation at half rate", false,
                "Body animation of off-screen bots on a Fika client runs at half rate. Not recommended.");
            _animLodOffscreen = c.Bind(Exp, "Half rate: off-screen distance", 10f,
                "Off-screen bots farther than this (m) animate at half rate.", advanced: true);
            _animLodLocal = c.Bind(Exp, "Half rate: local bots too", false,
                "Also apply half rate to bots simulated on this PC (no Fika, or you are the Fika host). Causes bots to move slower off-screen, not recommended.", advanced: true);
            _fastAnimator = c.Bind(Exp, "BSG fast body animator", false,
                "Use BSG's own lightweight body animator for every player. May break animation, movement or footsteps. Needs a game restart. Not likely to improve performance.", advanced: true);

            _physicsDiag = c.Bind(Diag, "Physics and corpse stats", true,
                "Log physics simulation statistics (for debugging).", advanced: true);
            _declutterReport = c.Bind(Diag, "Declutter report", false,
                "Log a report about decluttered object selection when enabling (for debugging).", advanced: true);
            _overlapsValidate = c.Bind(Diag, "Batched overlaps: validate", false,
                "Testing only (costs time): also run the original query for every served one and log any collider the batch missed.", advanced: true);
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
