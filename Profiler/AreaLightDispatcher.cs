using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Every enabled AreaLight subscribes its own AreaLight.method_2 to Camera.onPreCull (~1,170 delegates on
    /// Streets). For each camera each one checks its intensity, calls CompareTag twice (native calls), and the lit
    /// ones rebuild their command buffer. This replaces all of them with one delegate that checks the camera tag
    /// once and then does exactly what method_2 does for each light, in the same order. Output is identical.
    /// </summary>
    internal static class AreaLightDispatcher
    {
        private static readonly List<AreaLight> Lights = new List<AreaLight>();
        private static bool _installed;

        public static int Count => Lights.Count;

        public static void Install()
        {
            if (_installed) return;
            Lights.Clear();
            foreach (var light in Object.FindObjectsOfType<AreaLight>())
            {
                if (!light.isActiveAndEnabled) continue;
                Camera.onPreCull -= light.method_2; // removes the light's own subscription (delegate equality by target+method)
                Lights.Add(light);
            }
            Camera.onPreCull += Dispatch;
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            Camera.onPreCull -= Dispatch;
            // Give the lights their own hooks back, unless one re-subscribed itself (OnDisable/OnEnable during the step).
            var current = Camera.onPreCull?.GetInvocationList();
            foreach (var light in Lights)
            {
                if (light == null) continue;
                bool subscribed = false;
                if (current != null)
                    foreach (var d in current)
                        if (ReferenceEquals(d.Target, light)) { subscribed = true; break; }
                if (!subscribed) Camera.onPreCull += light.method_2;
            }
            Lights.Clear();
            _installed = false;
        }

        /// <summary>Mirror AreaLight's own subscribe/unsubscribe while the dispatcher is installed.</summary>
        public static void PatchLifecycle(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(AreaLight), nameof(AreaLight.OnEnable)), postfix: new HarmonyMethod(typeof(AreaLightDispatcher), nameof(OnEnablePostfix)));
            harmony.Patch(AccessTools.Method(typeof(AreaLight), nameof(AreaLight.OnDisable)), postfix: new HarmonyMethod(typeof(AreaLightDispatcher), nameof(OnDisablePostfix)));
        }

        private static void OnEnablePostfix(AreaLight __instance)
        {
            if (!_installed) return;
            Camera.onPreCull -= __instance.method_2; // it just subscribed itself
            if (!Lights.Contains(__instance)) Lights.Add(__instance);
        }

        private static void OnDisablePostfix(AreaLight __instance)
        {
            if (_installed) Lights.Remove(__instance);
        }

        private static void Dispatch(Camera cam)
        {
            if (!(cam.CompareTag("MainCamera") || cam.CompareTag("OpticCamera"))) return;
            for (int i = 0; i < Lights.Count; i++)
            {
                var light = Lights[i];
                // Same conditions as AreaLight.method_2 (minus the tag check done once above). Enable/disable is
                // tracked by the OnEnable/OnDisable postfixes below, so no per-frame isActiveAndEnabled native call.
                if (light.m_Intensity <= 0f || !light.method_0()) continue;
                light.SetUpCommandBuffer(cam);
            }
        }
    }
}
