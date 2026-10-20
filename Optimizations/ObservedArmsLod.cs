using System.Collections.Generic;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// VISUAL TRADE-OFF (opt-in, Fika clients): far or off-screen observed players' arms animators at a reduced rate.
    ///
    /// Fika creates every observed player (remote humans, and on a client every bot) with ArmsUpdateMode.Manual, so
    /// Player.ArmsUpdate calls ArmsAnimatorCommon.Update(dt) for each of them every frame: ~0.47 ms self time for ~19
    /// players on the headless client (4.0.13 capture 20261020-100541). Their shots don't come from this animator: Fika
    /// replays them from the shooter's packets. So for players that qualify, the animator is evaluated only every
    /// <see cref="Interval"/>th frame and then advanced by all the time it missed; Unity fires the animation events of
    /// the whole interval in that one evaluation, so weapon sounds and hand-controller events arrive up to Interval-1
    /// frames late but none are lost. The hands controller's own update and ArmsLateUpdate (events, ballistics) still
    /// run every frame.
    ///
    /// Qualifies: alive, an observed player (never the local player, never a local bot), farther than
    /// <see cref="NearDistance"/>, and off-screen or farther than <see cref="FarDistance"/>; never while you aim down
    /// sights at an on-screen player. Bots only unless <see cref="IncludeHumans"/>. Never on the headless host.
    /// FarDistance should stay at or above Fika's IK Cull Distance, so on-screen players that still get IK aren't throttled.
    /// </summary>
    internal static class ObservedArmsLod
    {
        public static bool Enabled;
        public static bool IncludeHumans;
        public static int Interval = 2;
        public static float NearDistance = 25f;
        public static float FarDistance = 60f;

        private sealed class State
        {
            public bool Observed, Bot;
            public float Pending; // skipped frames' time, caught up on the next evaluation
            public int Skipped;   // frames skipped in a row
        }

        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        private static ManualLogSource _log;
        private static bool _headless;
        private static int _aimFrame = -1;
        private static bool _aiming;
        private static long _skipped, _caughtUp;
        private static float _nextFlush;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            if (!Fika.Present)
            {
                log.LogInfo("Observed arms reduced rate: Fika not installed, nothing to do");
                return;
            }
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.ArmsUpdate)),
                prefix: new HarmonyMethod(typeof(ObservedArmsLod), nameof(ArmsUpdatePrefix)));
        }

        private static bool LocalPlayerAiming()
        {
            if (_aimFrame == Time.frameCount) return _aiming;
            _aimFrame = Time.frameCount;
            _headless = Fika.IsHeadless;
            var main = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            _aiming = main != null && main.ProceduralWeaponAnimation != null && main.ProceduralWeaponAnimation.IsAiming;
            return _aiming;
        }

        /// <summary>Same body as Player.ArmsUpdate, except the arms animator evaluation, which may be deferred.</summary>
        private static bool ArmsUpdatePrefix(Player __instance, float deltaTime, Player.AbstractHandsController ____handsController,
            ref bool ____armsupdated, ref float ____armsTime)
        {
            if (!Enabled && States.Count == 0) return true;
            int id = __instance.GetInstanceID();
            if (!States.TryGetValue(id, out var s))
            {
                // Classified once: a player's type and side don't change during a raid.
                s = new State { Observed = Fika.IsObserved(__instance) };
                s.Bot = s.Observed && Fika.IsObservedBot(__instance);
                States[id] = s;
            }
            if (!s.Observed) return true;

            bool aiming = LocalPlayerAiming();
            bool visible = __instance.IsVisible;
            float dist = __instance.Distance;
            bool qualifies = Enabled && !_headless && (s.Bot || IncludeHumans) && __instance.ArmsUpdateMode == Player.EUpdateMode.Manual &&
                dist > NearDistance && (!visible || dist > FarDistance) && !(aiming && visible);
            if (!qualifies && s.Pending <= 0f) return true;

            ____handsController?.ManualUpdate(deltaTime);
            // Stagger by player so the evaluations spread over frames.
            bool skip = qualifies && s.Skipped < Interval - 1 && ((Time.frameCount + id) % Interval) != 0;
            if (skip)
            {
                s.Pending += deltaTime;
                s.Skipped++;
                _skipped++;
            }
            else
            {
                float dt = deltaTime + s.Pending;
                if (s.Pending > 0f) _caughtUp++;
                s.Pending = 0f;
                s.Skipped = 0;
                __instance.ArmsAnimatorCommon.Update(dt);
                __instance.UnderbarrelWeaponArmsAnimator?.Update(dt);
            }
            ____armsupdated = true;
            ____armsTime = deltaTime;
            __instance.ArmsLateUpdate(deltaTime);
            return false;
        }

        /// <summary>Leaving a raid: drop per-player state.</summary>
        public static void ReleaseAll() => States.Clear();

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextFlush) return;
            _nextFlush = Time.realtimeSinceStartup + 1f;
            if (_skipped > 0) Stats.Add("observedArmsLod.skippedUpdates", _skipped);
            if (_caughtUp > 0) Stats.Add("observedArmsLod.catchUpUpdates", _caughtUp);
            _skipped = _caughtUp = 0;
        }
    }
}
