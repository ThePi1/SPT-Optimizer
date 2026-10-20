using System;
using BepInEx.Logging;
using EFT.UI.Screens;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// In-raid GC policy.
    ///
    /// EFT turns the Mono GC off at raid start. SPT's MemoryCollectionPatch turns it back on (and starts an
    /// incremental collection) when the Inventory opens, and nothing turns it off again, so after the first
    /// loot every collection is a 50–90 ms hitch on a 3 GB heap in the middle of gameplay.
    ///
    /// This keeps SPT's behaviour while a menu is open but turns the GC off again when the player is back in
    /// the game view. A safety valve turns it on if the managed heap grows too far past its raid-start size,
    /// and off again once collections bring it back down.
    /// </summary>
    internal sealed class GcPolicy
    {
        public bool Enabled = true;
        public bool CollectInAnyMenu;
        public float HeadroomMB = 3072f;

        private readonly ManualLogSource _log;
        private bool _inRaid;
        private float _baselineMB;
        private bool _valveOpen;
        private bool _wasInMenu;
        private float _menuEnterUsedMB;
        private EEftScreenType _menuScreen;
        private int _menuEnterCollections;
        private float _nextCheck;
        private float _raidStart;
        private bool _hadMenu;

        public GcPolicy(ManualLogSource log)
        {
            _log = log;
        }

        public static float MonoUsedMB => Profiler.GetMonoUsedSizeLong() / 1048576f;

        public void Tick()
        {
            // Screen and heap state change slowly; 10 checks a second is plenty and costs nothing.
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 0.1f;

            bool inRaid = Raid.InRaid(out _);
            if (inRaid != _inRaid)
            {
                _inRaid = inRaid;
                _valveOpen = false;
                _wasInMenu = false;
                if (inRaid)
                {
                    // EFT has just run its raid-start collection and disabled the GC.
                    _baselineMB = MonoUsedMB;
                    _raidStart = Time.realtimeSinceStartup;
                    _hadMenu = false;
                    _log.LogInfo($"GC policy: raid started, managed heap baseline {_baselineMB:0} MB, GC {GarbageCollector.GCMode}");
                }
                // Outside a raid EFT manages the GC itself; leave it alone.
            }
            if (!inRaid || !Enabled) return;

            EEftScreenType screen = Raid.CurrentScreen();
            bool inMenu = screen == EEftScreenType.Inventory || (CollectInAnyMenu && screen != EEftScreenType.None && screen != EEftScreenType.BattleUI);
            float used = MonoUsedMB;
            // EFT's raid-start collection may land just after we first see the raid; take the lowest early value.
            if (Time.realtimeSinceStartup - _raidStart < 60f && used < _baselineMB) _baselineMB = used;

            if (inMenu && !_wasInMenu)
            {
                _hadMenu = true;
                _menuScreen = screen;
                _menuEnterUsedMB = used;
                _menuEnterCollections = GC.CollectionCount(0);
                // SPT's patch enables the GC for the Inventory; do it ourselves for the other menus.
                if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
                {
                    GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                    GarbageCollector.CollectIncremental(25_000_000UL);
                }
            }
            else if (!inMenu && _wasInMenu)
            {
                _log.LogInfo($"GC policy: left {_menuScreen} (now {screen}); heap {_menuEnterUsedMB:0} -> {used:0} MB, {GC.CollectionCount(0) - _menuEnterCollections} collections while open");
            }
            _wasInMenu = inMenu;

            // Safety valve against runaway growth while the GC is off.
            float limit = _baselineMB + HeadroomMB;
            if (!_valveOpen && used > limit)
            {
                _valveOpen = true;
                _log.LogWarning($"GC policy: managed heap {used:0} MB passed limit {limit:0} MB; enabling GC until it drops below {_baselineMB + HeadroomMB / 2:0} MB");
            }
            else if (_valveOpen && used < _baselineMB + HeadroomMB / 2)
            {
                _valveOpen = false;
                _log.LogInfo($"GC policy: heap back to {used:0} MB, safety valve closed");
            }

            var want = inMenu || _valveOpen ? GarbageCollector.Mode.Enabled : GarbageCollector.Mode.Disabled;
            if (GarbageCollector.GCMode != want)
            {
                GarbageCollector.GCMode = want;
                _log.LogInfo($"GC policy: GC {want} (screen {screen}, heap {used:0} MB, valve {(_valveOpen ? "open" : "closed")})");
            }
        }

        /// <summary>Called when the policy is switched off at runtime: hand back the state SPT would have left (GC on once a menu was opened).</summary>
        public void Release()
        {
            if (_inRaid && _hadMenu && GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
            {
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                _log.LogInfo("GC policy disabled: GC enabled");
            }
        }
    }
}
