using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using EFT.Ballistics;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// Optional trade-offs, both off by default: stop rendering decorative clutter (litter, packaging, debris piles,
    /// spent casings, puddles, glass shards, mesh decals). Same idea as the Simple-Declutter / Tyrian-DeClutterer mods,
    /// reimplemented here with stricter safety rules:
    ///
    /// - Small objects: visual-only clutter (the rules below). Nothing gameplay touches changes.
    /// - Medium objects: keyword matches that are visual-only apart from a collider (or EFT's BallisticCollider script),
    ///   such as cardboard boxes, garbage bags and bricks. Only their meshes are hidden; the colliders stay, so they
    ///   become invisible obstacles that still stop bullets, players and grenades, the same for everyone in Fika.
    ///
    /// Measured on Streets (2026-10-20, headless client, littered alley): small −50 draw calls, medium on top of it
    /// −166 draw calls and −0.25 ms render thread; no measurable frame-time change either way (main-thread bound).
    ///
    /// - Candidates are LODGroup roots whose name matches a category keyword and doesn't match a protected keyword.
    /// - EFT's StaticDeferredDecals are never touched: disabling one makes StaticDeferredDecalRenderer.method_9
    ///   recompute the bounds of every other decal in its group, allocating a mesh vertex copy per decal, so
    ///   disabling ~5,500 of them froze the game for 14 s and allocated ~3 GB (2026-10-20). Mesh decals
    ///   (dust_decal, decal_dirt, stains...) are ordinary renderers and are covered by the Decals category.
    /// - Rejected if anything in the hierarchy has a Rigidbody, Light, AudioSource, Animator, ParticleSystem,
    ///   SkinnedMeshRenderer, or any MonoBehaviour other than BallisticCollider. A Collider or BallisticCollider makes
    ///   it a medium (solid) candidate instead of a small one.
    /// - Rejected if the combined renderer bounds are taller than 2 m.
    /// - Hidden with Renderer.forceRenderingOff, not SetActive(false). EFT's PerfectCulling toggles GameObjects,
    ///   LODGroups and Renderer.enabled on its own and would undo (or fight) those; forceRenderingOff is a separate
    ///   flag that none of them touch, and it never fires OnDisable on game scripts.
    ///
    /// The scan runs once per raid, time-sliced across frames, the first time either option (or the report) is on in
    /// that raid. Switching the options afterwards doesn't rescan.
    /// </summary>
    internal static class Declutter
    {
        [Flags]
        public enum Category
        {
            None = 0, Litter = 1, Packaging = 2, Debris = 4, Casings = 8, Puddles = 16, Shards = 32, Decals = 64, Decor = 128,
        }

        // Keyword -> category, matched case-insensitively against the object name; first match wins, so the more
        // specific keywords come first. Tune with the report (Diagnostics > Declutter report).
        private static readonly (string Key, Category Cat)[] Keywords =
        {
            ("puddle", Category.Puddles),
            ("decal", Category.Decals), ("dusty", Category.Decals), ("wall_crack", Category.Decals), ("small_crack", Category.Decals), ("web_atlas", Category.Decals),
            ("glass_crush", Category.Shards), ("brokenglass", Category.Shards), ("broken_glass", Category.Shards), ("shards", Category.Shards),
            ("tile_broken", Category.Shards), ("broken_tile", Category.Shards),
            ("shotshell", Category.Casings), ("rifleshell", Category.Casings), ("rifle_shells", Category.Casings), ("shells_", Category.Casings),
            ("_shells", Category.Casings), ("casing", Category.Casings), ("cartridges", Category.Casings),
            ("bottle", Category.Packaging), ("canned", Category.Packaging), ("can_", Category.Packaging), ("juice", Category.Packaging),
            ("carton", Category.Packaging), ("crackers", Category.Packaging), ("chocolate", Category.Packaging), ("biscuit", Category.Packaging),
            ("cigarette", Category.Packaging), ("plasticcup", Category.Packaging), ("_cup", Category.Packaging), ("cup_", Category.Packaging),
            ("package_", Category.Packaging), ("pizza", Category.Packaging), ("medkit", Category.Packaging),
            ("styrofoam", Category.Packaging), ("polyethylene", Category.Packaging), ("tray_", Category.Packaging),
            ("rubble", Category.Debris), ("debris", Category.Debris), ("scrap", Category.Debris), ("heap", Category.Debris),
            ("pile", Category.Debris), ("crushed_concre", Category.Debris), ("scatter", Category.Debris), ("floorset", Category.Debris),
            ("bricks", Category.Debris), ("brick_pile", Category.Debris), ("branches", Category.Debris), ("sticks", Category.Debris),
            ("rock_small", Category.Debris),
            ("garbage", Category.Litter), ("trash", Category.Litter), ("litter", Category.Litter), ("junk", Category.Litter),
            ("paper", Category.Litter), ("cardboard", Category.Litter), ("leaves", Category.Litter), ("cloth_", Category.Litter),
            ("rag_", Category.Litter), ("shirt_", Category.Litter), ("pants_", Category.Litter), ("boot_", Category.Litter),
            ("book", Category.Litter), ("folder", Category.Litter), ("magazine", Category.Litter), ("cable", Category.Litter),
            ("dishes", Category.Litter), ("cutlery", Category.Litter), ("fork_", Category.Litter), ("spoon", Category.Litter),
            ("knife_", Category.Litter), ("drawing_", Category.Litter), ("leaflet", Category.Litter), ("flyer", Category.Litter),
            // Off by default: wall and room decoration rather than clutter.
            ("poster", Category.Decor), ("toy_", Category.Decor), ("candle", Category.Decor), ("flower", Category.Decor),
            ("wall_clock", Category.Decor), ("hanger", Category.Decor), ("calendar", Category.Decor), ("pencil_stand", Category.Decor),
            ("socket", Category.Decor), ("gardin", Category.Decor), ("karniz", Category.Decor), ("fuse_holder", Category.Decor),
            ("slippers", Category.Decor), ("shoe", Category.Decor), ("plastic_bucket", Category.Decor),
        };

        // Never touch objects whose name contains one of these, whatever else matches.
        private static readonly string[] Protected =
        {
            "item_", "weapon", "barter", "mod_", "loot", "container", "door", "window", "switch", "lamp", "light", "trigger",
            "mine", "exfil", "extract", "quest", "zone", "spawn", "ladder", "collider", "colider", "audio", "sound", "key",
            "culling", "occluder", "portal", "shadow", "plate_street", "number",
        };

        private const float ScanBudgetMs = 3f;
        private const float ScanDelaySeconds = 5f;
        private const float MaxHeight = 2f;
        private const Category Categories = (Category)0x7f; // everything but Decor (wall and room decoration)

        private sealed class Candidate
        {
            public Renderer[] Renderers;
            public Category Cat;
            public float Height;
            public string Key;
            public bool Solid;
            public bool Hidden;
        }

        private static ManualLogSource _log;
        private static bool _small, _medium;
        private static readonly List<Candidate> Candidates = new List<Candidate>();
        private static readonly HashSet<int> ClaimedRenderers = new HashSet<int>();

        // scan state
        private static float _raidSeenAt = -1f;
        private static bool _scanned;
        private static LODGroup[] _groups;
        private static int _groupIndex;
        private static double _scanMs;
        private static string _location = "";
        private static readonly Stopwatch Slice = new Stopwatch();

        // report data (only collected when WriteReport is on)
        public static bool WriteReport;
        private static readonly Dictionary<string, int> RejectReasons = new Dictionary<string, int>();
        private static readonly Dictionary<string, (int Count, float Height)> Unmatched = new Dictionary<string, (int, float)>();
        private static readonly Dictionary<string, int> MatchedKeys = new Dictionary<string, int>();

        public static int HiddenObjects { get; private set; }
        public static int HiddenRenderers { get; private set; }

        public static void Init(ManualLogSource log) => _log = log;

        /// <summary>Hide visual-only clutter.</summary>
        public static bool Small
        {
            get => _small;
            set { if (_small != value) { _small = value; Apply(); } }
        }

        /// <summary>Hide the meshes of clutter that has a collider (the collider stays).</summary>
        public static bool Medium
        {
            get => _medium;
            set { if (_medium != value) { _medium = value; Apply(); } }
        }

        public static void Tick()
        {
            if (!Raid.InRaid(out var world))
            {
                // Scene is unloading: the renderers go with it, nothing to restore.
                if (_raidSeenAt >= 0f || _scanned || _groups != null) Reset();
                return;
            }
            if (_raidSeenAt < 0f)
            {
                _raidSeenAt = Time.realtimeSinceStartup;
                _location = world.LocationId ?? "";
            }
            // The report also scans with the feature off, so a test raid has the scan done before any A/B step.
            if (!(_small || _medium || WriteReport) || _scanned) return;
            if (Time.realtimeSinceStartup - _raidSeenAt < ScanDelaySeconds) return;
            ScanStep();
        }

        private static void Reset()
        {
            Candidates.Clear();
            ClaimedRenderers.Clear();
            _groups = null;
            _groupIndex = 0;
            _scanned = false;
            _scanMs = 0;
            _raidSeenAt = -1f;
            HiddenObjects = HiddenRenderers = 0;
            RejectReasons.Clear();
            Unmatched.Clear();
            MatchedKeys.Clear();
        }

        // ---------------------------------------------------------------- scan

        private static void ScanStep()
        {
            Slice.Restart();
            if (_groups == null) _groups = UnityEngine.Object.FindObjectsOfType<LODGroup>();
            while (_groupIndex < _groups.Length && Slice.Elapsed.TotalMilliseconds < ScanBudgetMs)
            {
                var g = _groups[_groupIndex++];
                if (g != null) ConsiderGroup(g);
            }
            _scanMs += Slice.Elapsed.TotalMilliseconds;
            if (_groupIndex < _groups.Length) return;

            _scanned = true;
            int groups = _groups.Length;
            _groups = null;
            _log.LogInfo($"Declutter: scanned {groups} LOD groups on {_location} in {_scanMs:0} ms of main-thread time; " +
                         $"{Candidates.Count(c => !c.Solid)} clutter candidates ({string.Join(", ", Candidates.Where(c => !c.Solid).GroupBy(c => c.Cat).Select(g => $"{g.Key} {g.Count()}"))}), " +
                         $"plus {Candidates.Count(c => c.Solid)} medium (with colliders)");
            Apply();
            if (WriteReport) WriteReportFile();
        }

        private static void ConsiderGroup(LODGroup g)
        {
            var go = g.gameObject;
            if (!go.activeInHierarchy) return;
            string name = go.name;
            string key = null;
            Category cat = Category.None;
            foreach (var (k, c) in Keywords)
            {
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) < 0) continue;
                key = k;
                cat = c;
                break;
            }
            if (key == null && !WriteReport) return;
            string guard = FirstMatch(name, Protected);
            if (guard != null)
            {
                if (key != null) Reject("protected name: " + guard);
                return;
            }

            string why = StructuralReject(go, out var renderers, out float height, out bool solid);
            if (why != null)
            {
                if (key != null) Reject(why);
                return;
            }
            if (solid)
            {
                if (key == null) return;
                Reject("collider (medium objects)");
            }
            else if (key == null)
            {
                // Small, visual-only, but no keyword: report it so the keyword list can be tuned.
                if (height <= MaxHeight)
                {
                    string stem = Stem(name);
                    Unmatched.TryGetValue(stem, out var u);
                    Unmatched[stem] = (u.Count + 1, u.Height + height);
                }
                return;
            }
            if (height > MaxHeight) { Reject("taller than 2 m"); return; }

            // Nested LOD groups: a renderer belongs to the first candidate that claims it.
            var own = renderers.Where(r => ClaimedRenderers.Add(r.GetInstanceID())).ToArray();
            if (own.Length == 0) return;
            Candidates.Add(new Candidate { Renderers = own, Cat = cat, Height = height, Key = key, Solid = solid });
            if (WriteReport) { MatchedKeys.TryGetValue(key, out int n); MatchedKeys[key] = n + 1; }
        }

        /// <summary>
        /// Null if the hierarchy is visual-only apart from collision; otherwise the reason it isn't. Colliders and
        /// EFT's BallisticCollider script don't reject, they set <paramref name="solid"/>.
        /// </summary>
        private static string StructuralReject(GameObject go, out Renderer[] renderers, out float height, out bool solid)
        {
            renderers = null;
            height = 0f;
            solid = false;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                switch (c)
                {
                    case null: continue;
                    case Collider _: solid = true; continue;
                    case BallisticCollider _: solid = true; continue;
                    case Rigidbody _: return "rigidbody";
                    case Light _: return "light";
                    case AudioSource _: return "audio source";
                    case Animator _: return "animator";
                    case ParticleSystem _: return "particle system";
                    case SkinnedMeshRenderer _: return "skinned mesh";
                    case MonoBehaviour mb: return "script " + mb.GetType().Name;
                }
            }
            renderers = go.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !r.forceRenderingOff).ToArray();
            if (renderers.Length == 0) return "no visible renderer";
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            height = b.size.y;
            return null;
        }

        // ---------------------------------------------------------------- apply / restore

        private static void Apply()
        {
            int objects = 0, rends = 0;
            foreach (var c in Candidates)
            {
                bool want = _scanned && (c.Solid ? _medium : _small) && (Categories & c.Cat) != 0;
                if (want != c.Hidden) Set(c, want);
                if (c.Hidden) { objects++; rends += c.Renderers?.Length ?? 0; }
            }
            if (HiddenObjects != objects)
                _log?.LogInfo(objects > 0 ? $"Declutter: hiding {objects} objects ({rends} renderers)" : "Declutter: all clutter restored");
            HiddenObjects = objects;
            HiddenRenderers = rends;
        }

        private static void Set(Candidate c, bool hide)
        {
            c.Hidden = hide;
            if (c.Renderers == null) return;
            foreach (var r in c.Renderers)
                if (r != null) r.forceRenderingOff = hide;
        }

        // ---------------------------------------------------------------- helpers and report

        private static string FirstMatch(string s, string[] keys)
        {
            foreach (var k in keys)
                if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return k;
            return null;
        }

        private static void Reject(string reason)
        {
            if (!WriteReport) return;
            RejectReasons.TryGetValue(reason, out int n);
            RejectReasons[reason] = n + 1;
        }

        /// <summary>"Garbage_paper_03 (12)" -> "garbage_paper": drops the instance suffix and trailing numbering.</summary>
        private static string Stem(string name)
        {
            int p = name.IndexOf(" (", StringComparison.Ordinal);
            if (p > 0) name = name.Substring(0, p);
            name = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_', ' ', '-', '.');
            return name.ToLowerInvariant();
        }

        private static void WriteReportFile()
        {
            try
            {
                string dir = Path.Combine(BepInEx.Paths.GameRootPath, "SPTOptimizer");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"declutter_{_location}.txt");
                var sb = new StringBuilder();
                sb.AppendLine($"Declutter report, {_location}, {DateTime.Now:yyyy-MM-dd HH:mm}. Scan {_scanMs:0} ms.");
                sb.AppendLine($"Candidates: {Candidates.Count}; hidden now: {HiddenObjects} objects, {HiddenRenderers} renderers (small {_small}, medium {_medium}; categories {Categories}).");
                sb.AppendLine();
                sb.AppendLine("== Matched, by keyword (count, avg height m) ==");
                foreach (var g in Candidates.GroupBy(c => (c.Key, c.Solid)).OrderByDescending(g => g.Count()))
                    sb.AppendLine($"{g.Count(),7}  {g.Average(c => c.Height),5:0.00}  {g.First().Cat,-10} {g.Key.Key}{(g.Key.Solid ? "  [medium: has collider]" : "")}");
                sb.AppendLine();
                sb.AppendLine("== Keyword matches rejected, by reason ==");
                foreach (var kv in RejectReasons.OrderByDescending(kv => kv.Value))
                    sb.AppendLine($"{kv.Value,7}  {kv.Key}");
                sb.AppendLine();
                sb.AppendLine("== Small visual-only LOD groups with no keyword (top 300 name stems: count, avg height m) ==");
                foreach (var kv in Unmatched.OrderByDescending(kv => kv.Value.Count).Take(300))
                    sb.AppendLine($"{kv.Value.Count,7}  {kv.Value.Height / kv.Value.Count,5:0.00}  {kv.Key}");
                File.WriteAllText(path, sb.ToString());
                _log.LogInfo("Declutter: report written to " + path);
            }
            catch (Exception ex)
            {
                _log.LogError("Declutter report failed: " + ex);
            }
        }
    }
}
