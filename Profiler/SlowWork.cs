using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Attributes single long work items, the usual cause of 10–40 ms hitches:
    ///  - EFT's JobScheduler continuations (Diz.Jobs, run in JobScheduler.LateUpdate): Class1031.Execute
    ///  - async/await continuations posted to Unity's SynchronizationContext (Update.ScriptRunDelayedTasks)
    ///  - coroutine steps (SetupCoroutine.InvokeMoveNext, Update.ScriptRunDelayedDynamicFrameRate etc.)
    /// Each item is timed only while a capture is recording; items slower than ThresholdMs are recorded with the
    /// name of the async method / coroutine / job behind them (name resolution only for slow items).
    /// </summary>
    internal static class SlowWork
    {
        public struct Item
        {
            public float Time;
            public int Frame;
            public float Ms;
            public string Kind;
            public string Name;
        }

        public static float ThresholdMs = 2f;
        public static readonly List<Item> Items = new List<Item>(4096);
        private static int _depth; // nested items: only time the outermost
        private static ManualLogSource _log;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static long _captureStart;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            Try(harmony, AccessTools.Method(typeof(Class1031), nameof(Class1031.Execute)), nameof(JobPrefix), nameof(JobPostfix), "JobScheduler continuations");
            Try(harmony, AccessTools.Method(AccessTools.TypeByName("UnityEngine.SetupCoroutine"), "InvokeMoveNext"), nameof(Prefix), nameof(CoroutinePostfix), "coroutine steps");
            Try(harmony, AccessTools.Method(AccessTools.TypeByName("UnityEngine.UnitySynchronizationContext+WorkRequest"), "Invoke"), nameof(Prefix), nameof(TaskPostfix), "async continuations");
        }

        private static void Try(Harmony harmony, MethodBase target, string prefix, string postfix, string what)
        {
            try
            {
                if (target == null) throw new MissingMethodException(what);
                harmony.Patch(target, new HarmonyMethod(typeof(SlowWork), prefix) { priority = Priority.First }, new HarmonyMethod(typeof(SlowWork), postfix) { priority = Priority.Last });
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Slow-work logger: can't hook {what}: {ex.Message}");
            }
        }

        /// <summary>Called at each frame start: a work item that threw never ran its postfix, so don't let the nesting count leak.</summary>
        public static void ResetDepth() => _depth = 0;

        public static void Begin()
        {
            Items.Clear();
            _depth = 0;
            _captureStart = Stopwatch.GetTimestamp();
        }

        // ---- hooks ----

        private static void Prefix(out long __state)
        {
            if (!FrameRecorder.Recording) { __state = 0; return; }
            _depth++;
            __state = Stopwatch.GetTimestamp();
        }

        private static void JobPrefix(out long __state) => Prefix(out __state);

        private static bool End(long start, out float ms)
        {
            ms = 0f;
            if (start == 0) return false;
            _depth--;
            ms = (float)((Stopwatch.GetTimestamp() - start) * TicksToMs);
            return _depth == 0 && ms >= ThresholdMs;
        }

        private static void JobPostfix(ref Struct263 continuation, long __state)
        {
            if (!End(__state, out float ms)) return;
            string job = continuation.jobYield != null ? continuation.jobYield.Name + "/" + continuation.jobStageName : "(no job)";
            Add("job", job + " " + Describe(continuation.action), ms);
        }

        private static void CoroutinePostfix(IEnumerator enumerator, long __state)
        {
            if (!End(__state, out float ms)) return;
            Add("coroutine", enumerator != null ? enumerator.GetType().FullName : "?", ms);
        }

        private static void TaskPostfix(object __instance, long __state)
        {
            if (!End(__state, out float ms)) return;
            string name = "?";
            try
            {
                var t = __instance.GetType();
                var state = t.GetField("m_DelagateState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(__instance);
                var cb = t.GetField("m_DelagateCallback", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(__instance) as Delegate;
                name = state is Delegate d ? Describe(d) : (cb != null ? Describe(cb) : "?");
            }
            catch
            {
                // best effort
            }
            Add("task", name, ms);
        }

        private static void Add(string kind, string name, float ms)
        {
            if (Items.Count >= 100000) return;
            Items.Add(new Item
            {
                Time = (float)((Stopwatch.GetTimestamp() - _captureStart) / (double)Stopwatch.Frequency),
                Frame = UnityEngine.Time.frameCount,
                Ms = ms,
                Kind = kind,
                Name = name,
            });
        }

        /// <summary>Best name for a delegate: the async state machine it resumes, else Type.Method.</summary>
        public static string Describe(Delegate d)
        {
            if (d == null) return "null";
            object target = d.Target;
            if (target != null)
            {
                // AsyncMethodBuilderCore.MoveNextRunner / continuation wrappers keep the state machine in a field.
                foreach (var fieldName in new[] { "m_stateMachine", "_stateMachine", "StateMachine", "m_continuation", "m_action" })
                {
                    var f = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f == null) continue;
                    var inner = f.GetValue(target);
                    if (inner is Delegate innerDelegate && !ReferenceEquals(innerDelegate, d)) return Describe(innerDelegate);
                    if (inner != null) return "async " + inner.GetType().FullName;
                }
                return target.GetType().FullName + "." + d.Method.Name;
            }
            return d.Method.DeclaringType?.FullName + "." + d.Method.Name;
        }
    }
}
