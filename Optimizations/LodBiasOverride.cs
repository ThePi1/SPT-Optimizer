using BepInEx.Logging;
using Comfort.Common;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// VISIBLE TRADE-OFF (opt-in): object LOD bias below EFT's minimum of 2.
    ///
    /// EFT applies its "Object LOD quality" setting (2–4) through GameGraphicsClass.smethod_9 → QualitySettings.lodBias;
    /// CameraLodBiasController scales it temporarily per camera (optics) and restores it. This substitutes the
    /// configured value in that one place, so the per-camera scaling keeps working on top of it.
    /// Measured on Streets (batch 2): bias 1 = −0.11 ms main thread, −0.37 ms render thread, −390 draw calls; mid-distance
    /// trees thin out and small debris at ~40–60 m disappears (shots in capture 20261019-153229).
    /// </summary>
    internal static class LodBiasOverride
    {
        private static float _value; // 0 = off (use the game setting)
        private static ManualLogSource _log;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(GameGraphicsClass), nameof(GameGraphicsClass.smethod_9)),
                prefix: new HarmonyMethod(typeof(LodBiasOverride), nameof(SetBiasPrefix)));
        }

        private static void SetBiasPrefix(ref float bias)
        {
            if (_value > 0f) bias = _value;
        }

        public static float Value
        {
            get => _value;
            set
            {
                value = value > 0f ? Mathf.Clamp(value, 0.5f, 4f) : 0f;
                if (Mathf.Approximately(value, _value)) return;
                _value = value;
                float apply = value > 0f ? value : GameSetting();
                if (apply > 0f) QualitySettings.lodBias = apply;
                _log?.LogInfo(value > 0f ? $"LOD bias override: {value}" : $"LOD bias override off (game setting {apply})");
            }
        }

        private static float GameSetting()
        {
            try
            {
                return Singleton<SharedGameSettingsClass>.Instantiated ? Singleton<SharedGameSettingsClass>.Instance.Graphics.Settings.LodBias.Value : 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
