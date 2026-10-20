using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Skips character-controller moves at impossible speeds.
    ///
    /// SimpleCharacterController.Move splits the motion into 0.3 m sub-steps, each with a neighbour query and
    /// depenetration pass. A character that is "falling" forever builds up speed without limit. On Streets this is
    /// the BTR gunner (shooterBTR) while it's parked off-map at (1000, -500, 1000) waiting for the BTR: its position
    /// is held there, but every frame EFT still sub-steps its whole fall (measured: 16–135 m per frame, 4,000+ m/s,
    /// hundreds of sub-steps per frame, growing for as long as the BTR hasn't arrived; EFT only gives up at 1,000
    /// sub-steps). The move achieves nothing, so skipping it changes nothing visible.
    ///
    /// The threshold (default 200 m/s) is far above anything legitimate: sprinting is ~7 m/s, a long fall ~30 m/s.
    /// </summary>
    internal static class RunawayMoveGuard
    {
        public static bool Enabled = true;
        public static float MaxSpeed = 200f;

        private static ManualLogSource _log;
        private static readonly HashSet<int> Logged = new HashSet<int>();
        public static long Skipped;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(SimpleCharacterController), nameof(SimpleCharacterController.Move)),
                prefix: new HarmonyMethod(typeof(RunawayMoveGuard), nameof(MovePrefix)) { priority = Priority.First });
        }

        /// <summary>True when a move of this size over this time is a runaway (and the guard is on).</summary>
        public static bool IsRunaway(Vector3 motion, float deltaTime) =>
            Enabled && motion.sqrMagnitude > MaxSpeed * MaxSpeed * Mathf.Max(deltaTime, 0.001f) * Mathf.Max(deltaTime, 0.001f);

        private static bool MovePrefix(SimpleCharacterController __instance, Vector3 motion, float deltaTime, ref CollisionFlags __result)
        {
            if (!IsRunaway(motion, deltaTime)) return true;
            Skipped++;
            int id = __instance.GetInstanceID();
            if (Logged.Count < 50 && Logged.Add(id))
            {
                var player = __instance.GetComponentInParent<Player>();
                var pos = __instance.transform.position;
                _log?.LogInfo($"Runaway move guard: skipping moves of {(player != null ? $"{player.Profile?.Info?.Settings?.Role} {player.Profile?.Nickname}" : __instance.name)} " +
                              $"at ({pos.x:0},{pos.y:0},{pos.z:0}): {motion.magnitude:0.0} m in {deltaTime * 1000f:0.0} ms ({motion.magnitude / Mathf.Max(deltaTime, 0.001f):0} m/s). Logged once per character.");
            }
            __result = CollisionFlags.None;
            return false;
        }

        public static void ResetLog() => Logged.Clear();
    }
}
