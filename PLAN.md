# SPT Optimizer: plan of attack

Goal: lower frame time (average and, more importantly, p99 / 1% lows / hitches) in EFT raids running under SPT 4.0.13, by shipping BepInEx/Harmony patches that can each be toggled and measured.

## What we're working with

| | |
| --- | --- |
| Install | `I:\Games\spt-4-0-13-hideoutroulette` |
| Client | EFT 0.16.9.40087, Unity 2022.3.43f1, **Mono** scripting backend (not IL2CPP), so every C# method is Harmony-patchable |
| Mod loader | BepInEx 5.4.23.2 + SPT modules (`ModulePatch`) |
| Source | `eft-decomp\decompiled\Assembly-CSharp` (8,681 files, from the earlier Hideout Roulette work) |
| boot.config | `gfx-enable-gfx-jobs=1`, `gfx-enable-native-gfx-jobs=1`, `gc-max-time-slice=3` (incremental GC, 3 ms slice) |

Things already spotted in the decompile:

- `MemoryControllerClass` turns the Mono GC **off during raids** (if RAM ≥ `GigabytesRequiredToDisableGCDuringRaid`) and forces collections at checkpoints. Its logging helper `GetTotalAllocatedMemoryGB()` calls `GC.GetTotalMemory(forceFullCollection: true)`, which runs a full collection itself. Worth checking when that runs.
- `GameGraphicsClass` sets `targetFrameRate = -1` in raid when Reflex is on, otherwise the menu cap. An FPS cap set in the menu doesn't apply the same way in raid.
- About 1,000 types have `Update`/`LateUpdate`/`FixedUpdate`. The usual suspects: `BotsController`, `BotOwner`, `AICoreControllerClass`, `Player`, `GameWorld`, `CullingManager`, `OcclusionCullingSwitcher`, `DistantShadow`, audio occlusion.

## Status

- 2026-10-18: The profiler plugin (items 2 and 3 below) is built and in use, with render A/B experiments added. See [Profiler/README.md](Profiler/README.md). Results are in [FINDINGS.md](FINDINGS.md).
- 2026-10-18: The first optimizations (GC policy and shadow proxy culling) are built and deployed but not yet verified in game. See [Optimizations/README.md](Optimizations/README.md).
- 2026-10-19: On by default and verified: GC policy, shadow proxy culling, batched overlaps, runaway move guard, faded light culling. Combined steady-state gain measured directly: −1.57 ms (+12% FPS) on Streets, plus −5–6 ms in the pre-BTR window. The batch test framework is in the profiler: round-robin A/B, auto-start, screenshot triple, Stats counters.

- 2026-10-20: FIKA headless (host on a second PC) moves AI, bot movement and collision, and most bot animation off the client. **Use Borderless, not exclusive fullscreen**: exclusive adds a ~4 ms present wait per frame on the headless client (12.30 → 8.83 ms at the same spot, +39% FPS). On the client the render-side optimizations still give ~1 ms. Batched overlaps and the runaway guard belong on the host.
- 2026-10-20: Like-for-like self-hosted vs headless (same install, all optimizations on): −1.8 to −2.0 ms measured, about −1.4 to −1.6 ms after correcting for the headless raid having 4 fewer bots. Play 1% lows went 61 → 86 fps. This supersedes the earlier "4–5 ms" estimate, which compared against an older instrumented single-player run.
- 2026-10-20: Declutter (idea from the Simple-Declutter mod) built as two opt-in settings, small and medium objects. It is safe in multiplayer but gives ~0 ms of frame time: hidden renderers still cost culling, and the client is main-thread-bound. Kept as a visual preference.
- 2026-10-20: Research report on rendering, script and Fika techniques: [reports/Tarkov Unity optimization research.md](reports/Tarkov%20Unity%20optimization%20research.md). Top picks are cheaper presentation of observed players (now partly built into Fika 2.4.4+) and distance-banded disabling of static renderer clusters.
- 2026-10-20: All 4.0.13 test data archived in `data\spt-4.0.13\` (git-ignored). Next: move to SPT 4.1.6 / Fika 2.4.6 (newer EFT client). Steps are in [PORTING.md](PORTING.md); testing is in [TESTING.md](TESTING.md).

### Parked (flagged, revisit later)
- **Half-rate distant bot animation:** −0.7 to −1.1 ms on Streets, but off-screen bots run ~20–25% slower. Unity's animator delivers ~20% less root motion when it catches up two frames in one update. Possible fixes: root-motion calibration from occasional full-rate samples, or half-rate only for **stationary** bots, where root motion doesn't matter (most off-screen bot time is idle). See FINDINGS.md, 2026-10-19 half-rate sections.

## Phase 0: Measure reliably first

Without a repeatable benchmark, no optimization can be proven. Build three things:

1. **External frame-time capture.** PresentMon (or CapFrameX, which wraps it) gives per-frame present intervals plus CPU-busy and GPU-busy times. This is the ground truth and needs no game changes.
2. **In-game profiler plugin (`SPTOptimizer.Profiler`).** A BepInEx plugin that, on a hotkey, records N seconds to CSV, one row per frame:
   - CPU time per PlayerLoop phase (inject timing markers into `PlayerLoop`: EarlyUpdate, FixedUpdate, Update, PreLateUpdate, PostLateUpdate). This shows script time vs. render submission.
   - GC: `GC.CollectionCount(0..2)`, Mono heap size, bytes allocated this frame. GC spikes show up here directly.
   - Context: live bot count, players, map, camera position.
   - `ProfilerRecorder` counters available in a release player (draw calls, SetPass, triangles; verify which ones report anything outside a development build).
3. **Harmony "sampling" instrumentation.** In a second mode, wrap a configurable list of methods (every `Update`/`LateUpdate` on hot types, the bot brain tick, `Player.LateUpdate`, etc.) with Stopwatch prefix/postfix and aggregate total ms per frame per method. This works as a release-build profiler, so we don't need Unity's development player.

Optional, deeper: Windows Performance Recorder (WPR/WPA) for native threads (render thread, job workers, driver), and trying to swap in a development `UnityPlayer.dll` of the same version so the real Unity Profiler can attach. That one is risky and may not work with EFT's build.

**Benchmark scenarios** (you run these; the plugin and PresentMon log them):

| ID | Scenario | Purpose |
| --- | --- | --- |
| B1 | Factory, bots off, stand at spawn 60 s | Floor: engine + render cost |
| B2 | Same spot, bots on (normal amount) | AI cost |
| B3 | Streets or Lighthouse, fixed walk route, bots on | Worst-case open map |
| B4 | Combat: a fight with several bots | Hitches from shots, ballistics, decals, audio, ragdolls |
| B5 | 10+ minute raid | GC disabled → heap growth → late hitches |

Each scenario runs 3× per configuration. Report avg, p50, p95, p99 frame time and hitches >33 ms.

## Phase 1: Find out where the frame goes

From B1–B5, decide per map which of these limits frame time:

- **Main-thread CPU (scripts):** most likely. EFT is known to be CPU-bound on bots, animators, and physics.
- **Render thread / draw-call submission:** shows as PostLateUpdate / `Gfx.WaitForPresent` time with an idle GPU.
- **GPU:** PresentMon GPU-busy ≈ frame time. Mostly out of scope except via settings and culling.
- **Hitches:** GC, asset/bundle loading, shader compilation, first-time spawns.

This decides where the effort goes. Don't optimize code that's 0.2 ms of a 16 ms frame.

## Phase 2: Pin down the hot spots

Point the Harmony instrumentation at the categories that dominated, then drill down. Read the decompiled code of the top items and look for:

- Per-frame allocations (LINQ, closures, `ToList`, string formatting, boxing, `GetComponentsInChildren`)
- Repeated work that could be cached or done less often (bot sensing, cover search, path updates, raycasts for distant/off-screen bots)
- O(n²) loops over players/bots/loot
- Allocating physics queries where `NonAlloc` variants exist
- Debug/logging work left on in release (string building even when the log level is off)
- Animator/IK updates on bots that are far away or off-screen

## Phase 3: Fix it, one toggle at a time

All fixes live in one BepInEx plugin, each behind its own config entry (ConfigurationManager, F12), so each can be A/B tested and disabled if it breaks something. Roughly cheapest first:

1. **Config/runtime knobs:** GC mode during raids, incremental GC time slice, job worker count, frame pacing / Reflex interaction, and any `MemoryControllerClass` forced collections that run mid-raid.
2. **Allocation removal** in hot paths (Harmony transpilers or prefix replacements).
3. **Throttling and LOD for AI and animation:** tick distant bots less often, reduce animator/IK updates when not visible. These change gameplay, so they need careful A/B and behaviour checks.
4. **Caching / algorithmic fixes** for whatever Phase 2 finds.
5. **Render-side:** culling distances, shadow casters, and per-light work, only if Phase 1 shows render-thread or GPU bound.

For every fix: measure the before/after on the relevant benchmark, check there are no behaviour changes (bots still see, hear and fight the same way), and keep it off by default if the effect is unclear.

## Phase 4: Package it

One plugin with a clear config file, a README listing each optimization with measured gains, compatibility notes (Fika, SAIN, other popular mods that patch the same methods), and the benchmark data.

## Ground rules

- Only touch this SPT copy. The original `Assembly-CSharp.dll.spt-bak` stays untouched; prefer runtime Harmony patches over editing dlls.
- Obfuscated names (`GClassNNNN`, `method_N`) are tied to this client build. Target methods by signature or a named neighbour where possible.
- Nothing ships without a number behind it.

## What I need from you to start

1. Your CPU, GPU, RAM, resolution, and in-game graphics settings (or a screenshot of the settings screen).
2. Which maps and situations feel worst to you.
3. OK to install PresentMon (or CapFrameX) for external capture.
4. Once I've built the profiler plugin: run B1–B3 and send me the `BepInEx\LogOutput.log` and the CSVs it writes.
