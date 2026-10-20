using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Harmony-based method timing. Wraps every target method with a prefix/postfix pair that records
    /// inclusive and self time (self = inclusive minus time in other instrumented methods it called).
    /// Only main-thread calls are timed. Patching is done on demand because it takes seconds for
    /// a thousand methods.
    /// </summary>
    internal static class MethodTimer
    {
        private const int MaxDepth = 512;

        public static bool Patched { get; private set; }
        public static bool Collecting;
        public static int MethodCount { get; private set; }
        public static string[] Names = new string[0];

        private static Harmony _harmony;
        private static int _mainThreadId;
        private static readonly Dictionary<IntPtr, int> Ids = new Dictionary<IntPtr, int>();

        // Call stack (main thread only)
        private static readonly long[] StartStack = new long[MaxDepth];
        private static readonly long[] ChildStack = new long[MaxDepth];
        private static int _depth;

        // Per-frame
        private static long[] _frameSelf = new long[0];
        private static long[] _frameIncl = new long[0];
        private static int[] _frameCalls = new int[0];
        private static int[] _touched = new int[0];
        private static bool[] _isTouched = new bool[0];
        private static int _touchedCount;
        private static int _frameCallTotal;

        // Capture totals
        public static long[] TotSelf = new long[0];
        public static long[] TotIncl = new long[0];
        public static long[] TotCalls = new long[0];
        public static long[] MaxSelf = new long[0];
        public static long[] MaxIncl = new long[0];
        public static int[] FramesSeen = new int[0];

        public static void Init()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _harmony = new Harmony("com.sptoptimizer.profiler.methods");
        }

        public static void Prefix(out int __state)
        {
            if (!Collecting || _depth >= MaxDepth || Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                __state = -1;
                return;
            }
            __state = _depth;
            ChildStack[_depth] = 0;
            StartStack[_depth] = Stopwatch.GetTimestamp();
            _depth++;
        }

        public static void Postfix(int __state, MethodBase __originalMethod)
        {
            if (__state < 0) return;
            long now = Stopwatch.GetTimestamp();
            long incl = now - StartStack[__state];
            long self = incl - ChildStack[__state];
            // Resetting depth from the saved state also heals the stack if an inner method threw.
            _depth = __state;
            if (__state > 0) ChildStack[__state - 1] += incl;

            if (!Ids.TryGetValue(__originalMethod.MethodHandle.Value, out int id)) return;
            if (!_isTouched[id])
            {
                _isTouched[id] = true;
                _touched[_touchedCount++] = id;
            }
            _frameSelf[id] += self;
            _frameIncl[id] += incl;
            _frameCalls[id]++;
            _frameCallTotal++;
        }

        /// <summary>Folds this frame into the capture totals and writes the top-3 self-time methods into the row.</summary>
        public static void EndFrame(float[] row, int o)
        {
            int t1 = -1, t2 = -1, t3 = -1;
            long s1 = 0, s2 = 0, s3 = 0;
            for (int i = 0; i < _touchedCount; i++)
            {
                int id = _touched[i];
                long self = _frameSelf[id];
                long incl = _frameIncl[id];
                TotSelf[id] += self;
                TotIncl[id] += incl;
                TotCalls[id] += _frameCalls[id];
                if (self > MaxSelf[id]) MaxSelf[id] = self;
                if (incl > MaxIncl[id]) MaxIncl[id] = incl;
                FramesSeen[id]++;

                if (self > s1) { t3 = t2; s3 = s2; t2 = t1; s2 = s1; t1 = id; s1 = self; }
                else if (self > s2) { t3 = t2; s3 = s2; t2 = id; s2 = self; }
                else if (self > s3) { t3 = id; s3 = self; }

                _frameSelf[id] = 0;
                _frameIncl[id] = 0;
                _frameCalls[id] = 0;
                _isTouched[id] = false;
            }
            _touchedCount = 0;

            row[o + Col.InstrCalls] = _frameCallTotal;
            _frameCallTotal = 0;
            row[o + Col.Top1Id] = t1;
            row[o + Col.Top1Ms] = (float)(s1 * FrameRecorder.TicksToMs);
            row[o + Col.Top2Id] = t2;
            row[o + Col.Top2Ms] = (float)(s2 * FrameRecorder.TicksToMs);
            row[o + Col.Top3Id] = t3;
            row[o + Col.Top3Ms] = (float)(s3 * FrameRecorder.TicksToMs);
        }

        public static void DiscardFrame()
        {
            for (int i = 0; i < _touchedCount; i++)
            {
                int id = _touched[i];
                _frameSelf[id] = 0;
                _frameIncl[id] = 0;
                _frameCalls[id] = 0;
                _isTouched[id] = false;
            }
            _touchedCount = 0;
            _frameCallTotal = 0;
        }

        public static void ResetTotals()
        {
            Array.Clear(TotSelf, 0, TotSelf.Length);
            Array.Clear(TotIncl, 0, TotIncl.Length);
            Array.Clear(TotCalls, 0, TotCalls.Length);
            Array.Clear(MaxSelf, 0, MaxSelf.Length);
            Array.Clear(MaxIncl, 0, MaxIncl.Length);
            Array.Clear(FramesSeen, 0, FramesSeen.Length);
            DiscardFrame();
        }

        public static void Patch(string targetsFile, ManualLogSource log)
        {
            if (Patched) return;
            var sw = Stopwatch.StartNew();
            var targets = ResolveTargets(targetsFile, log);

            var prefix = new HarmonyMethod(typeof(MethodTimer).GetMethod(nameof(Prefix))) { priority = Priority.First };
            var postfix = new HarmonyMethod(typeof(MethodTimer).GetMethod(nameof(Postfix))) { priority = Priority.Last };

            var names = new List<string>();
            Ids.Clear();
            int failed = 0;
            foreach (var m in targets)
            {
                try
                {
                    _harmony.Patch(m, prefix, postfix);
                    Ids[m.MethodHandle.Value] = names.Count;
                    names.Add(m.DeclaringType.FullName + ":" + m.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogWarning($"Could not instrument {m.DeclaringType?.FullName}:{m.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            int n = names.Count;
            Names = names.ToArray();
            MethodCount = n;
            _frameSelf = new long[n];
            _frameIncl = new long[n];
            _frameCalls = new int[n];
            _touched = new int[n];
            _isTouched = new bool[n];
            _touchedCount = 0;
            TotSelf = new long[n];
            TotIncl = new long[n];
            TotCalls = new long[n];
            MaxSelf = new long[n];
            MaxIncl = new long[n];
            FramesSeen = new int[n];
            _depth = 0;
            Patched = true;
            log.LogInfo($"Instrumented {n} methods ({failed} failed) in {sw.ElapsedMilliseconds} ms");
        }

        public static void Unpatch(ManualLogSource log)
        {
            if (!Patched) return;
            Collecting = false;
            var sw = Stopwatch.StartNew();
            _harmony.UnpatchSelf();
            Patched = false;
            log.LogInfo($"Removed method instrumentation in {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// Targets file syntax, one per line ('#' starts a comment):
        ///   @unity-messages AssemblyName [Update,LateUpdate,FixedUpdate,OnGUI]
        ///       every MonoBehaviour message method declared in that assembly (default: Update, LateUpdate, FixedUpdate)
        ///   Namespace.Type:Method     exact type full name (nested types use '+'), method name may contain '*'
        ///   -Namespace.Type:Method    exclude matches (applied after all includes)
        /// </summary>
        private static List<MethodInfo> ResolveTargets(string file, ManualLogSource log)
        {
            var include = new List<MethodInfo>();
            var exclude = new List<(Type type, Regex method)>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var raw in File.ReadAllLines(file))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("@unity-messages", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) { log.LogWarning("Bad targets line: " + raw); continue; }
                    var asm = assemblies.FirstOrDefault(a => a.GetName().Name == parts[1]);
                    if (asm == null) { log.LogWarning("Assembly not loaded: " + parts[1]); continue; }
                    var messages = parts.Length > 2 ? parts[2].Split(',') : new[] { "Update", "LateUpdate", "FixedUpdate" };
                    int before = include.Count;
                    foreach (var t in SafeGetTypes(asm))
                    {
                        if (t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            // Messages like OnRenderImage(src, dst) take parameters, so match by name only.
                            if (Array.IndexOf(messages, m.Name) >= 0) include.Add(m);
                        }
                    }
                    log.LogInfo($"{line}: {include.Count - before} methods");
                    continue;
                }

                if (line.StartsWith("@camera-callbacks", StringComparison.OrdinalIgnoreCase))
                {
                    // Methods subscribed to Camera.onPreCull/onPreRender/onPostRender right now (EFT's render hooks).
                    int before = include.Count;
                    foreach (var field in new[] { "onPreCull", "onPreRender", "onPostRender" })
                    {
                        var del = typeof(Camera).GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Delegate;
                        if (del == null) continue;
                        foreach (var d in del.GetInvocationList())
                        {
                            if (d.Method is MethodInfo mi) include.Add(mi);
                        }
                    }
                    log.LogInfo($"{line}: {include.Count - before} methods");
                    continue;
                }

                if (line.StartsWith("@callees", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = line.Substring("@callees".Length).Trim();
                    int before = include.Count;
                    foreach (var parent in ResolveSpec(spec, log))
                    {
                        include.Add(parent);
                        include.AddRange(Callees(parent));
                    }
                    log.LogInfo($"{line}: {include.Count - before} methods");
                    continue;
                }

                bool isExclude = line.StartsWith("-");
                if (isExclude) line = line.Substring(1);
                int colon = line.LastIndexOf(':');
                if (colon <= 0) { log.LogWarning("Bad targets line: " + raw); continue; }
                string typeName = line.Substring(0, colon);
                string methodGlob = line.Substring(colon + 1);
                var type = AccessTools.TypeByName(typeName);
                if (type == null) { log.LogWarning("Type not found: " + typeName); continue; }
                var re = new Regex("^" + Regex.Escape(methodGlob).Replace("\\*", ".*") + "$");

                if (isExclude)
                {
                    exclude.Add((type, re));
                    continue;
                }
                int found = 0;
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (re.IsMatch(m.Name)) { include.Add(m); found++; }
                }
                if (found == 0) log.LogWarning("No methods matched: " + line);
            }

            var seen = new HashSet<IntPtr>();
            var result = new List<MethodInfo>();
            foreach (var m in include)
            {
                // Generic methods/types are skipped: Mono shares their code across instantiations, so patching is unreliable.
                if (m.IsAbstract || m.IsGenericMethod || m.ContainsGenericParameters || m.DeclaringType.IsGenericType) continue;
                if (m.DeclaringType.Assembly == typeof(MethodTimer).Assembly) continue;
                if (exclude.Any(e => e.type == m.DeclaringType && e.method.IsMatch(m.Name))) continue;
                try
                {
                    if (m.GetMethodBody() == null) continue;
                }
                catch
                {
                    continue;
                }
                if (seen.Add(m.MethodHandle.Value)) result.Add(m);
            }
            return result;
        }

        private static List<MethodInfo> ResolveSpec(string spec, ManualLogSource log)
        {
            var result = new List<MethodInfo>();
            int colon = spec.LastIndexOf(':');
            if (colon <= 0) { log.LogWarning("Bad method spec: " + spec); return result; }
            var type = AccessTools.TypeByName(spec.Substring(0, colon));
            if (type == null) { log.LogWarning("Type not found: " + spec.Substring(0, colon)); return result; }
            var re = new Regex("^" + Regex.Escape(spec.Substring(colon + 1)).Replace("\\*", ".*") + "$");
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (re.IsMatch(m.Name)) result.Add(m);
            }
            if (result.Count == 0) log.LogWarning("No methods matched: " + spec);
            return result;
        }

        /// <summary>
        /// Methods called directly from <paramref name="parent"/>'s IL. Skips BCL methods and trivial
        /// property accessors. Virtual calls resolve to the declared method only, not overrides.
        /// </summary>
        private static IEnumerable<MethodInfo> Callees(MethodInfo parent)
        {
            var result = new List<MethodInfo>();
            IEnumerable<KeyValuePair<System.Reflection.Emit.OpCode, object>> body;
            try
            {
                body = PatchProcessor.ReadMethodBody(parent);
            }
            catch
            {
                return result;
            }
            foreach (var instr in body)
            {
                if (!(instr.Value is MethodInfo callee)) continue;
                if (instr.Key != System.Reflection.Emit.OpCodes.Call && instr.Key != System.Reflection.Emit.OpCodes.Callvirt) continue;
                string asm = callee.DeclaringType?.Assembly.GetName().Name ?? "";
                if (asm == "mscorlib" || asm == "netstandard" || asm.StartsWith("System")) continue;
                // Skip trivial bodies (Mathf helpers, field getters): timing them costs more than they do.
                // Property accessors need a bigger body to be worth it.
                try
                {
                    var b = callee.GetMethodBody();
                    if (b == null) continue;
                    int il = b.GetILAsByteArray().Length;
                    if (il < 32 || (callee.IsSpecialName && il < 64)) continue;
                }
                catch
                {
                    continue;
                }
                result.Add(callee);
            }
            return result;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
        }
    }
}
