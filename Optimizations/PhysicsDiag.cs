using System.Diagnostics;
using BepInEx.Logging;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// DIAGNOSTIC (batch 1): what keeps EFT's physics simulation running, and what it costs.
    ///
    /// EFT runs Physics.Simulate (script mode, PhysicsExtensions.Simulation) only while at least one
    /// "supported" rigidbody is active (PhysicsExtensions.UpdateController). Corpse ragdolls register for up to 15 s or until
    /// asleep; dropped loot, grenades, the BTR's wheels and suspension register too. This logs, every 30 s:
    ///  - physics.simFrames / simMs / simMaxMs: frames that simulated and their cost
    ///  - physics.vehicleOnlyFrames: simulated frames where only vehicle bodies (BTR) were registered
    ///  - physics.bodies.&lt;kind&gt;: registered bodies by kind (sampled once a second, maximum)
    ///  - corpse.settled / corpse.timeout / corpse.settleSecMax / corpse.settleSecTotal: ragdoll settle times
    /// No behaviour change.
    /// </summary>
    internal static class PhysicsDiag
    {
        public static bool Enabled = true;
        private static long _t0;
        private static bool _vehicleOnly;
        private static float _nextSample;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            var sim = typeof(PhysicsExtensions.Simulation);
            foreach (var name in new[] { "Simulate", "SmoothSimulate" })
                harmony.Patch(AccessTools.Method(sim, name),
                    prefix: new HarmonyMethod(typeof(PhysicsDiag), nameof(SimPrefix)),
                    postfix: new HarmonyMethod(typeof(PhysicsDiag), nameof(SimPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Corpse), nameof(Corpse.CheckCorpseIsStill)),
                postfix: new HarmonyMethod(typeof(PhysicsDiag), nameof(CorpseStillPostfix)));
        }

        private static bool _allSleeping;

        private static void SimPrefix()
        {
            _t0 = 0;
            if (!Enabled || !PhysicsExtensions.Simulation.CanRunSimulate) return;
            // Would skipping this step be safe? Only if every registered body is asleep (candidate idea).
            var list = PhysicsExtensions.UpdateController._rigidbodies;
            bool all = list.Count > 0;
            for (int i = 0; i < list.Count && all; i++)
            {
                var rb = list[i].rigidbody;
                if (rb != null && rb.gameObject.activeInHierarchy && !rb.isKinematic && !rb.IsSleeping()) all = false;
            }
            _allSleeping = all;
            _t0 = Stopwatch.GetTimestamp();
        }

        private static void SimPostfix()
        {
            if (_t0 == 0) return;
            double ms = (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;
            _t0 = 0;
            Stats.Add("physics.simCalls");
            if (_allSleeping) Stats.Add("physics.allSleepingCalls");
            Stats.Add("physics.simMs", ms);
            Stats.Max("physics.simMaxMs", ms);
            if (_vehicleOnly) Stats.Add("physics.vehicleOnlyCalls");
        }

        private static void CorpseStillPostfix(bool sleeping, float timePass, bool __result)
        {
            if (!Enabled || !__result) return;
            Stats.Add(sleeping ? "corpse.settled" : "corpse.timeout");
            Stats.Add("corpse.settleSecTotal", timePass);
            Stats.Max("corpse.settleSecMax", timePass);
        }

        /// <summary>Once a second: classify the registered bodies.</summary>
        public static void Tick()
        {
            if (!Enabled || Time.realtimeSinceStartup < _nextSample) return;
            _nextSample = Time.realtimeSinceStartup + 1f;
            var list = PhysicsExtensions.UpdateController._rigidbodies;
            int corpse = 0, loot = 0, vehicle = 0, other = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var rb = list[i].rigidbody;
                if (rb == null || !rb.gameObject.activeInHierarchy) continue;
                if (rb.GetComponentInParent<Corpse>() != null) corpse++;
                else if (rb.GetComponentInParent<LootItem>() != null) loot++;
                else if (rb.GetComponentInParent<WheelDrive>() != null || rb.GetComponentInParent<VehicleSuspension>() != null || rb.GetComponentInParent<EFT.Vehicle.BTRVehicle>() != null)
                {
                    vehicle++;
                    Stats.Max("physics.vehicle[" + rb.name + "]", 1);
                }
                else
                {
                    other++;
                    Stats.Max("physics.other[" + rb.name + "]", 1);
                }
            }
            _vehicleOnly = vehicle > 0 && corpse + loot + other == 0;
            if (corpse > 0) Stats.Max("physics.bodies.corpse", corpse);
            if (loot > 0) Stats.Max("physics.bodies.loot", loot);
            if (vehicle > 0) Stats.Max("physics.bodies.vehicle", vehicle);
            if (other > 0) Stats.Max("physics.bodies.other", other);
            if (PhysicsExtensions.Simulation.UpdateEnabled) Stats.Add("physics.secondsOn");
        }
    }
}
