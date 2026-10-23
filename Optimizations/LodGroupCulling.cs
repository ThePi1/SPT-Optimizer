using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;
using Koenigz.PerfectCulling;
using Koenigz.PerfectCulling.EFT;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Switches off LOD groups whose renderers are all disabled, and back on before any of them is shown again.
    ///
    /// Unity evaluates every enabled LODGroup for every camera each frame, whether or not any of its renderers is
    /// enabled. On Streets (4.1.6) ~108,000 of ~137,000 enabled LOD groups had every renderer disabled: their normal
    /// renderers hidden by Perfect Culling, their shadow proxies by shadow proxy culling. Switching all of those off
    /// (profiler step EmptyLodGroupsOff, capture 20261023-000234) saved 0.54 ms of camera culling and 0.86 ms per frame,
    /// with no screenshot change. A disabled LODGroup hides all of its renderers, so a group may only stay off while
    /// nothing wants to show them.
    ///
    /// Only groups whose renderers are all controlled by Perfect Culling bake groups or by shadow proxy culling are
    /// taken, because those are the two things that turn them back on, and both are hooked:
    /// - Perfect Culling switches bake groups through PerfectCullingCrossSceneGroup.UpdateSwitchQueues (its IsEnabled
    ///   setter is AggressiveInlining, so the caller is patched). The prefix switches on the LOD groups of every bake
    ///   group about to be activated, before its renderers come back; the postfix switches off groups left dark by the
    ///   deactivations.
    /// - Shadow proxy culling calls <see cref="BeforeEnable"/> and <see cref="AfterDisable"/> around its own toggles.
    /// A slow sweep (100 groups per frame, a full pass every ~10 s) re-enables any group of ours with an enabled renderer anyway, in
    /// case something else showed one, and forgets groups the game switched back on itself (EFT's autocull cells).
    /// </summary>
    internal static class LodGroupCulling
    {
        private static bool _enabled;
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value) Release();
                else if (_scanned) _fill = 0; // groups emptied while off are switched off over the next frames
            }
        }

        // ~117k groups / 100 = one pass every ~10 s. It has never had to rescue a group in testing; at 400 per frame its
        // Unity null checks and enabled getters cost ~0.2 ms of Update.
        private const int SweepPerFrame = 100;
        private const int FillPerFrame = 8000; // switching ~100k groups at once is a 100-400 ms hitch

        private struct Group
        {
            public LODGroup Lod;
            public Renderer[] Renderers;
            public bool OffByUs;
        }

        private static ManualLogSource _log;
        private static Group[] _groups = new Group[0];
        private static readonly Dictionary<int, int> GroupOfRenderer = new Dictionary<int, int>();
        private static readonly Dictionary<PerfectCullingBakeGroup, int[]> GroupsOfBake = new Dictionary<PerfectCullingBakeGroup, int[]>();
        private static readonly List<PerfectCullingBakeGroup> Deactivating = new List<PerfectCullingBakeGroup>();
        private static bool _scanned;
        private static int _off, _sweep;
        private static int _fill = -1; // next group of the switch-off pass after a scan or a switch-on; -1 = none
        private static long _onByCulling, _offByCulling, _rescued, _forgotten;
        private static float _nextFlush;

        private static readonly AccessTools.FieldRef<PerfectCullingCrossSceneGroup, FastArray<ushort>> QueueActivate =
            AccessTools.FieldRefAccess<PerfectCullingCrossSceneGroup, FastArray<ushort>>("_queueActivate");
        private static readonly AccessTools.FieldRef<PerfectCullingCrossSceneGroup, FastArray<ushort>> QueueDeactivate =
            AccessTools.FieldRefAccess<PerfectCullingCrossSceneGroup, FastArray<ushort>>("_queueDeactivate");

        public static int Disabled => _off;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            harmony.Patch(AccessTools.Method(typeof(PerfectCullingCrossSceneGroup), nameof(PerfectCullingCrossSceneGroup.UpdateSwitchQueues)),
                prefix: new HarmonyMethod(typeof(LodGroupCulling), nameof(SwitchQueuesPrefix)),
                postfix: new HarmonyMethod(typeof(LodGroupCulling), nameof(SwitchQueuesPostfix)));
        }

        /// <summary>Once per raid, after shadow proxy culling has scanned (GetLODs allocates; the GC is off in raid).</summary>
        public static void Scan(IEnumerable<Renderer> shadowProxies)
        {
            var sw = Stopwatch.StartNew();
            var controlled = new HashSet<int>();
            foreach (var r in shadowProxies) if (r != null) controlled.Add(r.GetInstanceID());
            var bakes = new List<PerfectCullingBakeGroup>();
            foreach (var cross in PerfectCullingCrossSceneGroup.AllCrossGroups)
            {
                if (cross == null || cross.bakeGroups == null) continue;
                foreach (var bake in cross.bakeGroups)
                {
                    if (bake == null) continue;
                    bakes.Add(bake);
                    foreach (var r in BakeRenderers(bake)) controlled.Add(r.GetInstanceID());
                }
            }

            GroupOfRenderer.Clear();
            var groups = new List<Group>();
            var members = new List<Renderer>();
            int all = 0, notControlled = 0;
            foreach (var lod in Object.FindObjectsOfType<LODGroup>())
            {
                if (!lod.enabled) continue;
                all++;
                members.Clear();
                bool ok = true;
                foreach (var level in lod.GetLODs())
                {
                    foreach (var r in level.renderers)
                    {
                        if (r == null || members.Contains(r)) continue;
                        int id = r.GetInstanceID();
                        // Something else controls it, or it already belongs to another group: leave this group alone.
                        if (!controlled.Contains(id) || GroupOfRenderer.ContainsKey(id)) { ok = false; break; }
                        members.Add(r);
                    }
                    if (!ok) break;
                }
                if (!ok) { notControlled++; continue; }
                if (members.Count == 0) continue;
                foreach (var r in members) GroupOfRenderer[r.GetInstanceID()] = groups.Count;
                groups.Add(new Group { Lod = lod, Renderers = members.ToArray() });
            }
            _groups = groups.ToArray();

            GroupsOfBake.Clear();
            var idx = new List<int>();
            foreach (var bake in bakes)
            {
                idx.Clear();
                foreach (var r in BakeRenderers(bake))
                    if (GroupOfRenderer.TryGetValue(r.GetInstanceID(), out int g) && !idx.Contains(g)) idx.Add(g);
                if (idx.Count > 0) GroupsOfBake[bake] = idx.ToArray();
            }

            _off = 0;
            _fill = 0;
            _scanned = true;
            _log.LogInfo($"LOD group culling: {_groups.Length} of {all} enabled LOD groups are controlled by Perfect Culling or shadow proxy culling ({notControlled} are not); {GroupsOfBake.Count} bake groups mapped (scan {sw.ElapsedMilliseconds} ms)");
        }

        private static IEnumerable<Renderer> BakeRenderers(PerfectCullingBakeGroup bake)
        {
            if (bake.renderers != null)
                foreach (var r in bake.renderers) if (r != null) yield return r;
            var data = bake.RuntimeGroupData;
            if (data != null)
                for (int i = 0; i < bake.runtimeGroupDataSize && i < data.Length; i++)
                    if (data[i].Renderer != null) yield return data[i].Renderer;
        }

        private static bool _hookFailed;

        private static void SwitchQueuesPrefix(PerfectCullingCrossSceneGroup __instance)
        {
            Deactivating.Clear();
            if (!_enabled || !_scanned || _hookFailed) return;
            // Never let a failure here stop Perfect Culling's own switching: switch the feature off instead.
            try { BeforeSwitch(__instance); }
            catch (System.Exception ex)
            {
                _hookFailed = true;
                _log.LogError("LOD group culling: Perfect Culling hook failed, switching LOD group culling off: " + ex);
                Enabled = false;
            }
        }

        private static void BeforeSwitch(PerfectCullingCrossSceneGroup __instance)
        {
            var bakes = __instance.bakeGroups;
            int b = PerfectCullingSettings.Instance.numActivationsPerVolume / 2;
            // FastArray.Dequeue pops from the end: these are exactly the entries UpdateSwitchQueues will take.
            var qa = QueueActivate(__instance);
            for (int k = 0, n = Mathf.Min(qa.Count, b); k < n; k++)
            {
                int id = qa.Buffer[qa.Count - 1 - k];
                if (id < bakes.Length && bakes[id] != null && GroupsOfBake.TryGetValue(bakes[id], out var gs))
                    foreach (int g in gs) if (Enable(g)) _onByCulling++;
            }
            var qd = QueueDeactivate(__instance);
            for (int k = 0, n = Mathf.Min(qd.Count, b); k < n; k++)
            {
                int id = qd.Buffer[qd.Count - 1 - k];
                if (id < bakes.Length && bakes[id] != null) Deactivating.Add(bakes[id]);
            }
        }

        private static void SwitchQueuesPostfix()
        {
            if (Deactivating.Count == 0) return;
            foreach (var bake in Deactivating)
                if (GroupsOfBake.TryGetValue(bake, out var gs))
                    foreach (int g in gs) if (TryDisable(g)) _offByCulling++;
            Deactivating.Clear();
        }

        /// <summary>Shadow proxy culling is about to show this renderer: its LOD group must be on first.</summary>
        public static void BeforeEnable(Renderer r)
        {
            if (_scanned && GroupOfRenderer.TryGetValue(r.GetInstanceID(), out int g)) Enable(g);
        }

        /// <summary>Shadow proxy culling has hidden this renderer: its LOD group may now be empty.</summary>
        public static void AfterDisable(Renderer r)
        {
            if (_enabled && _scanned && GroupOfRenderer.TryGetValue(r.GetInstanceID(), out int g)) TryDisable(g);
        }

        private static bool TryDisable(int i)
        {
            ref var g = ref _groups[i];
            if (!_enabled || g.OffByUs || g.Lod == null || !g.Lod.enabled) return false;
            foreach (var r in g.Renderers) if (r != null && r.enabled) return false;
            g.Lod.enabled = false;
            g.OffByUs = true;
            _off++;
            return true;
        }

        private static bool Enable(int i)
        {
            ref var g = ref _groups[i];
            if (!g.OffByUs) return false;
            if (g.Lod != null && !g.Lod.enabled) g.Lod.enabled = true;
            g.OffByUs = false;
            _off--;
            return true;
        }

        public static void Tick()
        {
            Flush();
            if (!_enabled || !_scanned || _groups.Length == 0) return;
            if (_fill >= 0)
            {
                // Switch off every group that is empty now, a slice per frame.
                int end = Mathf.Min(_fill + FillPerFrame, _groups.Length);
                for (; _fill < end; _fill++) TryDisable(_fill);
                if (_fill >= _groups.Length)
                {
                    _fill = -1;
                    _log.LogInfo($"LOD group culling: {_off} of {_groups.Length} groups switched off");
                }
            }
            // Safety net: a group of ours with a renderer someone else showed goes back on; a group the game switched
            // back on itself is no longer ours.
            for (int n = 0; n < SweepPerFrame; n++)
            {
                if (++_sweep >= _groups.Length) _sweep = 0;
                ref var g = ref _groups[_sweep];
                if (!g.OffByUs) continue;
                if (g.Lod == null || g.Lod.enabled) { g.OffByUs = false; _off--; _forgotten++; continue; }
                foreach (var r in g.Renderers)
                {
                    if (r == null || !r.enabled) continue;
                    Enable(_sweep);
                    _rescued++;
                    break;
                }
            }
        }

        /// <summary>Switched off: every group we switched off goes back on.</summary>
        public static void Release()
        {
            for (int i = 0; i < _groups.Length; i++) Enable(i);
        }

        /// <summary>Leaving a raid: the scene is being unloaded, nothing to restore.</summary>
        public static void Clear()
        {
            _groups = new Group[0];
            GroupOfRenderer.Clear();
            GroupsOfBake.Clear();
            Deactivating.Clear();
            _scanned = false;
            _off = _sweep = 0;
            _fill = -1;
        }

        public static bool NeedsScan => _enabled && !_scanned;

        private static void Flush()
        {
            if (Time.realtimeSinceStartup < _nextFlush) return;
            _nextFlush = Time.realtimeSinceStartup + 1f;
            if (_scanned) Stats.Max("lodGroupCulling.off", _off);
            if (_onByCulling > 0) Stats.Add("lodGroupCulling.onForPerfectCulling", _onByCulling);
            if (_offByCulling > 0) Stats.Add("lodGroupCulling.offAfterPerfectCulling", _offByCulling);
            if (_rescued > 0) Stats.Add("lodGroupCulling.rescuedBySweep", _rescued);
            if (_forgotten > 0) Stats.Add("lodGroupCulling.switchedOnByGame", _forgotten);
            _onByCulling = _offByCulling = _rescued = _forgotten = 0;
        }
    }
}
