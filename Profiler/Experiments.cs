using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Live A/B experiments. One experiment is active at a time; its id is written into every frame row
    /// ("experiment" column) so the report can compare segments. Every experiment fully reverts what it
    /// changed. They change visuals or bot animation and are for measurement only.
    /// </summary>
    internal static class Experiments
    {
        // Ids are indexes into Names; the CSV stores the name, so new ones are appended.
        private const int FarShadowsOnlyOff = 1, AllShadowsOnlyOff = 2, ShadowsOff = 3, LodBiasLow = 4, FarVertexLightsOff = 5,
            ShadowProxyCullingOff = 6, IkDistance20 = 7, IkOffBots = 8, AnimCullTransformsAll = 9, AnimCullCompletelyOffscreen = 10,
            AnimCullCompletelyFar = 11, GcOn = 12, AreaLightDispatch = 13, BatchedOverlaps = 14, FirstGfx = 15, RunawayGuardOff = 26, BatchedOverlapsOff = 27, AllOptimizationsOff = 28,
            GfxShadowsLow = 29, IkDistance40 = 30, FadedLightCulling = 31, FadedLightCullingOff = 32,
            BotAnimHalfRate = 33, GfxVisibility400 = 34, Declutter = 35, DeclutterSolid = 36;

        public static readonly string[] Names =
        {
            "Baseline",
            "FarShadowsOnlyOff",
            "AllShadowsOnlyOff",
            "ShadowsOff",
            "LodBiasLow",
            "FarVertexLightsOff",
            "ShadowProxyCullingOff",
            // Player animation / IK
            "IkDistance20",                // EFTHardSettings.CULL_GROUNDER (asset value 90 m) -> 20: IK only for visible bots within 20 m
            "IkOffBots",                   // CULL_GROUNDER -> 0: no IK/procedural pass for any bot (upper bound)
            "AnimCullTransformsAll",       // every bot animator CullUpdateTransforms (EFT: AlwaysAnimate within 10 m)
            "AnimCullCompletelyOffscreen", // every bot animator CullCompletely: off-screen bots stop animating AND moving (movement is root motion), footsteps stop. Measurement only.
            "AnimCullCompletelyFar",       // CullCompletely only for bots beyond FarAnimDistance (same caveat: freezes them)
            // No-impact candidates
            "GcOn",                        // Mono GC enabled during the step (GC policy paused): per-frame cost of an enabled incremental GC
            "AreaLightDispatch",           // one Camera.onPreCull dispatcher for all AreaLights instead of ~1,170 delegates; same output
            "BatchedOverlaps",             // optimizations plugin prototype ON: character-controller neighbour queries as one multi-threaded batch
            // EFT graphics settings, changed through the game's own settings objects (same path as the settings menu).
            // Each is relative to whatever the player has configured; see GfxChanges.
            "GfxShadowsHigh",
            "GfxShadowsUltra",
            "GfxVisibility1500",
            "GfxLodBias4",
            "GfxMipStreamingOn",
            "GfxTexturesLow",
            "GfxPostFxOff",
            "GfxRender50",
            "GfxRender200",
            "GfxReflexOff",
            "GfxReflexBoost",
            "RunawayGuardOff",             // optimizations plugin's runaway move guard OFF (it's on by default): cost of the parked BTR gunner's endless fall
            "BatchedOverlapsOff",          // batched overlaps OFF (it's on by default since 2026-10-19): its gain is minus this step's delta
            "AllOptimizationsOff",         // shadow proxy culling, batched overlaps and runaway move guard all OFF: the combined per-frame gain (GC policy left alone; it only affects hitches)
            // Batch 1 candidates (2026-10-19)
            "GfxShadowsLow",               // shadow quality -> Low (30 m shadow distance)
            "IkDistance40",                // CULL_GROUNDER 90 -> 40 m
            "FadedLightCulling",           // forces faded light culling ON (no-op since it became the default, 2026-10-19)
            "FadedLightCullingOff",        // faded light culling OFF: its gain is minus this step's delta
            // Batch 3: trade-offs (visible / gameplay)
            "BotAnimHalfRate",             // optimizations plugin trade-off ON: distant bots' body animators at half rate
            "GfxVisibility400",            // overall visibility -> 400 (minimum; game default here 1000)
            "Declutter",                   // optimizations plugin trade-off ON: "Declutter small objects" (visual-only clutter not rendered)
            "DeclutterSolid",              // optimizations plugin trade-offs ON: "Declutter small objects" + "Declutter medium objects" (collider-bearing clutter meshes hidden, colliders kept)
        };

        // Graphics experiments: (GraphicsSettingsClass field, value). Applied via IGameSetting.ObjectValue, which runs
        // EFT's bindings (GameGraphicsClass / CameraClass), and restored the same way.
        private static readonly Dictionary<string, (string Field, object Value)[]> GfxChanges = new Dictionary<string, (string, object)[]>
        {
            ["GfxShadowsHigh"] = new (string, object)[] { ("ShadowsQuality", 2) },
            ["GfxShadowsUltra"] = new (string, object)[] { ("ShadowsQuality", 3) },
            ["GfxVisibility1500"] = new (string, object)[] { ("OverallVisibility", 1500f) },
            ["GfxLodBias4"] = new (string, object)[] { ("LodBias", 4f) },
            ["GfxMipStreamingOn"] = new (string, object)[] { ("MipStreaming", true) },
            ["GfxTexturesLow"] = new (string, object)[] { ("TextureQuality", 0) },
            ["GfxPostFxOff"] = new (string, object)[]
            {
                ("Ssao", EFT.Settings.Graphics.ESSAOMode.Off), ("SSR", EFT.Settings.Graphics.ESSRMode.Off),
                ("AntiAliasing", EFT.Settings.Graphics.EAntialiasingMode.None), ("Sharpen", 0f),
            },
            ["GfxRender50"] = new (string, object)[] { ("SuperSampling", EFT.Settings.Graphics.ESamplingMode.DownX05) },
            ["GfxRender200"] = new (string, object)[] { ("SuperSampling", EFT.Settings.Graphics.ESamplingMode.SuperX2) },
            ["GfxReflexOff"] = new (string, object)[] { ("NVidiaReflex", EFT.Settings.Graphics.ENvidiaReflexMode.Off) },
            ["GfxReflexBoost"] = new (string, object)[] { ("NVidiaReflex", EFT.Settings.Graphics.ENvidiaReflexMode.OnAndBoost) },
            ["GfxShadowsLow"] = new (string, object)[] { ("ShadowsQuality", 0) },
            ["GfxVisibility400"] = new (string, object)[] { ("OverallVisibility", 400f) },
        };
        private static readonly List<(Bsg.GameSettings.IGameSetting Setting, object Saved)> GfxSaved = new List<(Bsg.GameSettings.IGameSetting, object)>();
        private static string _gfxDetail = "";

        // SPTOptimizer.Optimizations is a separate plugin; reach its switches by reflection so either can run alone.
        private static readonly System.Reflection.PropertyInfo CullingSwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("ShadowProxyCulling");
        private static bool _savedCulling;

        private static readonly System.Reflection.PropertyInfo GcPolicySwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("GcPolicy");
        private static bool _savedGcPolicy;
        private static UnityEngine.Scripting.GarbageCollector.Mode _savedGcMode;

        private static readonly System.Reflection.PropertyInfo OverlapsSwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("BatchedOverlaps");
        private static bool _savedOverlaps;

        private static readonly System.Reflection.PropertyInfo GuardSwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("RunawayMoveGuard");
        private static bool _savedGuard;

        private static readonly System.Reflection.PropertyInfo FadedLightsSwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("FadedLightCulling");
        private static bool _savedFadedLights;

        private static readonly System.Reflection.PropertyInfo AnimLodSwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("BotAnimLod");
        private static bool _savedAnimLod;

        private static readonly System.Reflection.PropertyInfo DeclutterSwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("DeclutterSmall");
        private static bool _savedDeclutter;

        private static readonly System.Reflection.PropertyInfo DeclutterMediumSwitch =
            HarmonyLib.AccessTools.TypeByName("SPTOptimizer.Optimizations.Api")?.GetProperty("DeclutterMedium");
        private static bool _savedDeclutterMedium;

        private static bool IsGfx(int id) => id >= FirstGfx && GfxChanges.ContainsKey(Names[id]);

        private static bool Available(int id) =>
            (id != ShadowProxyCullingOff || CullingSwitch != null) && ((id != BatchedOverlaps && id != BatchedOverlapsOff) || OverlapsSwitch != null) &&
            (id != RunawayGuardOff || GuardSwitch != null) && ((id != FadedLightCulling && id != FadedLightCullingOff) || FadedLightsSwitch != null) && (id != BotAnimHalfRate || AnimLodSwitch != null) && (id != Declutter || DeclutterSwitch != null) && (id != DeclutterSolid || (DeclutterSwitch != null && DeclutterMediumSwitch != null)) &&
            (id != AllOptimizationsOff || (CullingSwitch != null && OverlapsSwitch != null && GuardSwitch != null)) &&
            (!IsGfx(id) || Singleton<SharedGameSettingsClass>.Instantiated);

        public static int ActiveId { get; private set; }
        public static float FarShadowsOnlyDistance = 100f;
        public static float LodBias = 1f;
        public static float FarLightDistance = 50f;
        public static float FarAnimDistance = 60f;
        public static string[] SequenceNames = new string[0];
        public static int Repeats = 1;
        /// <summary>Round-robin: several experiments between Baselines (fewer Baseline steps, order rotated each repeat).
        /// The report compares each experiment with the nearest Baseline before and after it.</summary>
        public static bool RoundRobin;
        public static int BaselineEvery = 2;
        // Screenshot triple inside each step's settle window (the 3 s the report skips): A (experiment), then a brief
        // switch back to Baseline for B, then the experiment again for C. See Screenshots.
        private static float _stepStart;
        private static int _shotPhase;
        private static bool _flipping;
        private static int _flipId;
        /// <summary>The id written into frames.csv: during the screenshot switch-back it stays the step's experiment,
        /// so the brief Baseline flip (inside the skipped settle window) doesn't split the segment.</summary>
        public static int RecordedId => _flipping ? _flipId : ActiveId;

        private static ManualLogSource _log;
        private static Renderer[] _sceneRenderers;
        private static readonly List<Renderer> DisabledRenderers = new List<Renderer>();
        private static readonly List<Light> DisabledLights = new List<Light>();
        private static ShadowQuality _savedShadows;
        private static float _savedLodBias;
        private static float _savedCullGrounder;
        private static float _nextMaintain;

        // Auto sequence: Baseline is interleaved between experiments so drift (bots moving, time of day) averages out.
        private static readonly List<int> Sequence = new List<int>();
        private static int _seqIndex = -1;
        private static float _seqNextSwitch;
        public static float HoldSeconds = 15f;
        public static bool SequenceRunning => _seqIndex >= 0;
        public static int SequenceLength => Sequence.Count;
        public static event Action SequenceFinished;

        public static void Init(ManualLogSource log)
        {
            _log = log;
        }

        public static string Describe() =>
            "sequence: " + string.Join(", ", SequenceIds().Select(i => Names[i])) +
            (CullingSwitch == null ? "; ShadowProxyCullingOff unavailable (optimizations plugin not loaded)" : $"; shadow proxy culling currently {(bool)CullingSwitch.GetValue(null)}") +
            $"; far ShadowsOnly > {FarShadowsOnlyDistance} m, lodBias {LodBias}, far vertex lights > {FarLightDistance} m, far anim > {FarAnimDistance} m, hold {HoldSeconds} s";

        /// <summary>Switch to the next experiment (wrapping to Baseline).</summary>
        public static string Cycle()
        {
            int next = (ActiveId + 1) % Names.Length;
            while (!Available(next)) next = (next + 1) % Names.Length;
            Set(next);
            return Names[ActiveId];
        }

        private static List<int> SequenceIds()
        {
            var ids = new List<int>();
            foreach (var raw in SequenceNames)
            {
                int id = Array.FindIndex(Names, n => string.Equals(n, raw.Trim(), StringComparison.OrdinalIgnoreCase));
                if (id > 0 && Available(id) && !ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0)
                for (int i = 1; i < Names.Length; i++) if (Available(i)) ids.Add(i);
            return ids;
        }

        // Steps that force an optimization ON compare against Baseline, so Baseline must have it OFF. If the player's
        // config already has it on, it is held off for the whole sequence and restored afterwards.
        private static readonly List<(System.Reflection.PropertyInfo Switch, string Name)> HeldOff = new List<(System.Reflection.PropertyInfo, string)>();

        private static System.Reflection.PropertyInfo[] ForceOnSwitches(int id) =>
            id == BatchedOverlaps ? new[] { OverlapsSwitch } : id == FadedLightCulling ? new[] { FadedLightsSwitch } : id == BotAnimHalfRate ? new[] { AnimLodSwitch } :
            id == Declutter ? new[] { DeclutterSwitch } : id == DeclutterSolid ? new[] { DeclutterSwitch, DeclutterMediumSwitch } : new System.Reflection.PropertyInfo[0];

        private static void HoldOffForceOnSwitches(List<int> ids)
        {
            foreach (int id in ids)
            foreach (var sw in ForceOnSwitches(id))
            {
                if (sw == null || !(bool)sw.GetValue(null) || HeldOff.Any(h => h.Switch == sw)) continue;
                sw.SetValue(null, false);
                HeldOff.Add((sw, Names[id]));
                _log.LogWarning($"A/B: {sw.Name} is switched on in the config; held OFF for this sequence so Baseline measures it off (step {Names[id]} turns it on). Restored afterwards.");
            }
        }

        private static void ReleaseHeldOff()
        {
            foreach (var h in HeldOff) h.Switch.SetValue(null, true);
            if (HeldOff.Count > 0) _log.LogInfo("A/B: restored " + string.Join(", ", HeldOff.Select(h => h.Switch.Name)));
            HeldOff.Clear();
        }

        public static void StartSequence()
        {
            Sequence.Clear();
            var ids = SequenceIds();
            HoldOffForceOnSwitches(ids);
            for (int r = 0; r < Math.Max(1, Repeats); r++)
            {
                if (!RoundRobin || ids.Count == 1)
                {
                    foreach (int id in ids)
                    {
                        Sequence.Add(0);
                        Sequence.Add(id);
                    }
                    continue;
                }
                // Rotate the order each repeat so every experiment sits at a different position (next to a Baseline on one side, then the other).
                int every = Math.Max(1, BaselineEvery);
                for (int k = 0; k < ids.Count; k++)
                {
                    if (k % every == 0) Sequence.Add(0);
                    Sequence.Add(ids[(k + r) % ids.Count]);
                }
            }
            Sequence.Add(0);
            _seqIndex = 0;
            Set(Sequence[0]);
            _stepStart = Time.realtimeSinceStartup;
            _shotPhase = 0;
            _seqNextSwitch = Time.realtimeSinceStartup + HoldSeconds;
            _log.LogInfo($"A/B sequence started: {Sequence.Count} steps x {HoldSeconds} s: {string.Join(", ", Sequence.Select(i => Names[i]))}");
        }

        public static void StopSequence()
        {
            _flipping = false;
            _seqIndex = -1;
            Set(0);
            ReleaseHeldOff();
        }

        /// <summary>Called every frame from the plugin's Update (Update phase, i.e. before Unity's animation update).</summary>
        public static string Tick()
        {
            string switched = null;
            if (_seqIndex >= 0 && Time.realtimeSinceStartup >= _seqNextSwitch)
            {
                _seqIndex++;
                if (_seqIndex >= Sequence.Count)
                {
                    _seqIndex = -1;
                    Set(0);
                    ReleaseHeldOff();
                    _log.LogInfo("A/B sequence finished");
                    SequenceFinished?.Invoke();
                    return "A/B sequence finished";
                }
                _flipping = false;
                Set(Sequence[_seqIndex]);
                _stepStart = Time.realtimeSinceStartup;
                _shotPhase = 0;
                _seqNextSwitch = Time.realtimeSinceStartup + HoldSeconds;
                switched = $"A/B step {_seqIndex + 1}/{Sequence.Count}: {Names[ActiveId]}";
            }
            if (_seqIndex >= 0 && Screenshots.Enabled) TickShots();

            switch (ActiveId)
            {
                case FarVertexLightsOff:
                    // EFT's light culling (CullingLightObject) toggles lights itself; keep ours off.
                    if (Time.realtimeSinceStartup >= _nextMaintain)
                    {
                        _nextMaintain = Time.realtimeSinceStartup + 0.5f;
                        foreach (var l in DisabledLights)
                        {
                            if (l != null && l.enabled) l.enabled = false;
                        }
                    }
                    break;
                case AnimCullTransformsAll:
                case AnimCullCompletelyOffscreen:
                case AnimCullCompletelyFar:
                    // Player.VisualPass rewrites the culling mode in every LateUpdate (after this frame's animation
                    // update), so override it here, in Update, right before Unity's next animation update.
                    ForceBotAnimatorCulling();
                    break;
            }
            return switched;
        }

        private static void TickShots()
        {
            float t = Time.realtimeSinceStartup - _stepStart;
            int step = _seqIndex;
            int id = _flipping ? _flipId : ActiveId;
            string label = Names[id];
            bool baselineStep = id == 0;
            switch (_shotPhase)
            {
                case 0 when t >= 0.8f:
                    Screenshots.Request(step, 'a', label, baselineStep);
                    _shotPhase = 1;
                    break;
                case 1 when t >= 1.0f:
                    if (!baselineStep)
                    {
                        _flipId = ActiveId;
                        _flipping = true;
                        Set(0, quiet: true);
                    }
                    _shotPhase = 2;
                    break;
                case 2 when t >= 1.6f:
                    Screenshots.Request(step, 'b', label, baselineStep);
                    _shotPhase = 3;
                    break;
                case 3 when t >= 1.8f:
                    if (_flipping)
                    {
                        Set(_flipId, quiet: true);
                        _flipping = false;
                    }
                    _shotPhase = 4;
                    break;
                case 4 when t >= 2.6f:
                    Screenshots.Request(step, 'c', label, baselineStep);
                    _shotPhase = 5;
                    break;
            }
        }

        private static void ForceBotAnimatorCulling()
        {
            if (!Singleton<GameWorld>.Instantiated) return;
            var gw = Singleton<GameWorld>.Instance;
            var main = gw.MainPlayer;
            var players = gw.AllAlivePlayersList;
            float far = FarAnimDistance;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p == main) continue;
                AnimatorCullingMode mode;
                if (ActiveId == AnimCullTransformsAll) mode = AnimatorCullingMode.CullUpdateTransforms;
                else if (ActiveId == AnimCullCompletelyOffscreen) mode = AnimatorCullingMode.CullCompletely;
                else if (p.Distance > far) mode = AnimatorCullingMode.CullCompletely;
                else continue; // AnimCullCompletelyFar: leave nearer bots to EFT
                var body = p.BodyAnimatorCommon;
                if (body != null && body.cullingMode != mode) body.cullingMode = mode;
                var arms = p.ArmsAnimatorCommon;
                if (arms != null && arms.cullingMode != mode) arms.cullingMode = mode;
            }
        }

        public static void Set(int id, bool quiet = false)
        {
            if (id == ActiveId) return;
            var sw = Stopwatch.StartNew();
            Revert();
            ActiveId = id;
            try
            {
                switch (id)
                {
                    case FarShadowsOnlyOff: DisableShadowsOnly(FarShadowsOnlyDistance); break;
                    case AllShadowsOnlyOff: DisableShadowsOnly(0f); break;
                    case ShadowsOff:
                        _savedShadows = QualitySettings.shadows;
                        QualitySettings.shadows = ShadowQuality.Disable;
                        break;
                    case LodBiasLow:
                        _savedLodBias = QualitySettings.lodBias;
                        QualitySettings.lodBias = LodBias;
                        break;
                    case FarVertexLightsOff: DisableFarVertexLights(FarLightDistance); break;
                    case ShadowProxyCullingOff:
                        _savedCulling = (bool)CullingSwitch.GetValue(null);
                        CullingSwitch.SetValue(null, false);
                        break;
                    case IkDistance20:
                    case IkDistance40:
                    case IkOffBots:
                        _savedCullGrounder = EFTHardSettings.Instance.CULL_GROUNDER;
                        EFTHardSettings.Instance.CULL_GROUNDER = id == IkDistance20 ? 20f : id == IkDistance40 ? 40f : 0f;
                        break;
                    case BotAnimHalfRate:
                        _savedAnimLod = (bool)AnimLodSwitch.GetValue(null);
                        AnimLodSwitch.SetValue(null, true);
                        break;
                    case Declutter:
                    case DeclutterSolid:
                        _savedDeclutter = (bool)DeclutterSwitch.GetValue(null);
                        _savedDeclutterMedium = DeclutterMediumSwitch != null && (bool)DeclutterMediumSwitch.GetValue(null);
                        if (id == DeclutterSolid) DeclutterMediumSwitch.SetValue(null, true);
                        DeclutterSwitch.SetValue(null, true);
                        break;
                    case FadedLightCulling:
                    case FadedLightCullingOff:
                        _savedFadedLights = (bool)FadedLightsSwitch.GetValue(null);
                        FadedLightsSwitch.SetValue(null, id == FadedLightCulling);
                        break;
                    case AnimCullTransformsAll:
                    case AnimCullCompletelyOffscreen:
                    case AnimCullCompletelyFar:
                        ForceBotAnimatorCulling();
                        break;
                    case GcOn:
                        _savedGcPolicy = GcPolicySwitch != null && (bool)GcPolicySwitch.GetValue(null);
                        GcPolicySwitch?.SetValue(null, false);
                        _savedGcMode = UnityEngine.Scripting.GarbageCollector.GCMode;
                        UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
                        break;
                    case AreaLightDispatch:
                        AreaLightDispatcher.Install();
                        break;
                    case BatchedOverlaps:
                    case BatchedOverlapsOff:
                        _savedOverlaps = (bool)OverlapsSwitch.GetValue(null);
                        OverlapsSwitch.SetValue(null, id == BatchedOverlaps);
                        break;
                    case RunawayGuardOff:
                        _savedGuard = (bool)GuardSwitch.GetValue(null);
                        GuardSwitch.SetValue(null, false);
                        break;
                    case AllOptimizationsOff:
                        _savedCulling = (bool)CullingSwitch.GetValue(null);
                        _savedOverlaps = (bool)OverlapsSwitch.GetValue(null);
                        _savedGuard = (bool)GuardSwitch.GetValue(null);
                        CullingSwitch.SetValue(null, false);
                        OverlapsSwitch.SetValue(null, false);
                        GuardSwitch.SetValue(null, false);
                        if (FadedLightsSwitch != null)
                        {
                            _savedFadedLights = (bool)FadedLightsSwitch.GetValue(null);
                            FadedLightsSwitch.SetValue(null, false);
                        }
                        break;
                    default:
                        if (IsGfx(id)) ApplyGfx(Names[id]);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"Experiment {Names[id]} failed: {ex}");
            }
            if (!quiet || sw.ElapsedMilliseconds > 100)
                _log.LogInfo($"Experiment -> {Names[id]} ({sw.ElapsedMilliseconds} ms){Detail()}{(quiet ? " [screenshot switch-back]" : "")}");
        }

        private static string Detail()
        {
            switch (ActiveId)
            {
                case FarShadowsOnlyOff:
                case AllShadowsOnlyOff: return $": {DisabledRenderers.Count} renderers disabled";
                case FarVertexLightsOff: return $": {DisabledLights.Count} lights disabled";
                case AreaLightDispatch: return $": {AreaLightDispatcher.Count} AreaLight hooks replaced";
                case IkDistance20:
                case IkDistance40:
                case IkOffBots: return $": CULL_GROUNDER {_savedCullGrounder} -> {EFTHardSettings.Instance.CULL_GROUNDER}";
                default: return IsGfx(ActiveId) ? _gfxDetail : "";
            }
        }

        private static void Revert()
        {
            switch (ActiveId)
            {
                case FarShadowsOnlyOff:
                case AllShadowsOnlyOff:
                {
                    int reenabledByGame = 0;
                    foreach (var r in DisabledRenderers)
                    {
                        if (r == null) continue;
                        if (r.enabled) reenabledByGame++;
                        else r.enabled = true;
                    }
                    if (reenabledByGame > 0) _log.LogInfo($"{reenabledByGame} of {DisabledRenderers.Count} renderers had been re-enabled by the game during the experiment");
                    DisabledRenderers.Clear();
                    break;
                }
                case ShadowsOff:
                    QualitySettings.shadows = _savedShadows;
                    break;
                case LodBiasLow:
                    QualitySettings.lodBias = _savedLodBias;
                    break;
                case FarVertexLightsOff:
                    foreach (var l in DisabledLights)
                    {
                        if (l != null) l.enabled = true;
                    }
                    DisabledLights.Clear();
                    break;
                case ShadowProxyCullingOff:
                    CullingSwitch?.SetValue(null, _savedCulling);
                    break;
                case IkDistance20:
                case IkDistance40:
                case IkOffBots:
                    EFTHardSettings.Instance.CULL_GROUNDER = _savedCullGrounder;
                    break;
                case FadedLightCulling:
                case FadedLightCullingOff:
                    FadedLightsSwitch?.SetValue(null, _savedFadedLights);
                    break;
                case BotAnimHalfRate:
                    AnimLodSwitch?.SetValue(null, _savedAnimLod);
                    break;
                case Declutter:
                case DeclutterSolid:
                    DeclutterMediumSwitch?.SetValue(null, _savedDeclutterMedium);
                    DeclutterSwitch?.SetValue(null, _savedDeclutter);
                    break;
                case GcOn:
                    UnityEngine.Scripting.GarbageCollector.GCMode = _savedGcMode;
                    GcPolicySwitch?.SetValue(null, _savedGcPolicy);
                    break;
                case AreaLightDispatch:
                    AreaLightDispatcher.Uninstall();
                    break;
                case BatchedOverlaps:
                case BatchedOverlapsOff:
                    OverlapsSwitch?.SetValue(null, _savedOverlaps);
                    break;
                case RunawayGuardOff:
                    GuardSwitch?.SetValue(null, _savedGuard);
                    break;
                case AllOptimizationsOff:
                    CullingSwitch?.SetValue(null, _savedCulling);
                    OverlapsSwitch?.SetValue(null, _savedOverlaps);
                    GuardSwitch?.SetValue(null, _savedGuard);
                    if (ActiveId == AllOptimizationsOff) FadedLightsSwitch?.SetValue(null, _savedFadedLights);
                    break;
                default:
                    if (IsGfx(ActiveId)) RevertGfx();
                    break;
                // Animator culling experiments need no revert: Player.VisualPass sets the mode again in its next LateUpdate.
            }
            ActiveId = 0;
        }

        private static void ApplyGfx(string name)
        {
            var settings = Singleton<SharedGameSettingsClass>.Instance.Graphics.Settings;
            var parts = new List<string>();
            foreach (var (field, value) in GfxChanges[name])
            {
                var setting = (Bsg.GameSettings.IGameSetting)typeof(GraphicsSettingsClass).GetField(field).GetValue(settings);
                object saved = setting.ObjectValue;
                GfxSaved.Add((setting, saved));
                setting.ObjectValue = value;
                parts.Add($"{field} {saved} -> {setting.ObjectValue}");
            }
            _gfxDetail = ": " + string.Join(", ", parts);
        }

        private static void RevertGfx()
        {
            // Reverse order, so settings that depend on each other unwind cleanly.
            for (int i = GfxSaved.Count - 1; i >= 0; i--)
            {
                try { GfxSaved[i].Setting.ObjectValue = GfxSaved[i].Saved; }
                catch (Exception ex) { _log.LogError($"Restoring {GfxSaved[i].Setting.Key} failed: {ex.Message}"); }
            }
            GfxSaved.Clear();
            _gfxDetail = "";
        }

        private static void DisableShadowsOnly(float minDistance)
        {
            // The scene's renderer set is static once a raid is loaded; cache it (the first call costs a few hundred ms).
            if (_sceneRenderers == null || _sceneRenderers.Length == 0 || _sceneRenderers[0] == null)
                _sceneRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>();

            Vector3 cp = CameraPos();
            float min2 = minDistance * minDistance;
            foreach (var r in _sceneRenderers)
            {
                if (r == null || !r.enabled || r.shadowCastingMode != ShadowCastingMode.ShadowsOnly) continue;
                if (min2 > 0f && (r.bounds.center - cp).sqrMagnitude < min2) continue;
                r.enabled = false;
                DisabledRenderers.Add(r);
            }
        }

        private static void DisableFarVertexLights(float minDistance)
        {
            Vector3 cp = CameraPos();
            float min2 = minDistance * minDistance;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (!l.enabled || l.renderMode != LightRenderMode.ForceVertex || l.type == LightType.Directional) continue;
                if ((l.transform.position - cp).sqrMagnitude < min2) continue;
                l.enabled = false;
                DisabledLights.Add(l);
            }
        }

        private static Vector3 CameraPos()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        /// <summary>Drop cached scene objects (call when leaving a raid).</summary>
        public static void Reset()
        {
            _seqIndex = -1;
            Revert();
            ReleaseHeldOff();
            _sceneRenderers = null;
        }
    }
}
