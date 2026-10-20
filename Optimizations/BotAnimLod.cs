using System.Collections.Generic;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// GAMEPLAY TRADE-OFF (opt-in prototype): off-screen distant bots' body animators run at half rate.
    ///
    /// Every bot's 22-layer body Animator is evaluated every frame, even off-screen (EFT needs its root motion and the
    /// FootStep curve): ~65 µs per off-screen bot on Streets. For off-screen bots beyond <see cref="OffscreenDistance"/>,
    /// every other frame the animator's culling mode is set to CullCompletely right before Unity's animation update, so
    /// Unity skips it entirely. On the other frames it advances by all the time it missed (Animator.speed × elapsed /
    /// frame time, scaling whatever speed EFT set), and MovementContext.LastDeltaTime is stretched to match. EFT's own
    /// VisualPass sets the culling mode again every LateUpdate; we also restore it.
    ///
    /// v1 paused by disabling the Animator; measured: −0.96 ms in the animation update but +0.95 ms in
    /// Update.DirectorUpdate (re-enabling an Animator re-initialises it), net zero. CullCompletely avoids that.
    ///
    /// Root motion stays exact: EFT applies it from the animator's OnStateMove (MovementState.ManualAnimatorMoveUpdate),
    /// which only runs when the animator was evaluated. That call is also how "last evaluated" is detected, so if Unity
    /// evaluates anyway on a skip frame (a renderer of the bot was visible, e.g. in a shadow map), the next frame isn't
    /// scaled. On-screen bots are never touched. Footsteps follow the sign of the FootStep curve and still flip.
    /// Validation counters: average horizontal speed of qualifying bots with the feature on vs off (should match).
    ///
    /// The catch-up evaluation still delivers ~20% less root motion (a property of Unity's animator), so LOCAL bots
    /// (self-hosted or the Fika host, where root motion moves them) run ~20-25% slower. Since 0.3.0 it therefore applies
    /// by default only to observed bots on a Fika client: their position is written from the host's snapshots every
    /// frame (ObservedPlayer.ManualStateUpdate), so root motion moves nothing and the shortfall is invisible.
    /// <see cref="LocalBots"/> brings back the old behaviour. Never on the headless host.
    /// </summary>
    internal static class BotAnimLod
    {
        public static bool Enabled;
        public static bool Validate = true;
        public static bool LocalBots; // also local AI bots (they move ~20-25% slower); default: Fika observed bots only
        public static float OffscreenDistance = 10f;
        public static float VisibleDistance = 80f; // unused since v2 (on-screen bots aren't culled by CullCompletely)

        private sealed class State
        {
            public float SinceEval;        // the skipped frame's time, caught up on the next frame
            public int LastEvalFrame = -1;
            public bool Culled;            // we set CullCompletely this frame
            public AnimatorCullingMode SavedMode;
            public float BaseSpeed = -1f;  // EFT's speed while we scale it this frame
            public float ScaledSpeed;
            public Vector3 LastPos;
            public bool HasPos;
            public int Bucket;             // movement state bucket at this frame's ManualUpdate
            public int LastManualFrame = -1;
            public bool PendingCatchUp;    // the next evaluation should be the scaled catch-up
        }

        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        // Unity Animator instance id -> state (the root-motion callback only hands us the Animator).
        private static readonly Dictionary<int, State> ByAnimator = new Dictionary<int, State>();
        private static ManualLogSource _log;
        // Accumulated here and handed to Stats once a second (no per-bot string work in the hot path).
        private static double _onMeters, _onSeconds, _offMeters, _offSeconds;
        // [on/off, bucket]: bucket 0 = Run, 1 = Sprint, 2 = other states. Displacement and animator root motion, per second.
        private static readonly double[,] DispM = new double[2, 3], DispS = new double[2, 3], RootM = new double[2, 3];
        private static readonly string[] Buckets = { "run", "sprint", "other" };

        private static int Bucket(MovementContext mc)
        {
            var st = mc.CurrentState;
            if (st == null) return 2;
            var n = st.Name;
            return n == EPlayerState.Run ? 0 : n == EPlayerState.Sprint ? 1 : 2;
        }
        private static long _culled, _scaled, _evaluatedAnyway;
        // Catch-up diagnostics: root motion per catch-up evaluation vs per normal evaluation (expect ~2x), whether the
        // scaled speed was in effect, and whether the animator ran before this frame's ManualUpdate (decisions late).
        private static double _rootCatchUp, _rootNormal;
        private static long _nCatchUp, _nNormal, _speedApplied, _speedMissing, _evalBeforeManual;
        private static float _nextFlush;

        private static readonly AccessTools.FieldRef<MovementContext, Player> PlayerRef =
            AccessTools.FieldRefAccess<MovementContext, Player>("_player");

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(MovementContext), nameof(MovementContext.ManualUpdate)),
                postfix: new HarmonyMethod(typeof(BotAnimLod), nameof(ManualUpdatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(MovementContext), nameof(MovementContext.AnimatorStatesLateUpdate)),
                prefix: new HarmonyMethod(typeof(BotAnimLod), nameof(LateUpdatePrefix)));
            // Unity's root-motion callback for every movement state (some states override ManualAnimatorMoveUpdate
            // without calling the base, so hook the one entry point they all share).
            harmony.Patch(AccessTools.Method(typeof(PlayerStateContainer), nameof(PlayerStateContainer.OnStateMove),
                    new[] { typeof(Animator), typeof(AnimatorStateInfo), typeof(int) }),
                prefix: new HarmonyMethod(typeof(BotAnimLod), nameof(OnStateMovePrefix)));
            // Fika's ObservedPlayer.LateUpdate replaces Player.LateUpdate without calling it, so MovementContext.
            // AnimatorStatesLateUpdate never runs for observed players: undo our changes from there instead.
            if (Fika.Present)
                harmony.Patch(AccessTools.Method(Fika.ObservedPlayerType, "LateUpdate"),
                    prefix: new HarmonyMethod(typeof(BotAnimLod), nameof(ObservedLateUpdatePrefix)));
        }

        private static bool Qualifies(Player p) =>
            p != null && p.HealthController.IsAlive && !p.IsVisible && p.Distance > OffscreenDistance &&
            (Fika.IsObservedBot(p) || (LocalBots && p.IsAI)) && !Fika.IsHeadless;

        /// <summary>Runs inside the animator update (root motion callback): the animator was evaluated this frame.</summary>
        private static void OnStateMovePrefix(Animator animator)
        {
            if (ByAnimator.Count == 0 || animator == null || !ByAnimator.TryGetValue(animator.GetInstanceID(), out var s)) return;
            if (s.LastEvalFrame == Time.frameCount) return; // several states/layers can call back in one frame
            s.LastEvalFrame = Time.frameCount;
            var d = animator.deltaPosition; // root motion the animator produced this evaluation (covers a catch-up)
            float rm = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            if (Validate && s.HasPos) RootM[Enabled ? 0 : 1, s.Bucket] += rm;
            if (!Enabled) return;
            if (s.LastManualFrame != Time.frameCount) _evalBeforeManual++;
            if (s.PendingCatchUp)
            {
                s.PendingCatchUp = false;
                if (Mathf.Approximately(animator.speed, s.ScaledSpeed)) _speedApplied++; else _speedMissing++;
                if (s.Bucket == 0) { _rootCatchUp += rm; _nCatchUp++; }
            }
            else if (s.Bucket == 0) { _rootNormal += rm; _nNormal++; }
        }

        /// <summary>Update phase, per player, before Unity's animation update: decide skip/advance for this frame.</summary>
        private static void ManualUpdatePostfix(MovementContext __instance, float deltaTime)
        {
            if (!Enabled && !Validate) return;
            var player = PlayerRef(__instance);
            if (player == null) return;
            var body = player.BodyAnimatorCommon;
            if (body == null) return;
            int id = player.GetInstanceID();
            bool q = Qualifies(player);
            States.TryGetValue(id, out var s);
            if (s != null) { s.LastManualFrame = Time.frameCount; s.PendingCatchUp = false; }

            if (q && (Validate || Enabled))
            {
                if (s == null) States[id] = s = Track(__instance);
                s.Bucket = Bucket(__instance);
            }
            if (q && Validate)
            {
                var pos = player.Position;
                if (s.HasPos)
                {
                    float dx = pos.x - s.LastPos.x, dz = pos.z - s.LastPos.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < 100f) // ignore teleports and spawns
                    {
                        if (Enabled) { _onMeters += Mathf.Sqrt(d2); _onSeconds += deltaTime; }
                        else { _offMeters += Mathf.Sqrt(d2); _offSeconds += deltaTime; }
                        int b = s.Bucket, e = Enabled ? 0 : 1;
                        DispM[e, b] += Mathf.Sqrt(d2);
                        DispS[e, b] += deltaTime;
                    }
                }
                s.LastPos = pos;
                s.HasPos = true;
            }
            else if (s != null) s.HasPos = false;

            if (!Enabled || !q)
            {
                // Not handled this frame (a skip from last frame was already undone in last frame's LateUpdate).
                if (s != null) { s.Culled = false; s.SinceEval = 0f; }
                return;
            }

            if (s == null) States[id] = s = Track(__instance);
            // Catch up only on time we skipped ourselves and Unity really skipped (no root-motion callback last frame).
            // Anything else (Unity evaluated anyway, or a gap we didn't cause) gets no scaling, so motion is never added.
            float catchUp = 0f;
            bool culledLast = s.Culled;
            if (culledLast)
            {
                if (s.LastEvalFrame == Time.frameCount - 1) _evaluatedAnyway++;
                else { _culled++; catchUp = s.SinceEval; }
            }
            s.Culled = false;

            // Stagger: half the bots skip on even frames, half on odd. Never skip twice in a row.
            if (!culledLast && ((Time.frameCount + id) & 1) == 0)
            {
                s.SavedMode = body.cullingMode;
                body.cullingMode = AnimatorCullingMode.CullCompletely;
                s.Culled = true;
                s.SinceEval = deltaTime; // this frame's time, to be caught up next frame
                return;
            }

            if (catchUp > 0f)
            {
                float factor = (catchUp + deltaTime) / deltaTime;
                s.BaseSpeed = body.speed;
                s.ScaledSpeed = s.BaseSpeed * factor;
                body.speed = s.ScaledSpeed;
                __instance.LastDeltaTime = catchUp + deltaTime;
                s.PendingCatchUp = true;
                _scaled++;
            }
        }

        private static State Track(MovementContext mc)
        {
            var st = new State();
            // EFT wraps the Unity Animator (AnimationSystem.UnityAnimatorWrapper); the fast animator has none, in which case nothing is mapped.
            if (mc.PlayerAnimator?.Animator is AnimationSystem.UnityAnimatorWrapper wrapper && wrapper.Animator != null)
                ByAnimator[wrapper.Animator.GetInstanceID()] = st;
            return st;
        }

        /// <summary>LateUpdate phase (after Unity's animation update): undo this frame's culling mode and speed scaling.</summary>
        private static void LateUpdatePrefix(MovementContext __instance)
        {
            if (States.Count == 0) return;
            Restore(PlayerRef(__instance));
        }

        private static void ObservedLateUpdatePrefix(Player __instance)
        {
            if (States.Count == 0) return;
            Restore(__instance);
        }

        private static void Restore(Player player)
        {
            if (player == null || !States.TryGetValue(player.GetInstanceID(), out var s)) return;
            var body = player.BodyAnimatorCommon;
            if (body == null) return;
            if (s.Culled && body.cullingMode == AnimatorCullingMode.CullCompletely) body.cullingMode = s.SavedMode;
            if (s.BaseSpeed >= 0f)
            {
                // Only restore if EFT hasn't set a new speed in between.
                if (Mathf.Approximately(body.speed, s.ScaledSpeed)) body.speed = s.BaseSpeed;
                s.BaseSpeed = -1f;
            }
        }

        /// <summary>Switched off, or leaving a raid: undo anything still applied.</summary>
        public static void ReleaseAll()
        {
            if (Singleton<GameWorld>.Instantiated)
            {
                foreach (var p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
                {
                    if (p == null || !States.TryGetValue(p.GetInstanceID(), out var s)) continue;
                    var body = p.BodyAnimatorCommon;
                    if (body == null) continue;
                    if (s.Culled && body.cullingMode == AnimatorCullingMode.CullCompletely) body.cullingMode = s.SavedMode;
                    if (s.BaseSpeed >= 0f && Mathf.Approximately(body.speed, s.ScaledSpeed)) body.speed = s.BaseSpeed;
                }
            }
            States.Clear();
            ByAnimator.Clear();
        }

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextFlush) return;
            _nextFlush = Time.realtimeSinceStartup + 1f;
            if (_onSeconds > 0) { Stats.Add("botAnimLod.on.meters", _onMeters); Stats.Add("botAnimLod.on.seconds", _onSeconds); }
            if (_offSeconds > 0) { Stats.Add("botAnimLod.off.meters", _offMeters); Stats.Add("botAnimLod.off.seconds", _offSeconds); }
            if (_culled > 0) Stats.Add("botAnimLod.skippedUpdates", _culled);
            if (_scaled > 0) Stats.Add("botAnimLod.catchUpUpdates", _scaled);
            if (_evaluatedAnyway > 0) Stats.Add("botAnimLod.evaluatedAnyway", _evaluatedAnyway);
            if (_nCatchUp > 0) { Stats.Add("botAnimLod.diag.runRootCatchUp", _rootCatchUp); Stats.Add("botAnimLod.diag.runCatchUps", _nCatchUp); }
            if (_nNormal > 0) { Stats.Add("botAnimLod.diag.runRootNormal", _rootNormal); Stats.Add("botAnimLod.diag.runNormals", _nNormal); }
            if (_speedApplied > 0) Stats.Add("botAnimLod.diag.speedApplied", _speedApplied);
            if (_speedMissing > 0) Stats.Add("botAnimLod.diag.speedMissing", _speedMissing);
            if (_evalBeforeManual > 0) Stats.Add("botAnimLod.diag.evalBeforeManualUpdate", _evalBeforeManual);
            _rootCatchUp = _rootNormal = 0;
            _nCatchUp = _nNormal = _speedApplied = _speedMissing = _evalBeforeManual = 0;
            _onMeters = _onSeconds = _offMeters = _offSeconds = 0;
            for (int e = 0; e < 2; e++)
            for (int b = 0; b < 3; b++)
            {
                if (DispS[e, b] <= 0) continue;
                string k = "botAnimLod." + (e == 0 ? "on." : "off.") + Buckets[b];
                Stats.Add(k + ".meters", DispM[e, b]);
                Stats.Add(k + ".rootMeters", RootM[e, b]);
                Stats.Add(k + ".seconds", DispS[e, b]);
                DispM[e, b] = DispS[e, b] = RootM[e, b] = 0;
            }
            _culled = _scaled = _evaluatedAnyway = 0;
        }
    }
}
