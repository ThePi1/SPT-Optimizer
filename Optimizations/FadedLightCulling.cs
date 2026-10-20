using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Switch off lights that EFT has already faded to zero (on by default since 2026-10-19: batch tests showed
    /// −0.24 ms camera culling in 6/6 repeats and 0.00% visible change in the screenshot check).
    ///
    /// EFT's CullingLightObject fades each lamp by distance (by default from 50 m to 80 m) by setting
    /// Light.intensity, and leaves the Light component enabled at intensity 0. Unity still culls and sorts every
    /// enabled light per camera, so thousands of invisible far lamps cost main-thread time (the FarVertexLightsOff
    /// experiment measured ~0.25 ms for the far ones on Streets). This disables a light while EFT's own fade value for
    /// it is zero and re-enables it as soon as EFT fades it back in, so nothing visible changes by construction.
    ///
    /// Lamps can also be switched by the game (power switches: LampController.TurnLights, ControlledLampGroup).
    /// For a light we hold, the game's switch is recorded as its intended state, so it is restored correctly later.
    /// Lights with a flicker or volumetric component are left alone.
    /// </summary>
    internal static class FadedLightCulling
    {
        private static bool _enabled;
        private static ManualLogSource _log;
        // instance id -> (light, state the game wants when we release it)
        private static readonly Dictionary<int, (Light Light, bool Intent)> Held = new Dictionary<int, (Light, bool)>();

        private static readonly AccessTools.FieldRef<LampController, bool> LampOn = AccessTools.FieldRefAccess<LampController, bool>("bool_0");
        private static readonly AccessTools.FieldRef<ControlledLampGroup, Light[]> GroupLights =
            AccessTools.FieldRefAccess<ControlledLampGroup, Light[]>("_lights");

        public static int HeldCount => Held.Count;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value) Sweep();
                else ReleaseAll();
            }
        }

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(CullingLightObject), nameof(CullingLightObject.UpdateCullingObject)),
                postfix: new HarmonyMethod(typeof(FadedLightCulling), nameof(UpdateCullingObjectPostfix)));
            // LampController calls TurnLights every frame and it returns early unless the state changes (or force);
            // the prefix records whether it will actually switch, so the postfix only acts on real switches.
            harmony.Patch(AccessTools.Method(typeof(LampController), nameof(LampController.TurnLights)),
                prefix: new HarmonyMethod(typeof(FadedLightCulling), nameof(TurnLightsPrefix)),
                postfix: new HarmonyMethod(typeof(FadedLightCulling), nameof(TurnLightsPostfix)));
            // ControlledLampGroup.SetLightState runs on events only, but always writes light.enabled.
            harmony.Patch(AccessTools.Method(typeof(ControlledLampGroup), nameof(ControlledLampGroup.SetLightState)),
                postfix: new HarmonyMethod(typeof(FadedLightCulling), nameof(LampGroupPostfix)));
        }

        private static bool Eligible(CullingLightObject clo, Light light) =>
            light != null && clo.GetFlicker() == null && clo.GetComponent<VolumetricLight>() == null;

        private static void UpdateCullingObjectPostfix(CullingLightObject __instance, float multiplier)
        {
            if (!_enabled) return;
            var light = __instance.GetLight();
            if (!Eligible(__instance, light)) return;
            var cm = CullingManager.Instance;
            // Same effective value EFT applies (see CullingLightObject.UpdateCullingObject).
            float eff = (cm != null && cm.IsOpticEnabled() && Mathf.Approximately(multiplier, 0f)) ? 1f : multiplier;
            eff *= __instance.IntensityMultiplier;
            int id = light.GetInstanceID();
            if (eff <= 0f)
            {
                if (light.enabled && !Held.ContainsKey(id))
                {
                    Held[id] = (light, true);
                    light.enabled = false;
                    Stats.Add("fadedLights.disabled");
                }
            }
            else if (Held.TryGetValue(id, out var h))
            {
                Held.Remove(id);
                light.enabled = h.Intent;
                Stats.Add("fadedLights.restored");
            }
        }

        private static void TurnLightsPrefix(LampController __instance, bool on, bool force, out bool __state) =>
            __state = _enabled && Held.Count > 0 && (force || on != LampOn(__instance));

        private static void TurnLightsPostfix(LampController __instance, bool on, bool __state)
        {
            if (__state) RecordIntent(__instance.Lights, on);
        }

        private static void LampGroupPostfix(ControlledLampGroup __instance, bool state) => RecordIntent(GroupLights(__instance), state);

        private static void RecordIntent(Light[] lights, bool on)
        {
            if (lights == null || Held.Count == 0) return;
            foreach (var light in lights)
            {
                if (light == null || !Held.TryGetValue(light.GetInstanceID(), out var h)) continue;
                Held[light.GetInstanceID()] = (h.Light, on);
                if (light.enabled) light.enabled = false; // the game switched it on; it's still faded out, keep it off
                Stats.Add("fadedLights.lampSwitchWhileHeld");
            }
        }

        /// <summary>Hold every light that is already faded out (EFT only calls UpdateCullingObject when the value changes).</summary>
        public static void Sweep()
        {
            int n = 0;
            foreach (var clo in Object.FindObjectsOfType<CullingLightObject>())
            {
                var light = clo.GetLight();
                if (!clo.enabled || !Eligible(clo, light) || !light.enabled || light.intensity > 0f) continue;
                int id = light.GetInstanceID();
                if (Held.ContainsKey(id)) continue;
                Held[id] = (light, true);
                light.enabled = false;
                n++;
            }
            _log?.LogInfo($"Faded light culling: {n} faded-out lights disabled ({Held.Count} held)");
        }

        public static void ReleaseAll()
        {
            int n = 0;
            foreach (var h in Held.Values.ToList())
            {
                if (h.Light == null) continue;
                h.Light.enabled = h.Intent;
                n++;
            }
            Held.Clear();
            _log?.LogInfo($"Faded light culling: released {n} lights");
        }

        /// <summary>Periodic: drop destroyed lights, report the level.</summary>
        public static void Tick()
        {
            if (!_enabled) return;
            if (Time.frameCount % 300 == 0)
            {
                foreach (var key in Held.Where(k => k.Value.Light == null).Select(k => k.Key).ToList()) Held.Remove(key);
            }
            Stats.Set("fadedLights.held", Held.Count);
        }

        /// <summary>New raid: nothing from the last one is valid. Call before setting Enabled for the raid.</summary>
        public static void OnRaidStart()
        {
            Held.Clear();
            if (_enabled) Sweep();
        }
    }
}
