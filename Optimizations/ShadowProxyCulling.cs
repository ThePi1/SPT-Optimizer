using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Disables ShadowsOnly renderers (shadow proxy meshes) that are far outside the shadow distance, and
    /// re-enables them as the camera approaches.
    ///
    /// Streets has ~173k enabled ShadowsOnly renderers, ~160k of them more than 100 m from the player, while
    /// the sun's shadow distance is 40 m. Unity's per-camera culling and shadow-caster culling still walk all of
    /// them every frame: measured ~0.54 ms of culling plus ~0.40 ms of shadow setup on the main thread, with no
    /// change in the shadow casters actually drawn. EFT's DistantShadow system draws far shadows from its own
    /// command buffer (DrawMesh with captured matrices), so it does not need these renderers enabled.
    ///
    /// Static proxies only: renderers are bucketed into a 2D grid once per raid. Each update, whole cells are
    /// switched on or off by their distance range; only cells straddling the threshold are checked per renderer.
    /// Distance is measured from the closest point of a renderer's bounding sphere, so a large proxy is kept
    /// as soon as any part of it comes within range.
    /// </summary>
    internal sealed class ShadowProxyCulling
    {
        public bool Enabled = true;
        public float Distance = 100f;
        public float ShadowDistanceMultiplier = 2.5f;
        public string[] Layers = { "Default", "CullingMask", "HighPolyCollider" };

        private const float CellSize = 32f;
        private const float MoveThreshold = 4f;
        private const float ScanDelaySeconds = 5f;

        private struct Proxy
        {
            public Renderer Renderer;
            public Vector3 Center;
            public float Radius;
            public bool DisabledByUs;
        }

        private sealed class Cell
        {
            public Vector2 Min;
            public Vector2 Max;
            public float MaxRadius;
            public readonly List<int> Items = new List<int>();
            public int State; // 1 = all enabled, -1 = all disabled by us, 0 = mixed
        }

        private readonly ManualLogSource _log;
        private Proxy[] _proxies = new Proxy[0];
        private readonly List<Cell> _cells = new List<Cell>();
        private bool _scanned;
        private float _raidSeenAt = -1f;
        private Vector3 _lastEvalPos = new Vector3(float.MaxValue, 0, 0);
        private float _lastEvalDistance;
        private int _disabledCount;

        public int DisabledCount => _disabledCount;
        public int ProxyCount => _proxies.Length;

        public ShadowProxyCulling(ManualLogSource log)
        {
            _log = log;
        }

        public float EffectiveDistance => Mathf.Max(Distance, QualitySettings.shadowDistance * ShadowDistanceMultiplier);

        public void Tick()
        {
            bool inRaid = Raid.InRaid(out _);
            if (!inRaid)
            {
                if (_scanned || _raidSeenAt >= 0f)
                {
                    // scene is being unloaded; nothing to restore
                    Clear(restore: false);
                    LodGroupCulling.Clear();
                }
                _raidSeenAt = -1f;
                return;
            }
            if (_raidSeenAt < 0f) _raidSeenAt = Time.realtimeSinceStartup;

            if (!Enabled)
            {
                if (_disabledCount > 0) RestoreAll();
                return;
            }

            if (!_scanned)
            {
                // Let the level finish activating before taking the snapshot.
                if (Time.realtimeSinceStartup - _raidSeenAt < ScanDelaySeconds) return;
                Scan();
            }
            if (LodGroupCulling.NeedsScan) LodGroupCulling.Scan(ProxyRenderers());

            var cam = Camera.main;
            if (cam == null) return;
            Vector3 pos = cam.transform.position;
            float dist = EffectiveDistance;
            if ((pos - _lastEvalPos).sqrMagnitude < MoveThreshold * MoveThreshold && Mathf.Approximately(dist, _lastEvalDistance)) return;
            _lastEvalPos = pos;
            _lastEvalDistance = dist;
            Evaluate(pos, dist);
        }

        private void Scan()
        {
            var sw = Stopwatch.StartNew();
            int layerMask = 0;
            foreach (var name in Layers)
            {
                int l = LayerMask.NameToLayer(name.Trim());
                if (l >= 0) layerMask |= 1 << l;
            }

            var list = new List<Proxy>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || r.shadowCastingMode != ShadowCastingMode.ShadowsOnly) continue;
                if ((layerMask & (1 << r.gameObject.layer)) == 0) continue;
                if (r is SkinnedMeshRenderer || r.GetComponentInParent<Rigidbody>() != null) continue; // only static geometry
                var b = r.bounds;
                list.Add(new Proxy { Renderer = r, Center = b.center, Radius = b.extents.magnitude });
            }
            _proxies = list.ToArray();

            var byCell = new Dictionary<long, Cell>();
            for (int i = 0; i < _proxies.Length; i++)
            {
                int cx = Mathf.FloorToInt(_proxies[i].Center.x / CellSize);
                int cz = Mathf.FloorToInt(_proxies[i].Center.z / CellSize);
                long key = ((long)cx << 32) ^ (uint)cz;
                if (!byCell.TryGetValue(key, out var cell))
                {
                    cell = new Cell { Min = new Vector2(cx * CellSize, cz * CellSize), Max = new Vector2((cx + 1) * CellSize, (cz + 1) * CellSize), State = 1 };
                    byCell[key] = cell;
                }
                cell.Items.Add(i);
                cell.MaxRadius = Mathf.Max(cell.MaxRadius, _proxies[i].Radius);
            }
            _cells.Clear();
            _cells.AddRange(byCell.Values);
            _scanned = true;
            _log.LogInfo($"Shadow proxy culling: {_proxies.Length} static ShadowsOnly renderers in {_cells.Count} cells (scan {sw.ElapsedMilliseconds} ms), threshold {EffectiveDistance:0} m");
        }

        private IEnumerable<Renderer> ProxyRenderers()
        {
            foreach (var p in _proxies) yield return p.Renderer;
        }

        private void Evaluate(Vector3 pos, float dist)
        {
            var sw = Stopwatch.StartNew();
            int changed = 0;
            var p2 = new Vector2(pos.x, pos.z);
            foreach (var cell in _cells)
            {
                // Horizontal distance range from the camera to anything in the cell (bounding spheres can overhang the cell).
                float nearest = DistanceToRect(p2, cell.Min, cell.Max) - cell.MaxRadius;
                float farthest = FarthestInRect(p2, cell.Min, cell.Max) + cell.MaxRadius;
                if (nearest > dist)
                {
                    if (cell.State != -1) { changed += SetCell(cell, false); cell.State = -1; }
                }
                else if (farthest < dist)
                {
                    if (cell.State != 1) { changed += SetCell(cell, true); cell.State = 1; }
                }
                else
                {
                    // Straddles the threshold: decide per renderer, using full 3D distance to the bounding sphere.
                    foreach (int i in cell.Items)
                    {
                        bool keep = Vector3.Distance(pos, _proxies[i].Center) - _proxies[i].Radius <= dist;
                        changed += Set(i, keep);
                    }
                    cell.State = 0;
                }
            }
            if (changed > 200) _log.LogDebug($"Shadow proxy culling: {changed} toggled in {sw.Elapsed.TotalMilliseconds:0.0} ms, {_disabledCount} disabled");
        }

        private int SetCell(Cell cell, bool enabled)
        {
            int n = 0;
            foreach (int i in cell.Items) n += Set(i, enabled);
            return n;
        }

        private int Set(int i, bool enabled)
        {
            ref var p = ref _proxies[i];
            if (p.Renderer == null) return 0;
            if (enabled)
            {
                if (!p.DisabledByUs) return 0;
                LodGroupCulling.BeforeEnable(p.Renderer); // its LOD group, if switched off, must be on first
                p.Renderer.enabled = true;
                p.DisabledByUs = false;
                _disabledCount--;
                return 1;
            }
            if (p.DisabledByUs || !p.Renderer.enabled) return 0; // never touch something the game disabled itself
            p.Renderer.enabled = false;
            p.DisabledByUs = true;
            _disabledCount++;
            LodGroupCulling.AfterDisable(p.Renderer);
            return 1;
        }

        public void RestoreAll()
        {
            for (int i = 0; i < _proxies.Length; i++) Set(i, true);
            foreach (var c in _cells) c.State = 1;
            _lastEvalPos = new Vector3(float.MaxValue, 0, 0);
            _log.LogInfo("Shadow proxy culling: all proxies restored");
        }

        private void Clear(bool restore)
        {
            if (restore) RestoreAll();
            _proxies = new Proxy[0];
            _cells.Clear();
            _scanned = false;
            _disabledCount = 0;
            _lastEvalPos = new Vector3(float.MaxValue, 0, 0);
        }

        private static float DistanceToRect(Vector2 p, Vector2 min, Vector2 max)
        {
            // Two-argument Max only: the params overload allocates, and the GC is off in raid.
            float dx = Mathf.Max(Mathf.Max(min.x - p.x, p.x - max.x), 0f);
            float dz = Mathf.Max(Mathf.Max(min.y - p.y, p.y - max.y), 0f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float FarthestInRect(Vector2 p, Vector2 min, Vector2 max)
        {
            float dx = Mathf.Max(Mathf.Abs(p.x - min.x), Mathf.Abs(p.x - max.x));
            float dz = Mathf.Max(Mathf.Abs(p.y - min.y), Mathf.Abs(p.y - max.y));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
