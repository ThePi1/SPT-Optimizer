using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine.LowLevel;

namespace SPTOptimizer.Profiler
{
    /// <summary>Type tag for the timing markers this plugin inserts into the PlayerLoop.</summary>
    internal sealed class ProfilerMarker { }

    /// <summary>
    /// Inserts timing markers into Unity's PlayerLoop: one before every phase subsystem (or every phase)
    /// and one at the end of each phase. EFT rebuilds the loop at runtime (its own custom systems,
    /// texture streaming swaps during raid load), so <see cref="PlayerLoop.SetPlayerLoop"/> is patched
    /// to strip and re-add the markers on every call.
    /// </summary>
    internal static class LoopInjector
    {
        /// <summary>When true, every SetPlayerLoop call gets markers; when false, they're stripped.</summary>
        public static bool Active;

        /// <summary>One marker per subsystem (true) or one per top-level phase (false).</summary>
        public static bool PerSubsystem = true;

        public static void Apply(bool active)
        {
            Active = active;
            // The prefix below does the actual injecting/stripping.
            PlayerLoop.SetPlayerLoop(PlayerLoop.GetCurrentPlayerLoop());
        }

        public static PlayerLoopSystem Inject(PlayerLoopSystem root)
        {
            root = Strip(root);
            if (root.subSystemList == null) return root;

            var phases = root.subSystemList;
            var newPhases = new PlayerLoopSystem[phases.Length];
            var list = new List<PlayerLoopSystem>();
            for (int i = 0; i < phases.Length; i++)
            {
                var phase = phases[i];
                string phaseName = phase.type != null ? phase.type.Name : "Phase" + i;
                var subs = phase.subSystemList ?? new PlayerLoopSystem[0];

                list.Clear();
                if (i == 0) list.Add(new PlayerLoopSystem { type = typeof(ProfilerMarker), updateDelegate = FrameRecorder.FrameStart });

                if (PerSubsystem)
                {
                    for (int s = 0; s < subs.Length; s++)
                    {
                        string subName = subs[s].type != null ? subs[s].type.Name : "Sub" + s;
                        list.Add(Marker(FrameRecorder.GetSlot(phaseName + "." + subName)));
                        list.Add(subs[s]);
                    }
                }
                else
                {
                    list.Add(Marker(FrameRecorder.GetSlot(phaseName)));
                    list.AddRange(subs);
                }
                list.Add(Marker(FrameRecorder.GapSlot));

                phase.subSystemList = list.ToArray();
                newPhases[i] = phase;
            }
            root.subSystemList = newPhases;
            return root;
        }

        /// <summary>Returns the loop with all markers removed. Never mutates arrays of the input.</summary>
        public static PlayerLoopSystem Strip(PlayerLoopSystem system)
        {
            if (system.subSystemList == null || !ContainsMarker(system)) return system;

            var kept = new List<PlayerLoopSystem>(system.subSystemList.Length);
            foreach (var sub in system.subSystemList)
            {
                if (sub.type == typeof(ProfilerMarker)) continue;
                kept.Add(Strip(sub));
            }
            system.subSystemList = kept.ToArray();
            return system;
        }

        private static bool ContainsMarker(PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return false;
            foreach (var sub in system.subSystemList)
            {
                if (sub.type == typeof(ProfilerMarker) || ContainsMarker(sub)) return true;
            }
            return false;
        }

        private static PlayerLoopSystem Marker(int slot)
        {
            return new PlayerLoopSystem { type = typeof(ProfilerMarker), updateDelegate = () => FrameRecorder.Mark(slot) };
        }

        /// <summary>Two-level dump of the current loop without markers, for the capture metadata.</summary>
        public static string Describe()
        {
            var root = Strip(PlayerLoop.GetCurrentPlayerLoop());
            var sb = new StringBuilder();
            if (root.subSystemList == null) return "(empty)";
            foreach (var phase in root.subSystemList)
            {
                sb.Append(phase.type != null ? phase.type.Name : "?").Append('\n');
                if (phase.subSystemList == null) continue;
                foreach (var sub in phase.subSystemList)
                {
                    sb.Append("    ").Append(sub.type != null ? sub.type.FullName : "?").Append('\n');
                }
            }
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(PlayerLoop), nameof(PlayerLoop.SetPlayerLoop))]
    internal static class SetPlayerLoopPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref PlayerLoopSystem loop)
        {
            loop = LoopInjector.Active ? LoopInjector.Inject(loop) : LoopInjector.Strip(loop);
        }
    }
}
