using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Runs the character controllers' neighbour queries as one multi-threaded batch (on by default since 2026-10-19:
    /// validated against the original query, ~0.2 ms of LateUpdate on Streets with ~22 bots).
    ///
    /// Every player's move goes through EFT's SimpleCharacterController.Move, which for each movement sub-step calls
    /// method_7 → Physics.OverlapSphereNonAlloc (one capsule, ~14 µs per query on Streets, ~1.8 ms/frame total)
    /// on the main thread, one player after another. All players' motions for the frame are known before the first
    /// one is applied (MovementState schedules them in Update; MovementContext.AnimatorStatesLateUpdate applies them
    /// in LateUpdate). So at the first AnimatorStatesLateUpdate of a frame this issues one OverlapSphereCommand per
    /// player collider, covering its whole move path (start → start+motion, plus a margin), as a single batch on the
    /// job worker threads. method_7 then uses that result when its own query sphere lies inside the batched one.
    ///
    /// The batched sphere is a superset of every sub-step sphere it serves. Extra colliders are harmless: the
    /// controller runs Physics.ComputePenetration on each neighbour and ignores the ones that don't overlap. Result
    /// order may differ, which PhysX doesn't guarantee anyway. Anything not covered (sphere outside the batched one,
    /// a truncated result, a move not seen by the pre-pass) falls back to the original query.
    /// </summary>
    internal sealed class BatchedOverlaps : IDisposable
    {
        public static BatchedOverlaps Instance;

        public bool Enabled;
        public float Margin = 0.15f;
        public bool Validate;

        private const int MaxHits = 64; // = SimpleCharacterController.GClass755 capacity

        private static readonly AccessTools.FieldRef<SimpleCharacterController, Transform> MovementTransform =
            AccessTools.FieldRefAccess<SimpleCharacterController, Transform>("_movementTransform");
        private static readonly AccessTools.FieldRef<SimpleCharacterController, Collider[]> Colliders =
            AccessTools.FieldRefAccess<SimpleCharacterController, Collider[]>("_colliders");
        private static readonly AccessTools.FieldRef<SimpleCharacterController, LayerMask> CollisionMask =
            AccessTools.FieldRefAccess<SimpleCharacterController, LayerMask>("_collisionMask");
        private static readonly AccessTools.FieldRef<SimpleCharacterController, float> CastHalo =
            AccessTools.FieldRefAccess<SimpleCharacterController, float>("_castHalo");

        private struct Entry
        {
            public Vector3 Center;
            public float Radius;
            public bool Resolved;
            public bool Truncated;
            public int Count;
        }

        private readonly ManualLogSource _log;
        private readonly Dictionary<int, int> _entryByCollider = new Dictionary<int, int>(64);
        private Entry[] _entries = new Entry[64];
        private Collider[][] _resolved = new Collider[0][];
        private NativeArray<OverlapSphereCommand> _commands;
        private NativeArray<ColliderHit> _hits;
        private int _capacity;
        private int _frame = -1;

        // Stats since the last report
        private long _frames, _batched, _served, _missNoEntry, _missOutside, _missTruncated, _prepassTicks;
        private long _offCalls, _validated, _validateMissed, _validateExtra;
        private int _reportStartFrame = -1, _mismatchLogs;
        // Diagnostics: the largest single move seen this period (queries per Move grow with motion / 0.3 m).
        private float _maxMag;
        private string _maxMagWho;
        private long _bigMoves, _moveCalls;
        private float _nextReport;
        private readonly Collider[] _scratch = new Collider[MaxHits];

        public BatchedOverlaps(ManualLogSource log)
        {
            _log = log;
            Instance = this;
        }

        public void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MovementContext), nameof(MovementContext.AnimatorStatesLateUpdate)),
                prefix: new HarmonyMethod(typeof(BatchedOverlaps), nameof(AnimatorStatesLateUpdatePrefix)));
            harmony.Patch(AccessTools.Method(typeof(SimpleCharacterController), nameof(SimpleCharacterController.method_7)),
                prefix: new HarmonyMethod(typeof(BatchedOverlaps), nameof(Method7Prefix)));
            harmony.Patch(AccessTools.Method(typeof(SimpleCharacterController), nameof(SimpleCharacterController.Move)),
                prefix: new HarmonyMethod(typeof(BatchedOverlaps), nameof(MovePrefix)));
        }

        private static void MovePrefix()
        {
            var self = Instance;
            if (self != null && (self.Enabled || self.Validate)) self._moveCalls++;
        }

        // ---- hooks ----

        private static void AnimatorStatesLateUpdatePrefix()
        {
            var self = Instance;
            if (self == null || !self.Enabled) return;
            int frame = Time.frameCount;
            if (frame == self._frame) return;
            self._frame = frame;
            self.PrePass();
        }

        private static bool Method7Prefix(SimpleCharacterController __instance, Collider collider, SimpleCharacterController.GClass755 neighboursColliders, Vector3 desiredPoint)
        {
            var self = Instance;
            if (self == null) return true;
            if (!self.Enabled)
            {
                if (self.Validate) self._offCalls++;
                return true;
            }
            return !self.TryServe(__instance, collider, neighboursColliders, desiredPoint);
        }

        // ---- work ----

        private void PrePass()
        {
            long t0 = Stopwatch.GetTimestamp();
            _entryByCollider.Clear();
            _frames++;
            if (!Singleton<GameWorld>.Instantiated) return;
            var players = Singleton<GameWorld>.Instance.AllAlivePlayersList;

            int n = 0;
            for (int p = 0; p < players.Count; p++)
            {
                var player = players[p];
                if (player == null) continue;
                var mc = player.MovementContext;
                if (mc == null || !mc.ScheduleDirectApplyMotion) continue;
                var cc = player.CharacterController as SimpleCharacterController;
                if (cc == null || cc.IsMoveIgnored) continue;

                // Same motion DirectApplyMotion hands to Move (Flash is a no-op in this build).
                Vector3 motion = mc.ScheduledMotion + mc.PlatformMotion;
                float mag = motion.magnitude;
                if (mag <= 0f) continue;
                if (mag > 1f) _bigMoves++;
                if (RunawayMoveGuard.IsRunaway(motion, Time.deltaTime)) continue; // Move will be skipped
                if (mag > _maxMag)
                {
                    _maxMag = mag;
                    var pos = player.Position;
                    _maxMagWho = $"{player.Profile?.Info?.Settings?.Role.ToString() ?? "?"} {player.Profile?.Nickname} at ({pos.x:0},{pos.y:0},{pos.z:0}), " +
                                 $"state {mc.CurrentState?.Name}, grounded {mc.IsGrounded}, velocity {mc.Velocity.magnitude:0.0} m/s";
                }

                Vector3 start = MovementTransform(cc).position;
                var colliders = Colliders(cc);
                int mask = CollisionMask(cc).value;
                float halo = CastHalo(cc);
                for (int c = 0; c < colliders.Length; c++)
                {
                    var col = colliders[c];
                    if (col == null) continue;
                    Bounds b = col.bounds;
                    Vector3 ext = b.extents;
                    float r = Mathf.Max(Mathf.Max(ext.x, ext.y), ext.z) + halo;
                    Vector3 center = b.center + motion * 0.5f;  // b.center - start is the per-step offset method_7 adds
                    float radius = r + mag * 0.5f + Margin;
                    EnsureCapacity(n + 1);
                    _commands[n] = new OverlapSphereCommand(center, radius, new QueryParameters(mask, false, QueryTriggerInteraction.Ignore, false));
                    _entries[n] = new Entry { Center = center, Radius = radius };
                    _entryByCollider[col.GetInstanceID()] = n;
                    n++;
                }
            }

            if (n > 0)
            {
                var handle = OverlapSphereCommand.ScheduleBatch(_commands.GetSubArray(0, n), _hits.GetSubArray(0, n * MaxHits), 1, MaxHits);
                handle.Complete();
                _batched += n;
            }
            _prepassTicks += Stopwatch.GetTimestamp() - t0;
        }

        private bool TryServe(SimpleCharacterController cc, Collider collider, SimpleCharacterController.GClass755 neighbours, Vector3 desiredPoint)
        {
            if (_frame != Time.frameCount || !_entryByCollider.TryGetValue(collider.GetInstanceID(), out int idx))
            {
                _missNoEntry++;
                return false;
            }

            // Exactly the query method_7 would run.
            Bounds b = collider.bounds;
            Vector3 position = desiredPoint + (b.center - collider.transform.position);
            Vector3 ext = b.extents;
            float radius = Mathf.Max(Mathf.Max(ext.x, ext.y), ext.z) + CastHalo(cc);

            ref Entry e = ref _entries[idx];
            if ((position - e.Center).magnitude + radius > e.Radius)
            {
                _missOutside++;
                return false;
            }

            if (!e.Resolved)
            {
                e.Resolved = true;
                var buffer = _resolved[idx];
                int baseIndex = idx * MaxHits;
                int count = 0;
                for (int j = 0; j < MaxHits; j++)
                {
                    var hit = _hits[baseIndex + j];
                    if (hit.instanceID == 0) break;
                    var c = hit.collider;
                    if (c != null) buffer[count++] = c;
                }
                e.Count = count;
                // Cut off at capacity, or empty (a moving character is practically always touching something):
                // let the original query decide in both cases.
                e.Truncated = count >= MaxHits || count == 0;
            }
            if (e.Truncated)
            {
                _missTruncated++;
                return false;
            }

            Array.Copy(_resolved[idx], neighbours.colliders, e.Count);
            neighbours.count = e.Count;
            _served++;
            if (Validate) Check(cc, collider, position, radius, neighbours);
            return true;
        }

        /// <summary>Validation: run the original query and make sure the served set contains every collider it finds.</summary>
        private void Check(SimpleCharacterController cc, Collider self, Vector3 position, float radius, SimpleCharacterController.GClass755 served)
        {
            int n = Physics.OverlapSphereNonAlloc(position, radius, _scratch, CollisionMask(cc), QueryTriggerInteraction.Ignore);
            _validated++;
            _validateExtra += Math.Max(0, served.count - n);
            for (int i = 0; i < n; i++)
            {
                var want = _scratch[i];
                bool found = false;
                for (int j = 0; j < served.count; j++)
                    if (ReferenceEquals(served.colliders[j], want)) { found = true; break; }
                if (found) continue;
                _validateMissed++;
                if (_mismatchLogs++ < 20)
                    _log.LogWarning($"Batched overlaps MISMATCH: {self.name} missed {want.name} (layer {LayerMask.LayerToName(want.gameObject.layer)}, {want.GetType().Name}) at {position}, r {radius:0.000}");
            }
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _capacity) return;
            int cap = Math.Max(64, _capacity * 2);
            while (cap < needed) cap *= 2;
            // Keep the commands already written by the running pre-pass.
            var commands = new NativeArray<OverlapSphereCommand>(cap, Allocator.Persistent);
            if (_commands.IsCreated)
            {
                NativeArray<OverlapSphereCommand>.Copy(_commands, commands, _capacity);
                _commands.Dispose();
            }
            _commands = commands;
            if (_hits.IsCreated) _hits.Dispose();
            _hits = new NativeArray<ColliderHit>(cap * MaxHits, Allocator.Persistent);
            Array.Resize(ref _entries, cap);
            var resolved = new Collider[cap][];
            for (int i = 0; i < cap; i++) resolved[i] = i < _resolved.Length ? _resolved[i] : new Collider[MaxHits];
            _resolved = resolved;
            _capacity = cap;
        }
        /// <summary>Called from the plugin's Update: periodic stats while enabled.</summary>
        public void Tick()
        {
            if (!Enabled && !Validate) { _reportStartFrame = -1; return; }
            if (_reportStartFrame < 0) { ResetStats(); return; }
            if (Time.realtimeSinceStartup < _nextReport) return;
            if (!Enabled)
            {
                // Off, validating: the original query rate for comparison (only meaningful in a raid).
                if (_offCalls > 0)
                {
                    double frames = Math.Max(1, Time.frameCount - _reportStartFrame);
                    _log.LogInfo($"Batched overlaps OFF: {_offCalls / frames:0.0} controller queries per frame, {_moveCalls / frames:0.0} Move calls per frame");
                }
                ResetStats();
                return;
            }
            if (_frames == 0) { ResetStats(); return; }
            double f = _frames;
            long totalCalls = _served + _missNoEntry + _missOutside + _missTruncated;
            if (Validate)
                _log.LogInfo($"Batched overlaps validation: {_validated} served queries checked, {_validateMissed} colliders missed (must be 0), " +
                             $"{(_validated > 0 ? _validateExtra / (double)_validated : 0):0.0} extra colliders per query");
            _log.LogInfo($"Batched overlaps: per frame {_batched / f:0.0} batched queries, {totalCalls / f:0.0} controller queries " +
                         $"({_served / f:0.0} served from batch, {_missOutside / f:0.00} outside, {_missTruncated / f:0.00} truncated/empty, {_missNoEntry / f:0.00} no pre-pass entry); " +
                         $"pre-pass {_prepassTicks * 1000.0 / Stopwatch.Frequency / f:0.000} ms/frame; " +
                         $"largest move {_maxMag:0.00} m/frame ({_maxMagWho}), moves > 1 m: {_bigMoves / f:0.00}/frame, Move calls {_moveCalls / f:0.0}/frame");
            ResetStats();
        }

        private void ResetStats()
        {
            _frames = _batched = _served = _missNoEntry = _missOutside = _missTruncated = _prepassTicks = 0;
            _offCalls = _validated = _validateMissed = _validateExtra = _bigMoves = _moveCalls = 0;
            _maxMag = 0f;
            _maxMagWho = null;
            _reportStartFrame = Time.frameCount;
            _nextReport = Time.realtimeSinceStartup + 15f;
        }

        public void Dispose()
        {
            if (_commands.IsCreated) _commands.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
        }
    }
}
