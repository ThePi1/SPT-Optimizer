using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace SPTOptimizer.Profiler
{
    [BepInPlugin("com.sptoptimizer.profiler", "SPTOptimizer.Profiler", "0.3.1")]
    [BepInDependency("com.sptoptimizer.optimizations", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<KeyboardShortcut> _captureKey;
        private ConfigEntry<KeyboardShortcut> _instrumentKey;
        private ConfigEntry<bool> _perSubsystem;
        private ConfigEntry<bool> _autoCaptureRaids;
        private ConfigEntry<bool> _instrumentOnCapture;
        private ConfigEntry<string> _targetsFile;
        private ConfigEntry<string> _outputDir;
        private ConfigEntry<bool> _keepBinary;
        private ConfigEntry<KeyboardShortcut> _censusKey;
        private ConfigEntry<bool> _censusOnStop;
        private ConfigEntry<bool> _recordCounters;
        private ConfigEntry<KeyboardShortcut> _experimentKey;
        private ConfigEntry<KeyboardShortcut> _sequenceKey;
        private ConfigEntry<float> _holdSeconds;
        private ConfigEntry<float> _farShadowsOnly;
        private ConfigEntry<float> _lodBias;
        private ConfigEntry<float> _farLights;
        private ConfigEntry<float> _farAnim;
        private ConfigEntry<string> _sequenceNames;
        private ConfigEntry<int> _repeats;
        private ConfigEntry<bool> _roundRobin;
        private ConfigEntry<int> _baselineEvery;
        private ConfigEntry<bool> _screenshots;
        private ConfigEntry<float> _autoAbDelay;
        private ConfigEntry<string> _autoAbMaps;
        private bool _sequenceStartedCapture;
        private float _raidEnteredAt = -1f;
        private bool _autoAbDone, _autoAbWarned;

        private Overlay _overlay;
        private bool _autoStarted;
        private bool _wasInRaid;
        private string _captureLabel;
        private string _raidLocation;
        private string _recorderStatus;
        private DateTime _captureStartedAt;
        private FullScreenMode _screenModeAtStart;
        private string _captureDir;

        private void Awake()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string gameDir = BepInEx.Paths.GameRootPath;

            // The F12 menu, in display order; fine-tuning values are advanced. Entries renamed in 0.3.x carry their old
            // values over (see ConfigBuilder).
            var c = new ConfigBuilder(Config, Logger);
            const string Capture = "1. Capture";
            const string Ab = "2. A/B testing";
            const string Methods = "3. Method timing";
            const string Params = "4. Experiment parameters";

            _captureKey = c.Bind(Capture, "Toggle capture", new KeyboardShortcut(KeyCode.F9),
                "Start or stop recording frame data to a capture folder.", was: "Hotkeys/Toggle capture");
            _autoCaptureRaids = c.Bind(Capture, "Auto capture raids", false,
                "Start a capture when a raid starts and stop it when the raid ends.", was: "Capture/Auto capture raids");
            _censusKey = c.Bind(Capture, "Scene census", new KeyboardShortcut(KeyCode.F7),
                "Write a snapshot of cameras, lights, renderers, animators and players. Causes a short hitch.", advanced: true, was: "Hotkeys/Scene census");
            _censusOnStop = c.Bind(Capture, "Scene census at capture stop", true,
                "Write census.txt into the capture folder when a capture stops.", advanced: true, was: "Capture/Scene census at capture stop");
            _perSubsystem = c.Bind(Capture, "Per-subsystem PlayerLoop timing", true,
                "Time every PlayerLoop subsystem (about 100 markers) instead of just the top-level phases.", advanced: true, was: "Capture/Per-subsystem PlayerLoop timing");
            _recordCounters = c.Bind(Capture, "Record Unity counters and markers", true,
                "Record every Unity profiler counter and marker that works in a release player (draw calls, culling and shadow timings, ...).", advanced: true, was: "Capture/Record Unity counters and markers");
            _outputDir = c.Bind(Capture, "Output folder", Path.Combine(gameDir, "SPTOptimizer", "captures"),
                "Where captures are written.", advanced: true, was: "Capture/Output folder");
            _keepBinary = c.Bind(Capture, "Keep frames.bin", false,
                "Keep the raw binary next to the CSV.", advanced: true, was: "Capture/Keep frames.bin");

            _sequenceKey = c.Bind(Ab, "Run A/B sequence", new KeyboardShortcut(KeyCode.F5),
                "Start or stop the A/B sequence: Baseline and the listed experiments alternate, each held for the step length. Starts a capture if none is running. Stand still.", was: "Experiments/Run A/B sequence");
            _sequenceNames = c.Bind(Ab, "Sequence experiments", "BatchedOverlaps",
                "Comma-separated experiments the sequence runs. Optimization switches: AllOptimizationsOff, ShadowProxyCullingOff, FadedLightCullingOff, BatchedOverlapsOff, RunawayGuardOff, ObservedArmsReducedRate, ScopeHalfRate (scoped in), BotAnimHalfRate, Declutter, DeclutterSolid. Animation: IkDistance20, IkDistance40, IkOffBots. Graphics: GfxShadowsLow, GfxVisibility400, GfxRender50, GfxPostFxOff, ... (full list in Profiler/README.md). Empty = all.", was: "Experiments/Sequence experiments");
            _repeats = c.Bind(Ab, "Sequence repeats", 1,
                "How many times the sequence cycles through its experiments. 4 gives a usable spread.", was: "Experiments/Sequence repeats");
            _holdSeconds = c.Bind(Ab, "Step length (s)", 15f,
                "How long each step of the sequence lasts. The report skips the first 3 s of each step.", was: "Experiments/Hold seconds");
            _screenshots = c.Bind(Ab, "Screenshot every step", true,
                "Take screenshots in the first seconds of every step (the part the report skips) and compare experiment and Baseline shots at the end (shots/diff.txt).", was: "Batch testing/Screenshot every step");
            _autoAbDelay = c.Bind(Ab, "Auto-start after raid start (s)", 0f,
                "If > 0, start the sequence (with a capture) automatically this many seconds after entering a raid, once per raid. A 10 s warning shows first: be standing still by then.", was: "Batch testing/Auto-start A/B after raid start (s)");
            _autoAbMaps = c.Bind(Ab, "Auto-start maps", "TarkovStreets",
                "Comma-separated location ids where the auto-start applies (empty = all maps).", was: "Batch testing/Auto-start maps");
            _roundRobin = c.Bind(Ab, "Round-robin layout", true,
                "Run several experiments between Baseline steps (order rotated every repeat) instead of a Baseline before every experiment. Each experiment is compared with the nearest Baseline on either side.", advanced: true, was: "Batch testing/Round-robin layout");
            _baselineEvery = c.Bind(Ab, "Experiments per Baseline", 2,
                "Round-robin: how many experiment steps run between Baseline steps.", advanced: true, was: "Batch testing/Experiments per Baseline");
            _experimentKey = c.Bind(Ab, "Cycle experiment", new KeyboardShortcut(KeyCode.F6),
                "Manually switch to the next experiment in the full list (wraps to Baseline). Changes visuals or behaviour until switched back.", advanced: true, was: "Experiments/Cycle experiment");

            _instrumentKey = c.Bind(Methods, "Toggle method instrumentation", new KeyboardShortcut(KeyCode.F8),
                "Patch or unpatch the methods listed in the targets file, to time them per frame. Patching freezes the game for a few seconds.", was: "Hotkeys/Toggle method instrumentation");
            _instrumentOnCapture = c.Bind(Methods, "Collect method timings during capture", true,
                "When methods are instrumented, record their timings during captures (methods.csv).", advanced: true, was: "Capture/Collect method timings during capture");
            _targetsFile = c.Bind(Methods, "Targets file", Path.Combine(BepInEx.Paths.ConfigPath, "SPTOptimizer.Profiler.targets.txt"),
                "Which methods are instrumented. Created from default-targets.txt if missing.", advanced: true, was: "Instrumentation/Targets file");

            _farShadowsOnly = c.Bind(Params, "FarShadowsOnlyOff distance", 100f,
                "Shadow-only renderers farther than this (m) from the camera are disabled.", advanced: true, was: "Experiments/FarShadowsOnlyOff distance");
            _lodBias = c.Bind(Params, "LodBiasLow value", 1f,
                "QualitySettings.lodBias used by LodBiasLow (game default here: 2).", advanced: true, was: "Experiments/LodBiasLow value");
            _farLights = c.Bind(Params, "FarVertexLightsOff distance", 50f,
                "Non-shadow vertex lights farther than this (m) are disabled.", advanced: true, was: "Experiments/FarVertexLightsOff distance");
            _farAnim = c.Bind(Params, "AnimCullCompletelyFar distance", 60f,
                "Bots farther than this (m) get fully culled animators in AnimCullCompletelyFar.", advanced: true, was: "Experiments/AnimCullCompletelyFar distance");
            c.Finish();
            Experiments.Init(Logger);
            Screenshots.Init(this, Logger);
            Experiments.SequenceFinished += () =>
            {
                if (_sequenceStartedCapture && FrameRecorder.Recording) StopCapture();
                _sequenceStartedCapture = false;
            };

            if (!File.Exists(_targetsFile.Value))
            {
                string def = Path.Combine(pluginDir, "default-targets.txt");
                if (File.Exists(def)) File.Copy(def, _targetsFile.Value);
            }

            var harmony = new Harmony("com.sptoptimizer.profiler");
            harmony.PatchAll(typeof(SetPlayerLoopPatch));
            try { AreaLightDispatcher.PatchLifecycle(harmony); } catch (Exception ex) { Logger.LogWarning("AreaLight hooks unavailable: " + ex.Message); }
            SlowWork.Install(harmony, Logger);
            MethodTimer.Init();
            CameraTimer.Hook();
            CaptureWriter.Start(Logger);
            _overlay = gameObject.AddComponent<Overlay>();
            _overlay.enabled = false;

            Logger.LogInfo($"Loaded. {_captureKey.Value} = capture, {_instrumentKey.Value} = instrument methods, {_censusKey.Value} = scene census. Output: {_outputDir.Value}");
        }

        private void Update()
        {
            if (_captureKey.Value.IsDown())
            {
                if (FrameRecorder.Recording) StopCapture();
                else StartCapture(false);
            }

            if (_instrumentKey.Value.IsDown())
            {
                if (FrameRecorder.Recording)
                {
                    Show("Stop the capture before changing instrumentation");
                }
                else if (MethodTimer.Patched)
                {
                    MethodTimer.Unpatch(Logger);
                    Show("Method instrumentation OFF");
                }
                else
                {
                    try
                    {
                        MethodTimer.Patch(_targetsFile.Value, Logger);
                        Show($"Method instrumentation ON ({MethodTimer.MethodCount} methods)");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("Instrumentation failed: " + ex);
                        Show("Instrumentation failed, see LogOutput.log");
                    }
                }
            }

            if (_censusKey.Value.IsDown())
            {
                string dir = FrameRecorder.Recording ? _captureDir : _outputDir.Value;
                WriteCensus(Path.Combine(dir, $"census_{DateTime.Now:yyyyMMdd-HHmmss}_{Sanitize(Location())}.txt"));
            }

            if (_experimentKey.Value.IsDown())
            {
                ApplyExperimentSettings();
                Show("Experiment: " + Experiments.Cycle());
            }

            if (_sequenceKey.Value.IsDown())
            {
                if (Experiments.SequenceRunning)
                {
                    Experiments.StopSequence();
                    Show("A/B sequence stopped");
                }
                else
                {
                    ApplyExperimentSettings();
                    if (!FrameRecorder.Recording)
                    {
                        StartCapture(false);
                        _sequenceStartedCapture = true;
                    }
                    Experiments.StartSequence();
                    Show($"A/B sequence started: {Experiments.SequenceLength} steps x {Experiments.HoldSeconds:0} s. Stand still.");
                }
            }

            string switched = Experiments.Tick();
            // Short, so the message is gone before the step's screenshots (0.8 s in).
            if (switched != null) Show(switched, Screenshots.Enabled ? 0.6f : 2f);

            bool inRaid = InRaid();
            if (!inRaid && _wasInRaid) Experiments.Reset();
            if (inRaid && !_wasInRaid)
            {
                _raidEnteredAt = Time.realtimeSinceStartup;
                _autoAbDone = _autoAbWarned = false;
            }
            TickAutoAb(inRaid);
            if (inRaid && FrameRecorder.Recording && _raidLocation == null) _raidLocation = Location();
            if (_autoCaptureRaids.Value)
            {
                if (inRaid && !_wasInRaid && !FrameRecorder.Recording)
                {
                    StartCapture(true);
                }
                else if (!inRaid && _wasInRaid && FrameRecorder.Recording && _autoStarted)
                {
                    StopCapture();
                }
            }
            _wasInRaid = inRaid;
        }

        private void TickAutoAb(bool inRaid)
        {
            float delay = _autoAbDelay.Value;
            if (delay <= 0f || !inRaid || _autoAbDone || _raidEnteredAt < 0f) return;
            string maps = _autoAbMaps.Value.Trim();
            if (maps.Length > 0 && !maps.Split(',').Any(m => string.Equals(m.Trim(), Location(), StringComparison.OrdinalIgnoreCase)))
            {
                _autoAbDone = true;
                return;
            }
            float t = Time.realtimeSinceStartup - _raidEnteredAt;
            if (!_autoAbWarned && t >= delay - 10f)
            {
                _autoAbWarned = true;
                Show("Auto A/B starts in 10 s: stand still somewhere busy and don't move the camera", 8f);
            }
            if (t < delay) return;
            _autoAbDone = true;
            if (Experiments.SequenceRunning) return;
            ApplyExperimentSettings();
            if (!FrameRecorder.Recording)
            {
                StartCapture(false);
                _sequenceStartedCapture = true;
            }
            Experiments.StartSequence();
            Show($"Auto A/B started: {Experiments.SequenceLength} steps x {Experiments.HoldSeconds:0} s (~{Experiments.SequenceLength * Experiments.HoldSeconds / 60f:0.0} min). Stand still.", 5f);
        }

        private static bool InRaid()
        {
            if (!Singleton<GameWorld>.Instantiated) return false;
            var gw = Singleton<GameWorld>.Instance;
            return gw != null && !(gw is HideoutGameWorld) && gw.MainPlayer != null;
        }

        private static string Location()
        {
            if (!Singleton<GameWorld>.Instantiated) return "menu";
            var gw = Singleton<GameWorld>.Instance;
            if (gw is HideoutGameWorld) return "hideout";
            try
            {
                return string.IsNullOrEmpty(gw.LocationId) ? "unknown" : gw.LocationId;
            }
            catch
            {
                return "unknown";
            }
        }

        private void StartCapture(bool auto)
        {
            _autoStarted = auto;
            _captureStartedAt = DateTime.Now;
            _screenModeAtStart = Screen.fullScreenMode;
            _captureLabel = Location();
            string dir = Path.Combine(_outputDir.Value, _captureStartedAt.ToString("yyyyMMdd-HHmmss") + "_" + Sanitize(_captureLabel));
            _captureDir = dir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "playerloop.txt"), LoopInjector.Describe());

            CaptureWriter.Open(dir);
            LoopInjector.PerSubsystem = _perSubsystem.Value;
            LoopInjector.Apply(true);
            MethodTimer.ResetTotals();
            MethodTimer.Collecting = MethodTimer.Patched && _instrumentOnCapture.Value;
            if (_recordCounters.Value) Counters.Begin(dir);
            SlowWork.Begin();
            Screenshots.Enabled = _screenshots.Value;
            Screenshots.Begin(dir);
            FrameRecorder.BeginCapture();

            _recorderStatus = FrameRecorder.RecorderStatus() + " unity=" + Counters.Status();
            _raidLocation = null;
            Logger.LogInfo($"Capture started: {dir}  recorders: {_recorderStatus}");
            Show("Capture STARTED" + (MethodTimer.Collecting ? $" (+{MethodTimer.MethodCount} methods)" : ""));
        }

        private void StopCapture()
        {
            if (Experiments.SequenceRunning) Experiments.StopSequence();
            FrameRecorder.EndCapture();
            MethodTimer.Collecting = false;
            LoopInjector.Apply(false);

            int slots = FrameRecorder.SlotCount;
            var slotNames = new string[slots];
            Array.Copy(FrameRecorder.SlotNames, slotNames, slots);
            bool methods = MethodTimer.Patched && _instrumentOnCapture.Value;
            var counterNames = (string[])Counters.Names.Clone();
            Counters.End();

            var info = new CaptureInfo
            {
                Directory = _captureDir,
                Header = BuildHeader(),
                SlotNames = slotNames,
                CounterNames = counterNames,
                ExperimentNames = (string[])Experiments.Names.Clone(),
                SlowItems = SlowWork.Items.ToArray(),
                MethodNames = methods ? (string[])MethodTimer.Names.Clone() : new string[0],
                MethodTotSelf = (long[])MethodTimer.TotSelf.Clone(),
                MethodTotIncl = (long[])MethodTimer.TotIncl.Clone(),
                MethodTotCalls = (long[])MethodTimer.TotCalls.Clone(),
                MethodMaxSelf = (long[])MethodTimer.MaxSelf.Clone(),
                MethodMaxIncl = (long[])MethodTimer.MaxIncl.Clone(),
                MethodFramesSeen = (int[])MethodTimer.FramesSeen.Clone(),
                FramesDropped = FrameRecorder.FramesDropped,
                KeepBinary = _keepBinary.Value,
            };
            CaptureWriter.Finish(info);
            Screenshots.Finish();
            if (_censusOnStop.Value) WriteCensus(Path.Combine(_captureDir, "census.txt"));

            Logger.LogInfo($"Capture stopped: {FrameRecorder.FramesRecorded} frames, {FrameRecorder.FramesDropped} dropped");
            Show($"Capture STOPPED ({FrameRecorder.FramesRecorded} frames), writing...");
        }

        private void ApplyExperimentSettings()
        {
            Experiments.HoldSeconds = Mathf.Max(5f, _holdSeconds.Value);
            Experiments.FarShadowsOnlyDistance = _farShadowsOnly.Value;
            Experiments.LodBias = _lodBias.Value;
            Experiments.FarLightDistance = _farLights.Value;
            Experiments.FarAnimDistance = _farAnim.Value;
            Experiments.Repeats = _repeats.Value;
            Experiments.SequenceNames = _sequenceNames.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            Experiments.RoundRobin = _roundRobin.Value;
            Experiments.BaselineEvery = _baselineEvery.Value;
        }

        private void WriteCensus(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Census.Take());
                Show("Scene census written");
                Logger.LogInfo("Census: " + path);
            }
            catch (Exception ex)
            {
                Logger.LogError("Census failed: " + ex);
            }
        }

        private string BuildHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SPTOptimizer.Profiler capture");
            sb.AppendLine($"Started: {_captureStartedAt:yyyy-MM-dd HH:mm:ss}   Location at start: {_captureLabel}   raid seen: {_raidLocation ?? "none"}   at stop: {Location()}");
            sb.AppendLine($"Auto capture: {_autoStarted}   Per-subsystem markers: {LoopInjector.PerSubsystem}   Methods instrumented: {(MethodTimer.Patched ? MethodTimer.MethodCount : 0)}");
            sb.AppendLine($"CPU: {SystemInfo.processorType} ({SystemInfo.processorCount} threads)   RAM: {SystemInfo.systemMemorySize} MB");
            sb.AppendLine($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsMemorySize} MB)");
            sb.AppendLine($"Screen: {Screen.width}x{Screen.height} {_screenModeAtStart} at start, {Screen.fullScreenMode} at stop @ {Screen.currentResolution.refreshRateRatio.value:0.#} Hz");
            sb.AppendLine($"Quality level: {QualitySettings.names[QualitySettings.GetQualityLevel()]}   vSyncCount: {QualitySettings.vSyncCount}   targetFrameRate: {Application.targetFrameRate}");
            sb.AppendLine($"GC mode: {GarbageCollector.GCMode}   incremental: {GarbageCollector.isIncremental}   slice: {GarbageCollector.incrementalTimeSliceNanoseconds / 1e6:0.##} ms");
            sb.AppendLine($"Recorders: {_recorderStatus}");
            sb.AppendLine($"Experiments: {Experiments.Describe()}");
            sb.AppendLine($"Optimizations: {OptimizationsState()}");
            return sb.ToString();
        }

        /// <summary>The optimizations plugin's switches at capture stop (via its Api, by reflection).</summary>
        private static string OptimizationsState()
        {
            var api = AccessTools.TypeByName("SPTOptimizer.Optimizations.Api");
            if (api == null) return "plugin not loaded";
            return string.Join(", ", api.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(bool))
                .Select(p => { try { return $"{p.Name} {((bool)p.GetValue(null) ? "on" : "off")}"; } catch { return p.Name + " ?"; } }));
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private void Show(string message, float seconds = 3f)
        {
            Logger.LogInfo(message);
            _overlay.Show(message, seconds);
        }
    }

    /// <summary>Short on-screen status message. Disabled when idle so IMGUI costs nothing during captures.</summary>
    internal class Overlay : MonoBehaviour
    {
        private string _message;
        private float _until;
        private GUIStyle _style;

        public void Show(string message, float seconds = 3f)
        {
            _message = message;
            _until = Time.realtimeSinceStartup + seconds;
            enabled = true;
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup > _until) enabled = false;
        }

        private void OnGUI()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
            }
            GUI.Box(new Rect(20, 20, Mathf.Max(560, 10 * _message.Length + 160), 36), "[SPTOptimizer] " + _message, _style);
        }
    }
}
