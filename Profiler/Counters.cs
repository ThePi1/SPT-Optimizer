using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Records Unity's built-in profiler counters and markers that work in a release (non-development) player.
    /// Markers flagged AvailabilityNonDevelopment give real per-frame native timings (culling, shadows, etc.);
    /// counters give draw calls, visible renderers, memory and so on. Every available stat is listed to
    /// available_counters.txt at capture start so we can see what this player exposes.
    /// </summary>
    internal static class Counters
    {
        public const int MaxCounters = 192;

        private static readonly ProfilerRecorder[] Recorders = new ProfilerRecorder[MaxCounters];
        private static readonly float[] Scale = new float[MaxCounters];
        public static string[] Names = new string[0];
        private static int _count;

        // Categories we care most about get picked first when there are more stats than columns.
        private static readonly string[] CategoryPriority =
        {
            "Render", "Camera", "Lighting", "Animation", "Physics", "Scripts", "GC", "Memory", "Particles", "Audio", "Loading", "Gui", "Ai", "Video",
        };

        public static void Begin(string captureDir)
        {
            End();
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);

            var all = new List<(ProfilerRecorderHandle h, ProfilerRecorderDescription d, string cat)>();
            foreach (var h in handles)
            {
                ProfilerRecorderDescription d;
                try
                {
                    d = ProfilerRecorderHandle.GetDescription(h);
                }
                catch
                {
                    continue;
                }
                all.Add((h, d, d.Category.Name));
            }

            bool Usable(ProfilerRecorderDescription d) =>
                (d.Flags & MarkerFlags.Counter) != 0 || (d.Flags & MarkerFlags.AvailabilityNonDevelopment) != 0;

            int Priority(string cat)
            {
                int i = Array.FindIndex(CategoryPriority, c => string.Equals(c, cat, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? CategoryPriority.Length : i;
            }

            var chosen = all.Where(x => Usable(x.d))
                .OrderBy(x => Priority(x.cat))
                .ThenBy(x => x.d.Name, StringComparer.Ordinal)
                .Take(MaxCounters)
                .ToList();

            var names = new List<string>();
            foreach (var x in chosen)
            {
                ProfilerRecorder rec;
                try
                {
                    // Default (24) does not include StartImmediately in this Unity build, so start explicitly.
                    rec = new ProfilerRecorder(x.h, 1, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.StartImmediately);
                }
                catch
                {
                    continue;
                }
                if (!rec.Valid)
                {
                    rec.Dispose();
                    continue;
                }

                string unit;
                float scale;
                switch (x.d.UnitType)
                {
                    case ProfilerMarkerDataUnit.TimeNanoseconds: unit = "ms"; scale = 1e-6f; break;
                    case ProfilerMarkerDataUnit.Bytes: unit = "MB"; scale = 1f / 1048576f; break;
                    case ProfilerMarkerDataUnit.Percent: unit = "%"; scale = 1f; break;
                    case ProfilerMarkerDataUnit.FrequencyHz: unit = "Hz"; scale = 1f; break;
                    default: unit = "n"; scale = 1f; break;
                }
                Recorders[_count] = rec;
                Scale[_count] = scale;
                names.Add($"ctr:{x.cat}/{x.d.Name} [{unit}]".Replace(",", ";"));
                _count++;
            }
            Names = names.ToArray();

            // Full inventory of what this player exposes, for reference.
            var sb = new StringBuilder();
            sb.AppendLine($"{all.Count} stats available, {_count} recorded (max {MaxCounters}). Flags: N=works in non-development player, C=counter.");
            sb.AppendLine("rec  flags  category / name  [unit, type]");
            var recorded = new HashSet<string>(chosen.Select(x => x.cat + "/" + x.d.Name));
            foreach (var x in all.OrderBy(x => x.cat).ThenBy(x => x.d.Name, StringComparer.Ordinal))
            {
                string f = ((x.d.Flags & MarkerFlags.AvailabilityNonDevelopment) != 0 ? "N" : "-") + ((x.d.Flags & MarkerFlags.Counter) != 0 ? "C" : "-");
                sb.AppendLine($"{(recorded.Contains(x.cat + "/" + x.d.Name) ? "*" : " ")}    {f}     {x.cat} / {x.d.Name}  [{x.d.UnitType}, {x.d.DataType}]");
            }
            try
            {
                File.WriteAllText(Path.Combine(captureDir, "available_counters.txt"), sb.ToString());
            }
            catch
            {
                // Non-essential.
            }
        }

        public static void Fill(float[] row, int offset)
        {
            for (int i = 0; i < _count; i++)
            {
                row[offset + i] = Recorders[i].LastValue * Scale[i];
            }
        }

        public static void End()
        {
            for (int i = 0; i < _count; i++) Recorders[i].Dispose();
            _count = 0;
            Names = new string[0];
        }

        public static string Status() => _count.ToString(CultureInfo.InvariantCulture) + " counters/markers";
    }
}
