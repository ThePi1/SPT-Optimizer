using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Correctness and diagnostic counters for candidates under test. Each candidate adds to named counters; every
    /// 30 s the non-empty ones are logged as one "Stats:" line and reset. Keys are "candidate.counter".
    /// </summary>
    internal static class Stats
    {
        private const float Period = 30f;
        private static readonly Dictionary<string, double> Values = new Dictionary<string, double>();
        private static readonly HashSet<string> MaxKeys = new HashSet<string>();
        private static float _next = -1f;

        public static void Add(string key, double value = 1)
        {
            Values.TryGetValue(key, out double v);
            Values[key] = v + value;
        }

        /// <summary>Keep the maximum seen this period.</summary>
        public static void Max(string key, double value)
        {
            MaxKeys.Add(key);
            if (!Values.TryGetValue(key, out double v) || value > v) Values[key] = value;
        }

        /// <summary>A level (current count etc.): the last value set this period.</summary>
        public static void Set(string key, double value) => Values[key] = value;

        public static void Tick(ManualLogSource log)
        {
            float now = Time.realtimeSinceStartup;
            if (_next < 0f) _next = now + Period;
            if (now < _next) return;
            _next = now + Period;
            if (Values.Count == 0) return;
            log.LogInfo($"Stats ({Period:0} s): " + string.Join(", ", Values.OrderBy(k => k.Key).Select(k => $"{k.Key}={Format(k.Value)}")));
            Values.Clear();
            MaxKeys.Clear();
        }

        private static string Format(double v) => v == System.Math.Floor(v) && System.Math.Abs(v) < 1e9 ? ((long)v).ToString() : v.ToString("0.###");
    }
}
