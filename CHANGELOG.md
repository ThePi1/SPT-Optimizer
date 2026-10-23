# Changelog

Both plugins (`SPTOptimizer.Optimizations` and `SPTOptimizer.Profiler`) share one version number. Gains are frame-time changes measured on Streets; see [FINDINGS.md](FINDINGS.md) for the captures behind each number.

## 0.4.0 (2026-10-23), SPT 4.1.6

Includes everything from the unreleased 0.3.0 and 0.3.1.

### New
- **LOD group culling** (on by default). Switches off LOD groups whose renderers are all hidden, by EFT's own occlusion culling (Perfect Culling) or by shadow proxy culling, and back on before they're shown again. About 108,000 of ~137,000 groups on Streets. −0.56 ms of camera culling.
- **Scope camera at reduced rate** (off by default). While you look through a magnified scope, its picture renders every Nth frame (default 2), and at full rate while you turn quickly. −0.9 to −1.8 ms while scoped. Leaves the scopes PiP-Disabler handles alone.
- **Far player arms at reduced rate** (off by default, Fika clients only). The arms and weapon animation of far or off-screen bots updates every Nth frame. −0.31 ms with ~20 bots.

### Changed
- **The settings menu (F12) is reorganised** into numbered sections: *1. Standard Optimizations*, *2. Extra Optimizations (potential visible impact)*, *3. Experimental / Other*, *4. Diagnostics*. Fine-tuning values are under **Advanced settings**. Settings from 0.2.0 are not carried over; delete the old `com.sptoptimizer.*.cfg` files to clean them up.
- *GC policy* is now called *Modify GC policy*.
- **Distant bot animation at half rate** now only affects bots on a Fika client, where the host sets their position, so they no longer move slower. It measured ~0 ms there, so it stays off and is listed as experimental.

### Removed
- **Bot IK distance.** Fika 2.4.4+ sets the same value through its own *IK Cull Distance* setting, and ours reset it on every settings change.

### Fixed
- The 0.3.0 build failed to start (a settings name contained a character BepInEx rejects), which switched every optimization off. Fixed before release.

### Profiler
- New A/B steps: `ObservedArmsReducedRate`, `ScopeHalfRate`, `LodGroupCulling`, and the measurement-only `EmptyLodGroupsOff`.
- The scene census adds physics sync settings, shadow-proxy mesh readability and static batching, and LOD group composition.
- The menu is reorganised the same way: *1. Capture*, *2. A/B testing*, *3. Method timing*, *4. Experiment parameters*.

### Compatibility
- Checked against PerformanceScope, Dynamic External Resolution, PiP-Disabler and Tarkov DLSS 4.5: no shared patch targets.

## 0.2.0 (2026-10-20), SPT 4.1.6

- Port to SPT 4.1.6 / Fika 2.4.6 (EFT 0.16.9.40743): about 20 hooks re-targeted to the readable names SPT 4.1 uses. The mapping is in [PORTING.md](PORTING.md).
- Built against SPT 4.1.6's DLLs, which SPT requires to load the plugins.

## 0.1.0 (2026-10-20), SPT 4.0.13

First release.

- **On by default:** GC policy (no garbage collection during gameplay; removes 50–150 ms looting hitches), shadow proxy culling (−0.5 to −1.0 ms), batched character-controller overlaps, runaway move guard (the parked BTR gunner on Streets, up to −6 ms), faded light culling (−0.24 ms). Together −1.57 ms (+12% FPS) self-hosted.
- **Opt-in:** declutter small / medium objects, object LOD bias override, bot IK distance, distant bot animation at half rate (experimental), BSG fast body animator (experimental).
- **Profiler plugin:** per-frame captures, method instrumentation, scene census, automated A/B sequences with screenshot comparison.
