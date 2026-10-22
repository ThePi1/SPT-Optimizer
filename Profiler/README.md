# SPTOptimizer.Profiler

A BepInEx plugin that records per-frame timing in EFT (SPT 4.0.13) without a Unity development build. It's the measuring tool for phases 0–2 of [../PLAN.md](../PLAN.md).

## Install / build

`dotnet build -c Release` builds the plugin and copies it to `I:\Games\spt-4-0-13-hideoutroulette\BepInEx\plugins\SPTOptimizer\`. To use a different install, pass `-p:SptDir=...`. The build is already deployed to that copy.

## Settings menu (F12)

Since 0.3.1 the settings are in four numbered sections; values from the old names carry over automatically. Fine-tuning values are *advanced* (tick **Advanced settings** to see them).

| Section | Everyday | Advanced |
| --- | --- | --- |
| **1. Capture** | Toggle capture (F9), Auto capture raids | Scene census key (F7), census at capture stop, per-subsystem timing, Unity counters, output folder, keep frames.bin |
| **2. A/B testing** | Run A/B sequence (F5), Sequence experiments, Sequence repeats, Step length (s), Screenshot every step, Auto-start after raid start (s), Auto-start maps | Round-robin layout, Experiments per Baseline, Cycle experiment key (F6) |
| **3. Method timing** | Toggle method instrumentation (F8) | Collect method timings during capture, Targets file |
| **4. Experiment parameters** | | FarShadowsOnlyOff distance, LodBiasLow value, FarVertexLightsOff distance, AnimCullCompletelyFar distance |

## Use

| Key | Does |
| --- | --- |
| **F9** | Start or stop a capture. |
| **F8** | Turn method instrumentation on or off (methods from the targets file). It freezes the game for a few seconds. Only works while not capturing. |
| **F7** | Scene census: a snapshot of quality settings, cameras (image-effect components, command buffers), lights, reflection probes, renderers, skinned meshes, animators, LOD groups, particles, terrain and behaviour counts. It causes a short hitch. One is also written automatically when a capture stops. |
| **F6** | Switch to the next render experiment (see below). |
| **F5** | Start or stop the automatic A/B sequence. |

A status line shows in the top-left corner for 3 seconds. Hotkeys and options are in ConfigurationManager (F12) or `BepInEx\config\com.sptoptimizer.profiler.cfg`. Setting **Auto capture raids** records from raid start to raid end.

Each capture writes `<game>\SPTOptimizer\captures\<date-time>_<map>\`:

| File | Contents |
| --- | --- |
| `summary.txt` | Hardware and settings, frame-time percentiles and 1%/0.1% lows, hitch counts, GC stats, PlayerLoop breakdown, top methods, and the 25 worst frames with what they spent time on. |
| `frames.csv` | One row per frame: frame time, GC events, allocations, Mono heap, player count, position, render counters, then one column per PlayerLoop subsystem (ms). |
| `methods.csv` | Only when instrumented. For each method: calls, self and inclusive ms per frame, worst frame, µs per call. |
| `playerloop.txt` | The game's PlayerLoop layout when the capture started. |
| `census.txt` | The scene census at capture stop (see F7). |
| `available_counters.txt` | Every Unity profiler stat this player exposes, and which ones were recorded. |

## What it measures

- **PlayerLoop markers.** While capturing, a timing marker goes before every PlayerLoop subsystem, so every frame splits into pieces like `Update.ScriptRunBehaviourUpdate` (all `Update()` scripts), `PreLateUpdate.ScriptRunBehaviourLateUpdate`, `FixedUpdate.PhysicsFixedUpdate`, `PostLateUpdate.FinishFrameRendering` (render submission), and `TimeUpdate.WaitForLastPresentationAndUpdateTime` (waiting on the render thread, GPU, vsync or the frame cap). EFT rebuilds the PlayerLoop at runtime, so `PlayerLoop.SetPlayerLoop` is patched to keep the markers in. When not capturing, no markers are present and the cost is zero.
- **Method instrumentation (F8).** Harmony prefix/postfix timing of the methods in `BepInEx\config\SPTOptimizer.Profiler.targets.txt`. The defaults are every `Update`/`LateUpdate`/`FixedUpdate` in Assembly-CSharp, the world, player and bot ticks, and every subsystem `BotOwner.UpdateManual` calls. `@callees Type:Method` adds a method and everything it calls directly, which is the way to drill down. Self time excludes time spent in other instrumented methods.
- **Per frame:** GC collections, Mono used/heap, and whether the GC is on. EFT turns the GC off during raids.
- **Counters the release player may not expose:** GC allocations per frame, draw calls and batches, and `FrameTimingManager` CPU/GPU times. They show as `n/a` if unavailable; the log line at capture start lists which ones work.

## Render pipeline detail

- **Per-camera timing.** Unity's `Camera.onPreCull`/`onPreRender`/`onPostRender` split each camera into `Cam.<name>.Cull` (culling, plus other pre-cull hooks) and `Cam.<name>.Render` (shadows, geometry passes, command buffers). These slots are carved out of the slot they ran in, usually `PostLateUpdate.FinishFrameRendering`. What remains in FinishFrameRendering is image effects (`OnRenderImage`) and present. `camera_renders` counts camera renders per frame.
- **Unity counters and markers.** At capture start, every built-in stat flagged as available in non-development players is recorded per frame as `ctr:<category>/<name> [unit]`. These include native timing markers, so culling or shadow times may be among them. Some marker times are summed across job threads.
- **Render callbacks.** The default targets time every `OnPreCull`, `OnPreRender`, `OnPostRender`, `OnRenderImage`, `OnWillRenderObject` and `OnBecameVisible` in game code and in post-processing, plus whatever is subscribed to the camera callbacks when you press F8.

## Render A/B experiments

These change visuals and exist only for measurement. Each fully reverts when you switch away. The active experiment is written into the `experiment` column of every frame.

| Experiment | Change |
| --- | --- |
| Baseline | nothing |
| FarShadowsOnlyOff | disables `ShadowsOnly` renderers farther than 100 m from the camera. The sun's shadow distance is 40 m, so there should be no visible change |
| AllShadowsOnlyOff | disables every `ShadowsOnly` renderer (upper bound; close shadows go missing) |
| ShadowsOff | sets `QualitySettings.shadows` to Disable |
| LodBiasLow | sets `QualitySettings.lodBias` to 1 (game default 2) |
| FarVertexLightsOff | disables non-shadow `ForceVertex` lights farther than 50 m. It's re-applied every 0.5 s because EFT's light culling toggles lights itself |

Player animation and IK experiments (the default F5 set; edit *2. A/B testing → Sequence experiments* to pick others):

| Experiment | Change | Measures |
| --- | --- | --- |
| IkDistance20 | `EFTHardSettings.CULL_GROUNDER` → 20 m (the game's asset value is 90 m) | IK/procedural cost of visible bots at 20–90 m |
| IkOffBots | `CULL_GROUNDER` → 0 | all bot IK and procedural animation in `Player.VisualPass` (upper bound) |
| AnimCullTransformsAll | every bot animator `CullUpdateTransforms` (EFT forces AlwaysAnimate within 10 m) | off-screen near bots |
| AnimCullCompletelyOffscreen | every bot animator `CullCompletely` | animation cost of all off-screen bots (upper bound). **This freezes off-screen bots in place: movement is root motion and footsteps come from an animator curve. Measurement only.** |
| AnimCullCompletelyFar | `CullCompletely` only for bots beyond 60 m | the far/off-screen share. **Also freezes those bots. Measurement only.** |

New per-frame columns: `bots_alive`, `bots_visible` (body renderer visible to any camera, shadow maps included), `bots_ik` (visible and within the IK distance), `bots_near` (within the 10 m AlwaysAnimate distance), `bots_anim_always`. The census adds a per-player table: distance, on-screen state, renderers forced off by EFT's bot culling, body/arms animator culling modes, hands controller. The default targets also time the per-player animation path (`Player.LateUpdate`, `VisualPass`, `ComplexLateUpdate`, `ComplexUpdate`, `ManualUpdate`, `ArmsUpdate` and their direct callees).

**F5** runs Baseline, then each experiment with Baseline in between, holding each step for 15 s (about 2.75 minutes in total). It starts a capture if none is running and stops it at the end. **Stand still and don't move the camera** for the whole sequence. `summary.txt` then gets an **A/B experiments** table: for each experiment, the mean frame time, camera cull and render, animation, main/render thread and GPU time, draw calls, SetPass and shadow casters, plus a delta against the Baseline segments either side of it. The first 3 s of every step are skipped. The step length is in *2. A/B testing*; the distances and lodBias value are in *4. Experiment parameters* (advanced).

`tools/analyze.py <capture> --experiment FarShadowsOnlyOff` filters the analyzer to one experiment.

## Batch testing (many candidates in one run)

Settings are in the *2. A/B testing* section.
- **Round-robin layout** (on by default). Several experiments run between Baseline steps: *Experiments per Baseline*, default 2. The order rotates every repeat, so each experiment sits next to a Baseline on one side, then the other. The report compares each experiment segment with the nearest Baseline segment before and after it. Six candidates × 3 repeats at 12 s per step take about 5½ minutes.
- **Auto-start.** *Auto-start after raid start (s)* > 0 starts a capture and the F5 sequence automatically, once per raid, on the maps listed in *Auto-start maps*. A 10 s warning shows first. You load in, walk to a busy spot and stand still.
- **Screenshots** (on by default). Inside each step's settle window (the 3 s the report skips), three shots are taken ~0.7 s apart: the experiment, a brief switch back to Baseline, then the experiment again. A pixel counts as a real change only if both experiment shots agree and both differ from the Baseline shot. Weapon sway, foliage, drifting cloud shadows and on-screen counters change between the two experiment shots too, so they drop out. Baseline steps take the same three shots without switching; their score is the noise floor. During the switch-back, frames keep the step's experiment tag.
  - `shots/diff.txt`: "real" and "raw" change % per step, plus the average per experiment
  - `NN_<step>_exp.png` and `_base.png`: full-size experiment and Baseline shots
  - `NN_<step>_change.png`: real changes in red
  - Type `fps 0` in the console before a run to hide the counters.
- **Report additions:**
  - `p99`: 99th-percentile frame time
  - `slow25/min`: frames over 25 ms per minute
  - the frame delta of every repeat, with its spread, to judge consistency
  - the capture header lists which optimizations were on
- **Correctness and diagnostic counters** come from the optimizations plugin, as a `Stats (30 s):` line in LogOutput.log. See its README.

## Slow-work logger (hitch attribution)

During every capture, single work items over 2 ms are recorded in `slow_work.txt` (grouped) and `slow_work.csv` (each one, with time and frame). Three kinds are covered:
- EFT `JobScheduler` jobs, with the job and stage name
- async/await continuations (`Update.ScriptRunDelayedTasks`)
- coroutine steps

Each entry names the async method, coroutine or job behind it.

Graphics-settings experiments (`Gfx*`) change EFT's own settings objects, which runs the same code as the in-game settings menu. Each is relative to your configured settings and is restored afterwards. They are not saved to `Graphics.ini`.

| Experiment | Change |
| --- | --- |
| GfxRender50 / GfxRender200 | render scale 0.5× / 2× pixels (SuperSampling) |
| GfxShadowsHigh / GfxShadowsUltra | shadow quality → High / Ultra |
| GfxVisibility1500 | overall visibility → 1500 |
| GfxLodBias4 | object LOD quality → 4 |
| GfxMipStreamingOn | mip streaming on |
| GfxTexturesLow | texture quality → Low |
| GfxPostFxOff | SSAO, SSR, anti-aliasing and sharpen off |
| GfxReflexOff / GfxReflexBoost | NVIDIA Reflex → Off / On + Boost |

Optimization switches:
- `BatchedOverlapsOff` turns batched overlaps off. It's on by default, so its gain is minus this step's delta.
- `RunawayGuardOff` turns the runaway move guard off. On Streets, run it before the BTR arrives.
- `AllOptimizationsOff` turns shadow proxy culling, batched overlaps and the runaway move guard off together, so the step's delta is their combined per-frame gain. It leaves the GC policy alone: that one only affects hitches, and switching it would let a collection land inside the step.
- `BatchedOverlaps` forces batched overlaps on. That's a no-op now that it's on by default.
- `Declutter` turns *Declutter small objects* on. `DeclutterSolid` turns both *Declutter small objects* and *Declutter medium objects* on (collider-bearing clutter meshes hidden, colliders kept). Either switch that's on in the config is held off for the sequence so Baseline measures them off. To keep the scan out of the measured steps, turn on *Diagnostics → Declutter report*: that runs the scan 5 s into the raid even while both are off.
- `BotAnimHalfRate` turns *Distant bot animation at half rate* on. Since 0.3.0 it affects only observed bots on a Fika client, so run it on a headless-hosted raid.
- `ObservedArmsReducedRate` turns *Far player arms at reduced rate* on (Fika client only). Held off for the sequence like the others.
- `ScopeHalfRate` turns *Scope camera at reduced rate* on (its configured interval and turn rule; held off for Baseline like the others). **Run it scoped in for the whole sequence**, e.g. `Sequence experiments = ScopeHalfRate`; outside a scope it does nothing. With PiP-Disabler it measures only the scopes PiP-Disabler leaves to the game (variable, thermal and night-vision scopes).

The 4.1.6 decompile rules out two other cheap knobs from the research report: EFT already sets every layer's cull distance from the *Overall visibility* setting (`CameraManager.SetOverallVisibility`; measured by `GfxVisibility400`), and the Low shadow preset already uses fewer than 4 cascades.

No-impact experiments: `AreaLightDispatch` (one camera hook for all AreaLights instead of ~1,170) and `GcOn` (GC enabled during the step, GC policy paused). The default F5 sequence is now `AreaLightDispatch, GcOn, FarVertexLightsOff`.

## Caveats

- The status message's IMGUI draw runs during the first 3 seconds of a capture. Skip those seconds when comparing.
- Instrumentation adds a fraction of a µs per call. Methods called thousands of times per frame are inflated, and parents' inclusive times include that overhead. `instr_calls` per frame shows how much.
- Virtual or interface calls found by `@callees` only time the base method, not overrides. Generic methods are skipped.
- Only main-thread work is timed. Job workers and the render thread show up indirectly, as wait time in the main thread.

## Suggested first runs

1. **F9** in the hideout or main menu for 10 s, as a sanity check. Look at `summary.txt`.
2. Factory, bots on, stand at spawn: **F9**, wait 60 s, **F9**. This is benchmark B2.
3. Same thing with **F8** first, so the capture includes method timings.
4. A busy map (Streets or Lighthouse) on a walking route, 60–120 s.

Send me the capture folders and `BepInEx\LogOutput.log`.
