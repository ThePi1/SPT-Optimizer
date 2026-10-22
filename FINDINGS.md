# Findings

Machine: Ryzen 7 7800X3D, RTX 3090, 64 GB RAM, 2560x1440 borderless at 240 Hz, D3D11, quality preset "ShadLow".

## 2026-10-18: first captures (hideout and a Factory day raid, no method instrumentation)

Captures: `SPTOptimizer\captures\20261018-105744_hideout`, `20261018-105845_unknown` (menu → Factory → menu). Analyzed with `python tools\analyze.py <dir> --from 35 --to 119`.

### Frame-capped, so averages only show headroom
- In raid, `Application.targetFrameRate` is the in-game "game FPS limit" (`GameGraphicsClass.ChangeFramerate`). It was 120: frames sat at 8.45 ms, about 4 ms of which was waiting in `TimeUpdate.WaitForLastPresentationAndUpdateTime`. The cap is removed only by "disable game framerate limit" or by Reflex being on with vsync off. The hideout and menu have their own lobby cap (120 and 60).
- The same value goes to EFT's `JobScheduler.SetTargetFrameRate`, so the cap may also change how much deferred work EFT does per frame.
- **For benchmarking, turn the in-game FPS limit off.**

### Steady state, Factory (4–8 players, frames 35–119 s)
| | ms/frame |
| --- | --- |
| Main-thread work (frame minus present wait) | avg 4.41, p50 3.95, p99 9.19 |
| `PostLateUpdate.FinishFrameRendering` (render submission) | 1.44 |
| `PreLateUpdate.ScriptRunBehaviourLateUpdate` | 0.95 (p95 3.8) |
| `PreLateUpdate.DirectorUpdateAnimationBegin` (Animators) | 0.73 |
| `Update.ScriptRunBehaviourUpdate` | 0.48 |
| Draw calls / SetPass | about 1,600 / 675 avg, peaks around 3,000 / 1,270 |

Factory with a 12-bot cap isn't CPU-heavy on this CPU. A busier map is needed.

### Hitches in raid (frames > 18 ms)
Grouped by the PlayerLoop slot that took the time:
- `Update.ScriptRunDelayedDynamicFrameRate` (coroutines, `yield return null`): 35–65 ms spikes.
- `Update.ScriptRunDelayedTasks` (async/await continuations): recurring 6–12 ms. In the hideout it runs every ~1–3 s in quantized steps of 11.5 or 22.9 ms. This looks like one specific periodic task.
- `Update.ScriptRunBehaviourUpdate`: recurring 9–17 ms.
- `PreLateUpdate.ScriptRunBehaviourLateUpdate`: 15–30 ms.
- `PostLateUpdate.UpdateAudio`: 10–11 ms (Unity audio, 2 events).
- GC: 2 collections in raid, each about a 25 ms frame (time shows up as present wait).

Raid loading and the first ~15 s in raid have multi-second frames: `FixedUpdate.ScriptRunBehaviourFixedUpdate` 7.3 s with 30 GCs, LateUpdate 1.4 s, coroutines 1.1 s. That's loading work, but it's also part of what players feel.

### GC behaviour
- EFT: `BaseLocalGame.method_7` forces a full blocking GC and then turns the GC **off** at raid start (`MemoryControllerClass.GCEnabled = false`).
- SPT: `spt-custom` `SPT.Custom.Patches.MemoryCollectionPatch` (postfix on `MenuTaskBar.OnScreenChanged`) turns the GC back **on** and runs `GarbageCollector.CollectIncremental(25 ms)` when the Inventory screen opens in raid. Nothing turns it off again, so from the first inventory open the rest of the raid runs with incremental GC. Seen at t=65.6 s.
- Mono heap stayed around 1.2 GB in a short raid. Long raids (B5) are needed to see whether GC-off growth or GC-on collections are worse.

### Profiler notes
- Work in this player: PlayerLoop timing, GC counts, Mono heap, and draw call, SetPass, batch and triangle counters.
- Not available: GC allocations per frame, and FrameTimingManager CPU/GPU times (Frame Timing Stats weren't enabled in the build).

## 2026-10-18: Streets, uncapped, with method instrumentation (557 methods)

Capture `20261018-134812_TarkovStreets`: 23 players alive (about 10 active bots), in raid from t=0 to about 90 s. The inventory was opened at t=54.5 (GC on from then). Instrumentation overhead is about 990 timed calls per frame, roughly 0.3 ms.

### CPU-bound: present wait is about 0. Main-thread work 11.4 ms per frame (87 fps)
Steady state with GC off (t=2–48):

| Slot | avg ms | share |
| --- | --- | --- |
| `PostLateUpdate.FinishFrameRendering` (culling and render submission on the main thread) | 4.22 | 37% |
| `PreLateUpdate.DirectorUpdateAnimationBegin` (Unity Animators) | 2.37 | 21% |
| `Update.ScriptRunBehaviourUpdate` | 2.01 | 17% |
| `PreLateUpdate.ScriptRunBehaviourLateUpdate` | 1.95 | 17% |
| Draw calls / SetPass | 2,770 / 1,710 avg | |

Top scripts (self ms per frame): `Player.LateUpdate` **1.16** (19.9 calls, 58 µs each), `Player.ComplexUpdate` 0.40 (34 calls), `CullingManager.Update` 0.20, `AICoreControllerClass.Update` 0.16, `AmbientLight.LateUpdate` 0.15, `MuzzleSystem.LateUpdate` 0.11, `StaticManager.Update` 0.10, `FlareScheduler.LateUpdate` 0.09, `PerfectCullingCrossSceneGroup.Update` 0.07 (269 instances).

**Bot AI is cheap**: `BotOwner.UpdateManual` is 0.13 ms per frame inclusive for all bots. The per-player cost is animation (Animators plus `Player.LateUpdate`, i.e. IK and procedural animation), plus rendering.

### Per-player code paths (`Player.LateUpdate` → `VisualPass`)
- Full-body IK (`FBBIKUpdate`), procedural weapon animation and head/hand IK run for every player that is first-person, **or visible within `EFTHardSettings.CULL_GROUNDER` = 40 m**.
- Animator culling: beyond `EFTHardSettings.AnimatorCullDistance` = 10 m it's `CullUpdateTransforms`; inside 10 m it's `AlwaysAnimate`, even when off-screen.
- `ApplicationConfigClass.UseSpiritPlayer`/`UseBodyFastAnimator` (BSG's simplified distant players) exists, but `BaseLocalGame` forces `UseSpiritPlayer = false` for local raids (line 476). Not a free switch.

### GC
- With GC off the Mono heap was 3.3 GB at raid start and grew about **2.8 MB/s** (about 32 KB per frame).
- After the inventory opened (SPT turns the GC on), each collection was a **56–68 ms** frame on a 3.4 GB heap. One ran inside `AICoreControllerClass.Update` (allocation-triggered), one at frame start.

### Hitches
Opening the inventory: a 47 ms coroutine frame followed by 10 ms task frames. `InputManager.Update` 17 ms in the UI. Extraction: `Player.ComplexUpdate` 55 ms, then a 1.8 s coroutine and a 2.2 s task (raid end). Apart from those and GC, steady state is smooth: p99 15.7 ms vs p50 11.3.

## 2026-10-18: Streets, render pipeline breakdown

Capture `20261018-173012_TarkovStreets`, 160 s, 18 alive players, GC off until t=124. Steady window 20–120 s: 13.8 ms per frame (72.6 fps), CPU-bound. Instrumentation was larger this time (710 methods, about 2,240 calls per frame), so script slots are inflated by roughly 0.5 ms versus a clean run.

### Where `FinishFrameRendering` went
| | ms/frame |
| --- | --- |
| `Cam.FPS Camera.Cull` (onPreCull → onPreRender: culling, LOD selection, pre-cull hooks) | **2.21** |
| `Cam.FPS Camera.Render` (onPreRender → onPostRender: shadows, G-buffer, lighting, command buffers) | **2.22** |
| Remaining `FinishFrameRendering` (image effects, present) | 0.51 |
| Other cameras (ambient reflection probe, icon camera) | about 0.02 |

There's only one real camera render per frame, so no stray scope, reflection or mirror cameras are adding cost.

**Managed render callbacks are small.** Every OnPreCull/OnPreRender/OnRenderImage/camera-delegate method together is about 0.7 ms (largest: `AreaLight.method_2` 0.20 ms over 1,174 calls per frame, `StaticDeferredDecalRenderer` 0.08, `UltimateBloom` 0.08, `PostProcessLayer.OnPreCull` 0.06, `PrismEffects` 0.06). **Nearly all of the 4.4 ms of camera time is native Unity work.**

### Why culling is expensive (census)
- **211,298 enabled renderers** (535k active), **2,713 visible**. 177k of them cast shadows, and **173,442 are `ShadowsOnly`**.
- **136,785 enabled LOD groups** (124k cross-fade).
- **4,803 enabled lights** (4,789 `ForceVertex` point/spot lights with no shadows). Only 4 realtime shadow casters, plus the directional sun.
- Camera: deferred, HDR, `useOcclusionCulling = false` (EFT uses Koenigz PerfectCulling and GPUInstancer Hi-Z instead), far 10,000 m, layer cull distances 1,000 m, depth + normals + motion vectors.
- Quality: shadow distance 40 m with 2 cascades, `lodBias` 2, `pixelLightCount` 5.
- Command buffers: about 37 AreaLight/GI buffers in `AfterImageEffectsOpaque`, plus decals, distant shadow, clouds, SSAA copies.
- Rendering is threaded (`GfxDevice: threaded=1; jobified=1` in Player.log). `SystemInfo.graphicsMultiThreaded` reports False because it only means native graphics jobs.

Unity's per-camera culling tests every enabled renderer (and shadow caster, light and LOD group) each frame. With about 200k renderers that's the likely bulk of the 2.2 ms cull. Most of them are shadow-only proxies or things far outside the 40 m shadow distance.

### Other spikes in this run
`Diz.Jobs.JobScheduler.LateUpdate` (EFT's time-sliced job runner) 15–35 ms spikes, `AICoreControllerClass.Update` 10–18 ms, `GameWorld.DoWorldTick` 15–30 ms, `InputManager.Update` 15–41 ms (inventory UI), and 2 GCs at 79–87 ms.

### Profiler fixes from this run
- The Unity counter recorders never started (`ProfilerRecorderOptions.Default` is 24 here and doesn't include StartImmediately). Fixed. The next run will record the 43 exposed stats, including **CPU Main/Render Thread Frame Time, GPU Frame Time, Shadow Casters Count, Visible Skinned Meshes**, and the `GC.Collect` marker.
- The census now adds enabled renderers by distance band × shadow mode, the most common ShadowsOnly renderer names, and the LOD group coverage.

## 2026-10-18: Streets, clean run (no instrumentation), with Unity counters

Capture `20261018-185634_TarkovStreets`, 61 s, 23 alive players, GC off. 14.47 ms avg (69 fps), p99 22.7.

### Thread balance (Unity FrameTiming counters)
| | avg ms | p99 |
| --- | --- | --- |
| CPU main thread | **14.45** | 22.7 |
| CPU render thread | 5.18 | 7.8 |
| GPU | 5.98 | 9.5 |

**The main thread is the bottleneck by a wide margin.** The GPU is idle about 60% of the time and the render thread about 65%. GPU-side savings (resolution, post-processing) won't raise fps. Only main-thread work counts.

Main-thread split: Animators 3.03, camera render 2.57, LateUpdate 2.52, camera cull 2.36, Update 2.30, FinishFrameRendering remainder 0.50 ms.
Counters: 3,612 draw calls, 2,645 SetPass, 707 shadow casters drawn (p99 1,682), 1.67M triangles, 18 visible skinned meshes, 123k GPU buffers.

### What the culling walks over (census, camera at 48,4,204)
| distance | shadow On | shadow Off | **ShadowsOnly** | visible |
| --- | --- | --- | --- | --- |
| <25 m | 237 | 767 | 2,719 | 749 |
| 25–50 m | 127 | 623 | 4,430 | 760 |
| 50–100 m | 496 | 2,458 | 12,847 | 154 |
| 100–200 m | 959 | 7,235 | 53,715 | 325 |
| 200–400 m | 1,771 | 11,459 | **93,430** | 475 |
| 400–800 m | 51 | 540 | 6,415 | 31 |

- 200,399 enabled renderers. **173,585 are ShadowsOnly proxies** (e.g. `*_SHADOW_LOD0..3` on fences, pipes and props, plus 8,719 loot `shadow` meshes), **~166k of them beyond 50 m**, while the sun's shadow distance is 40 m and the only other shadow casters are 4 spot lights (≤23 m range).
- 99.5% of renderers sit under a LOD group (each shadow-proxy LOD level is its own renderer).
- `DistantShadow` (far shadows) builds its own command buffer from the LOD meshes at init (`DrawMesh`/`DrawMeshInstanced` with captured matrices, `DistantShadow.method_1`), so it doesn't depend on those renderers staying enabled.
- **Hypothesis:** far ShadowsOnly renderers are pure culling and LOD-selection overhead. Disabling ShadowsOnly renderers beyond about 2× the shadow distance should cut `Cam.Cull` without any visual change. Needs an A/B.

### Odd memory counter
`Profiler Used Memory` is 6.1 GB (6.6 GB reserved) of the 13.5 GB Unity total. That's Unity's profiler bookkeeping in a release player. Worth checking whether it's ours (ProfilerRecorders) or present without the plugin. It won't affect frame time, but it's RAM.

## 2026-10-18: Streets render A/B (F5 sequence, standing still, raining)

Capture `20261018-191205_TarkovStreets`: 11 × 15 s steps, 22 players, GC off. The first 3 s of each step are skipped. Deltas are paired against the Baseline segments either side.

**How noisy it is:** across the 6 Baseline segments, camera cull was 2.05–2.12 ms and render 2.23–2.27 ms, so those deltas are solid. Animation (1.95–2.84) and Update/LateUpdate (±0.3) drift with bot activity, so treat script deltas as uncertain. Rain was constant for the whole run. Its absolute cost isn't measured, so compare future runs in the same weather. None of the disabled renderers were re-enabled by the game during an experiment.

| Experiment | What changed | Cull Δ | Render Δ | Main-thread render total Δ* | Render thread Δ | GPU Δ | Draws Δ | Shadow casters Δ | Visual cost |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **FarShadowsOnlyOff** | 160,595 ShadowsOnly renderers > 100 m disabled | **−0.54** | **−0.40** | **≈ −1.0** | −0.08 | −0.03 | +16 | −12 | none expected (beyond 40 m shadow distance) |
| AllShadowsOnlyOff | 173,683 disabled | −0.54 | −0.55 | ≈ −1.15 | −0.48 | −0.28 | −533 | −667 | close shadows missing |
| ShadowsOff | no shadows | −0.31 | −0.66 | ≈ −1.0 | −0.59 | −0.45 | −647 | −841 | no shadows |
| LodBiasLow | lodBias 2 → 1 | −0.23 | −0.39 | ≈ −0.6 | **−1.39** | −0.44 | −898 | −194 | earlier LOD switches |
| FarVertexLightsOff | 4,728 ForceVertex lights > 50 m disabled | **−0.35** | −0.05 | ≈ −0.4 | −0.04 | −0.04 | −26 | −10 | unknown; needs a visual check |

\* Cull + render + RendererNotifyInvisible + FinishFrameRendering. The total frame-time deltas were −1.98 / −1.48 / −1.84 / −1.10 / −0.44 ms, including the noisy script slots.

**Conclusions**
1. **Far ShadowsOnly proxies are pure overhead: about 1 ms of main-thread time (≈8% of the frame) with no visible change.** Disabling them gives most of what turning shadows off entirely would (shadows-off saves about the same main-thread time but costs the visuals). The few far proxies that remain enabled hardly matter: AllShadowsOnlyOff only adds −0.15 ms on top. Shadow casters actually drawn stayed the same (−12), which confirms they never contributed to visible shadows.
2. **Far vertex lights cost about 0.35 ms of culling.** Whether disabling them changes anything visible (distant lamp glow?) needs a check.
3. LodBias mostly relieves the render thread, which isn't the bottleneck. It's a quality setting, not a free win.
4. Together, (1) and (2) are worth about 1.3–1.4 ms per frame on Streets (~12.6 ms → ~11.2 ms, ≈ +12% fps), as long as the visual checks pass.

**Turning (1) into a real optimization:** it has to follow the player. Options: Unity `CullingGroup` with a sphere per proxy and a distance band (native, event-driven), or a coarse grid re-evaluated when the camera moves more than about 10 m. Measure distance from the closest point of the bounds, not the centre, and keep the threshold well above the shadow distance (default 100 m, min 2.5 × shadowDistance), so tall proxies can still cast long shadows into view at low sun angles. Scope cameras don't need special handling: shadow distance is measured from the rendering camera's position, so distant scoped targets are outside it anyway.

## 2026-10-18: Verifying the first optimizations (SPTOptimizer.Optimizations 0.1.0)

### Shadow proxy culling: two F5 A/B runs at the same spot (Streets, 69,348; 14–16 players)
Captures `20261018-223524` (replicate) and `20261018-223906` (requested run). Baseline is the game **with** culling on. The `ShadowProxyCullingOff` step turns it off.

| Run | Frame Δ when culling is off | Cull Δ | Render Δ | Shadow casters Δ |
| --- | --- | --- | --- | --- |
| 223906 | **+0.60 ms** | +0.25 | +0.04 | 0 |
| 223524 | **+0.84 ms** | +0.38 | +0.25 | +6 |

- **Saves 0.6–0.8 ms per frame here** (about 7–9% of an 8.8 ms frame). It was about 1.0 ms at the earlier, denser spot. The shadow casters actually drawn are unchanged, so no shadow is lost.
- `FarShadowsOnlyOff` now does almost nothing (−0.04/−0.05 ms), so the plugin already covers what that experiment did.
- In run 223906 the **sun shadows went away at t≈160 s** (weather; shadow casters dropped to 0 and draw calls fell by about 900). The `FarVertexLightsOff` segment straddles that change and is invalid in that run. The culling step and its neighbouring Baselines were all in the no-shadow period, so that comparison holds. Culling still saved cull time with sun shadows off.
- Other experiments at this lighter spot: LodBiasLow −0.6/−0.8 ms (mostly render thread), FarVertexLightsOff −0.25 ms cull (consistent in both runs), AllShadowsOnlyOff/ShadowsOff −0.2 to −0.4 ms.
- **Visual check passed (2026-10-18):** a sunny daytime Streets walk (weather forced to SUNNY on the test install), toggling culling in F12, showed no visible difference. On this raid the scan found 173,074 static proxies in 602 grid cells (362 ms, once).

### GC policy: two F9 raids on the same Streets route (~600 m, 22–23 players, looting several times; culling off in both)
| | Policy on (`224926`) | Policy off (`225633`) |
| --- | --- | --- |
| Collections | 10, **all while a menu was open** | 13; they line up with the looting moments, plus about 3 others (t=75, 155, 160) |
| Collection frame time | 87–99 ms | 108–139 ms (heap 3.6 GB vs 3.1 GB) |
| Frames > 100 ms | **0** | 14 |
| Frames > 50 ms | 22 (all at inventory open) | 34 |
| p99.9 | 52 ms | 87 ms |
| Avg frame | 11.84 ms | 13.68 ms |

- The policy works as designed: the GC only runs while the inventory is open, and the gameplay collections are gone. A collection is still a ~90 ms frame, but it lands on the inventory screen.
- The 1.4–1.8 ms average difference **can't be attributed** from these runs. Camera cost differed by +0.37 ms (247 more draw calls on the off run's path) and Update scripts by +0.65 ms (one more player alive). Whether having the GC on costs something every frame (incremental marking) needs a controlled A/B: an experiment step that turns the GC on while standing still.
- Profiler fix: camera slots are now keyed by name, so they no longer split as `FPS Camera#2`/`#3` across raids.

## 2026-10-18: Player animation and IK, how EFT does it (from the decompile)

- **Every alive bot has two Unity Animators updated natively** (`Player.Create(..., Auto, Auto, ...)` in `LocalGame`): the body animator (`Player_anim_controller`, 22 layers) and the weapon/arms animator (5–7 layers). Both run in `PreLateUpdate.DirectorUpdateAnimationBegin`. BSG's own faster alternative (`UseBodyFastAnimator`, a playable-based animator, and `UseHandsFastAnimator`) is off in this config.
- **Culling modes** are set each frame in `Player.VisualPass`: within `EFTHardSettings.AnimatorCullDistance` = 10 m both animators are `AlwaysAnimate`, even off-screen. Beyond that they're `CullUpdateTransforms` (arms stay AlwaysAnimate with empty hands, knife or usable items). `CullUpdateTransforms` still runs state machines and curves; it only skips IK, retargeting and writing transforms when not visible.
- **Footsteps come from the body animator's `FootStep` curve** (`Player.Single_0`), so off-screen bots' animators must keep evaluating curves for their footsteps to be heard. `CullCompletely` would silence them, so it's only usable beyond hearing range.
- **IK** (`VisualPass`): procedural weapon animation, FBBIK (iterations = clamp(15/distance, 0, 2)), limb IK (skipped beyond 70/300 m), head and hands. It runs only for bots that are **visible** (body or feet renderer visible to any camera, shadow maps included) **and within `CULL_GROUNDER` = 40 m**.
- **EFT's own bot render culling** (`LocalPlayerCullingHandlerClass` → `GClass917`/`GClass999`, via `ObservedCullingManager`): a bot counts as visible within 30 m (`CULLING_PLAYER_DISTANCE`), or if its autocull visibility check passes and it's inside the camera view. Bullets passing nearby also keep it visible. After 5 s hidden, its renderers are switched off (`forceRenderingOff`), which also turns off its IK and culls its animator transforms.
- `Player.Sleep` (bot standby) is an empty method in this build. Standby doesn't reduce animation cost.

**Corrections from runtime data:** the `EFTHardSettings` asset overrides the code defaults: **`CULL_GROUNDER` = 90 m** (IK distance) and `CULLING_PLAYER_DISTANCE` = 40 m.

## 2026-10-18: Animation and IK measured (Streets, standing still, 21 bots, 2 on screen)

Captures `20261018-232725` (F5 animation A/B) and `20261018-233035` (F8 instrumented). Baseline is 10.7 ms per frame. Census: all 21 bots had body and arms animators in `CullUpdateTransforms`. Only 2 were on screen (53 and 57 m); 19 were off-screen at 145–1,120 m, most with renderers forced off by EFT's culling.

| Experiment | Frame Δ | Animation Δ | LateUpdate Δ | Verdict |
| --- | --- | --- | --- | --- |
| IkDistance20 (90 → 20 m) | −0.66 | −0.09 | −0.33 | IK for 2–4 bots |
| IkOffBots | −0.07 | −0.09 | −0.16 | IK is small |
| AnimCullTransformsAll | +0.30 | +0.14 | +0.04 | nothing to cull (no bots within 10 m); measures the noise floor, ~±0.3 |
| AnimCullCompletelyOffscreen | −1.99 | −1.44 | −0.57 | **invalid: froze bots**, see below |
| AnimCullCompletelyFar (>60 m) | −2.23 | −1.44 | −0.77 | **invalid: froze bots** |

**Bots move by animator root motion.** `MovementState.ProcessAnimatorMovement` takes the body animator's `deltaPosition`, `MovementContext.ApplyMotion` schedules it, and `AnimatorStatesLateUpdate` → `DirectApplyMotion` → `CharacterController.Move` applies it. So `CullCompletely` on an off-screen bot stops its movement and footsteps (the `FootStep` curve), not just its visuals. The "LateUpdate savings" in those two rows are the frozen bots' `CharacterController.Move` calls. **CullCompletely can't be used as an optimization.** What those rows do show: **off-screen bots' animators cost about 1.3 ms (≈65 µs per bot) of animation update**, even in `CullUpdateTransforms`, because EFT needs their root motion and curves.

**Per-player script costs (instrumented, 24 `Player.LateUpdate` calls per frame):**
| Method | ms/frame | per call | What it is |
| --- | --- | --- | --- |
| `MovementContext.AnimatorStatesLateUpdate` | 0.64 | 27 µs | applies root motion via `CharacterController.Move` |
| `Player.method_91` (ComplexLateUpdate) | 0.42 | 17 µs | `HandsController.EmitEvents` + `BallisticUpdate` |
| `Player.ManualUpdate` (self) | 0.35 | 16 µs | movement state machine |
| `Player.VisualPass` incl. IK | 0.28 | — | FBBIK ~21 µs and limb IK ~16 µs per visible bot (3 per frame) |

So per-player work is ≈1.9 ms of scripts plus ≈1.8 ms of native animation. IK is a minor part. The big lever is **the animation of off-screen bots**, which can't simply be culled because movement and footsteps depend on it.

**BSG already built the fix: the "fast animator"** (`ApplicationConfigClass.UseBodyFastAnimator`, `FastAnimatorSystem.PlayableAnimator`):
- The body state machine runs in managed code (`FastAnimatorProcessorClass`).
- Root motion comes from a precomputed `RootMotionBlendTable`, so no clip evaluation is needed for movement.
- The visual PlayableGraph is stopped while the bot is off-screen (`GClass1340` culler), except during prone transitions or important parameter changes.
- It's used for observed players in online games (`NetworkGame`), and the data ships in the client (`player_anim_controller.animatorcontrollerdata.bundle`, which is preloaded, and `playerrootmotiontable.asset.bundle`, 41 MB, which is not).
- The config flag is built from defaults plus the launcher's `-config` patch in `CommonClientApplication.ConfigureApplication`, so it's off in SPT.
- Next: an opt-in experimental switch for local raids (game restart required). A/B it by raid, and check that bots move, animate and play footsteps.

### Fast animator test, attempt 1 (2026-10-18): didn't engage; both runs are baselines
Captures `234617` and `235130`, two raids in one game session at the same spot (−18, 305). Both censuses show `UseBodyFastAnimator False` and the standard animator wrapper (`GClass1446`) for every player. The setting was most likely toggled in F12 without a game restart. Profiler/plugin now log the fast-animator state in every raid and warn when the setting changes.

**Same-spot, different-raid baseline (fast animator off):**
| Capture | Frame | Animation | LateUpdate | Update | Cull+Render | Bots alive / visible / IK |
| --- | --- | --- | --- | --- | --- | --- |
| 234617 | 10.15 | 1.65 | 1.77 | 2.19 | 3.18 | 21.1 / 2.8 / 1.3 |
| 235130 | 10.99 | 2.24 | 2.07 | 2.07 | 3.24 | 23.0 / 1.0 / 0 |

Raid-to-raid noise at the same spot is about **±0.8 ms frame and ±0.6 ms animation** (bot count and activity differ). A cross-raid A/B needs an effect well beyond that; compare animation per alive bot too (78 vs 97 µs here).

### Fast animator test, attempt 2 (2026-10-19): works, but slower under Mono
Capture `235841`, fresh game launch with the option on, same spot (−19, 306), 21 bots.
- **It engaged cleanly.** The flag was set at `ConfigureApplication`, the root-motion bundles were preloaded, and all 23 players got `FastAnimatorProcessorClass`. Neither LogOutput.log nor Player.log has any exceptions, and there are no animator or playable warnings. The "referenced script is missing" bursts at raid unload also appear without it (82 in the previous session vs 78).
- **Performance is worse:**

| Same spot | Frame | Native animation | Update scripts | LateUpdate | p99 |
| --- | --- | --- | --- | --- | --- |
| Off (2 raids) | 10.15 / 10.99 | 1.65 / 2.24 | 2.19 / 2.07 | 1.77 / 2.07 | 14.5 / 13.4 |
| **On** | **12.58** | **0.22** | **5.36** | 2.10 | 18.9 |

  Native animation almost vanishes (−1.4 to −2.0 ms), but the managed body state machine (`FastAnimatorProcessorClass.Update` via `Player.BodyUpdate`) adds about **+3.2 ms of Update scripts, ≈150 µs per bot per frame**. That's about twice the native cost (65–95 µs per bot). It doesn't scale with visible bots (Update was 6.5 / 4.8 / 5.8 ms at 5 / 6 / 7 visible), so it's paid for every bot every frame. Allocations rose slightly (3.6 vs 3.1 MB/s).
- **Verdict:** under EFT's Mono runtime, BSG's fast animator is a net loss of ~1.6–2.4 ms per frame on Streets. Keep it off. (It may pay off for BSG where observed players don't need root motion, or in a different runtime.)

### Where animation/IK stands
- IK: ~0.2–0.3 ms. Not worth optimizing.
- Native body/arms animation of off-screen bots: ~1.3–2.2 ms. It can't be culled, because movement (root motion) and footsteps depend on it, and BSG's alternative is slower.
- Per-player scripts: ~1.9 ms (`CharacterController.Move` via root motion 27 µs, `HandsController.EmitEvents`+`BallisticUpdate` 17 µs, `ManualUpdate` 16 µs per player). Remaining candidate: update-rate LOD for these per-player script parts on far off-screen bots, which needs careful handling of accumulated motion.

## 2026-10-19: Remaining no-impact candidates (code investigation)

- **AreaLight camera hooks.** Each of ~1,170 enabled `AreaLight`s subscribes its own `method_2` to `Camera.onPreCull`. Per camera each one checks intensity, calls `CompareTag` twice (native) and, if lit, rebuilds its command buffer. A single dispatcher (tag check once per camera, then the same per-light logic in the same order) gives identical output. Prototype: profiler experiment `AreaLightDispatch`; light enable/disable is mirrored via Harmony postfixes on `AreaLight.OnEnable`/`OnDisable`, so there are no per-frame native checks. Expected ≤0.2 ms.
- **JobScheduler hitches (15–35 ms).** `Diz.Jobs.JobScheduler` (in LateUpdate) runs queued async continuations (bundle loading, pool and object creation, mostly when bots spawn) while the frame is under budget. The budget is 1000 / FPS-limit setting (≈8 ms at 120). It force-runs work after 6 slow frames. A spike is one continuation that can't be split. Its built-in continuation profiler is compiled out (`[Conditional("DEBUG")]`).
- **New: slow-work logger** (profiler, automatic in every capture). It times each single JobScheduler job (`Class1031.Execute`, with job and stage name), async continuation (`UnitySynchronizationContext.WorkRequest.Invoke`) and coroutine step (`SetupCoroutine.InvokeMoveNext`), and names the async method, coroutine or job behind any item over 2 ms in `slow_work.txt`/`.csv`. This is the next step for hitches: find out exactly what they are before deciding whether any is avoidable.
- **Top steady-state scripts** (`CullingManager`, `AmbientLight`, `MuzzleSystem`, `StaticManager`, `AbstractApplication.Update` → `EFTPhysicsClass.Update`): thin dispatchers over already-batched systems. No cheap safe win found.
- **GC on vs off per frame:** profiler experiment `GcOn` turns the GC on during its step and pauses the GC policy.

### Test plan (one Streets raid, clear daytime, game freshly launched)
1. Stand still at a fixed busy spot and press **F5**. The sequence is now `AreaLightDispatch, GcOn, FarVertexLightsOff` (7 steps × 15 s ≈ 1¾ min). Don't move the camera. → A/B table in `summary.txt`.
2. Press **F8** (instrumentation, now also counting physics queries), then **F9**. Play normally for 2–3 minutes: move around, let bots spawn and fight, loot once. Then **F9** → `slow_work.txt` (hitch sources) and `methods.csv` (physics query counts and time).
3. Optional visual check for far vertex lights: at night, use **F6** to cycle to `FarVertexLightsOff` and back while looking at distant lamps.

### Results (captures `20261019-092017` F5 A/B, `20261019-092351` instrumented, 4 min of play)
**A/B (paired, standing still):**
| Experiment | Δ frame | Δ cull | Verdict |
| --- | --- | --- | --- |
| FarVertexLightsOff | −0.30 | **−0.25** | Consistent across 3 runs (−0.25/−0.35/−0.25). Ship it once a night visual check passes. |
| GcOn | +0.09 | +0.01 | An enabled but idle GC costs nothing per frame. The GC policy's value is purely in avoiding hitches. |
| AreaLightDispatch | −0.04 | −0.004 | No measurable gain; the earlier estimate was mostly instrumentation overhead. Dropped. |

**Hitch attribution (250 frames > 25 ms over 240 s; p99 29 ms):**
- **Physics simulation bursts: the main hitch source.** EFT's top-level physics update (`AbstractApplication.Update` → `EFTPhysicsClass.Update` → `Physics.Simulate`, script mode) only runs while supported rigidbodies (ragdolls after kills, dropped items) are active. There were 10 bursts of 1–7 s and 1,029 frames with a 2–20 ms simulation step (avg 3.5 ms); frames in a burst averaged 18.2 ms vs 13.0 ms overall. It was the top method in 164 of the 250 hitch frames.
- Slow single work items (logger, 86 over 2 ms, ~480 ms in total):
  - a `GClass7+Class92` coroutine, 5 times at up to 35 ms
  - item-related async jobs (`GClass924<Item,…>`), 31 times at ~5 ms
  - first-time pool object creation for loot (armbands, glasses, cases), 11–13 ms each
  - `Player`/`InventoryScreen` coroutines, 3–14 ms
  - `FlyingBulletSoundPlayer`, 12 times at ~3.4 ms
- Also: `InputManager.Update` peaked at 123 ms (UI), `JobScheduler` at 17.7 ms.
- The async-continuation hook failed to compile (Harmony can't box the private `WorkRequest` struct), so `ScriptRunDelayedTasks` items aren't named yet.

**Physics queries on the main thread (instrumented): ~2.3 ms/frame.**
| Query | Calls/frame | ms/frame | µs/call |
| --- | --- | --- | --- |
| `Physics.OverlapSphereNonAlloc` | 126 | **1.80** | 14.3 |
| `Physics.Raycast` (all overloads) | ~55 | ~0.28 | 2–7 |
| `Physics.RaycastNonAlloc` | ~28 | ~0.15 | 5 |
| Linecast, SphereCast, OverlapBox | <6 | ~0.05 | — |

Almost all of the overlap calls come from **EFT's `SimpleCharacterController.Move`**. Every player's move runs one `OverlapSphereNonAlloc` per character collider per movement sub-step to find neighbouring colliders (`method_7`), plus a ground raycast. That's ~5 per player per frame, at 14 µs each because the scene is dense with colliders.

**Multi-threading candidate (concrete):** every player's motion for the frame is known before LateUpdate applies it (`ProcessAnimatorMovement` schedules it in Update; `AnimatorStatesLateUpdate` → `Move` applies it). A pre-pass at the start of LateUpdate can run all players' first-step neighbour queries as one `OverlapSphereCommand` batch (Unity 2022.2+) on the worker threads. `method_7` would then use the precomputed result when its query matches exactly. Same queries, same colliders; the only difference is result order, which could change depenetration order slightly (practically invisible). Potential: most of 1.8 ms → a few tenths of a ms.

### Batched overlaps prototype: test steps
Built in the optimizations plugin, off by default. Profiler F5 sequence: `BatchedOverlaps` × 3 repeats (7 steps × 15 s).
1. Streets, daytime. Stand still somewhere with bots moving nearby and press **F5** (~1¾ min). → A/B table in `summary.txt`. Look at the Update/LateUpdate slots and the frame delta.
2. Then F12 → *Batched overlaps (prototype) → Enabled* on, and play 1–2 minutes. Watch for movement oddities: stairs, slopes, tight doorways, crouch/prone under obstacles, bumping into bots, vaulting. LogOutput.log gets a `Batched overlaps:` stats line every 15 s (served vs fallbacks, pre-pass ms).
3. Switch it off again afterwards (it's a prototype).

## 2026-10-19: Feasibility of multi-threading and GPU offload

**Thread budget measured (Streets):** main thread 10–14 ms, render thread 4–5 ms, GPU 5–6 ms. The render thread and GPU have ~50%+ headroom, so only moving work **off the main thread** helps.

**What the main thread spends its time on, and whether it can move:**
| Main-thread block | ms | Already parallel inside Unity? | Can a mod move it? |
| --- | --- | --- | --- |
| Camera culling | 1.4–2.2 | Yes, culling is jobified; the main thread waits | No. Can only be reduced by giving it fewer objects (done for shadow proxies) |
| Camera render setup | 1.7–2.2 | Partly (graphics jobs "legacy" on DX11; "native" needs DX12, which EFT doesn't ship) | No |
| Native animation | 1.5–2.2 | Yes, animation jobs; the main thread waits | No (and the managed alternative was slower) |
| Per-player scripts (`CharacterController.Move`, animator reads, hands events, IK) | ~1.9 | No | Mostly no: built on main-thread-only Unity APIs (transforms, animators, CharacterController) |
| Physics queries (raycasts, casts, overlaps) | unknown (now instrumented) | Not when called one at a time | **Yes, if numerous:** batch with `RaycastCommand`/`SpherecastCommand` jobs (EFT already does this for overlap boxes: `PhysicsCustomOverlapBoxSystem`) |
| Other managed scripts | ~2 | No | Only pure-math parts. EFT code mixes logic with Unity API calls, so splitting is per-system work |
| Async/job hitches | spikes | No | Partly: work that's pure data (deserialization, computation) could run on a worker. Object instantiation can't (main thread only) |

**GPU offload:** little to gain. The CPU cost isn't computation that maps onto the GPU (skinning is already 0.03 ms, post-processing already runs on the GPU). It's object management: culling ~200k renderers, setting up ~3,600 draw calls, and scripts. GPU-driven rendering (GPU culling and indirect draws, like EFT's GPUInstancer does for vegetation) would mean rebuilding how the level's static geometry is rendered, which isn't realistic from a mod.

**Burst / Jobs from a mod:** Unity's Job System is available to mods (plain C# jobs run on the 15 worker threads under Mono). Burst would need precompiled native code loaded at runtime (`BurstRuntime.LoadAdditionalLibrary`); possible but heavy tooling. Only worth it for a clearly identified, data-parallel hotspot.

**Verdict:** a general "multi-thread the game" isn't feasible. The big main-thread blocks are either Unity-native work that's already multithreaded internally (culling, animation, rendering) or managed code bound to main-thread-only Unity APIs. Targeted moves are feasible if a measurement finds a large, data-parallel chunk. The best candidate is batching many individual physics queries; the instrumented run in the test plan will show whether that chunk exists.

## 2026-10-19: Batched overlaps, first results (captures `20261019-094812` F5 × 3, `20261019-095035` play with it on)

**A/B (standing still, Streets, 3 repeats):** frame −0.41 ms, main thread −0.41 ms. The LateUpdate slot, where the controller moves run, is the clean signal. With it on, LateUpdate was 1.56, 1.61 and 1.56 ms; Baseline segments around them were 1.85, 1.70, 1.89 and 2.06 ms. That's **−0.25 to −0.35 ms every time**. The Update slot also dropped (−0.18 ms), but the first Baseline segment there was an outlier (2.8 ms).

**Coverage (LogOutput.log stats):**
| | Batched spheres / frame | Controller queries / frame | Served from batch | Fallback "outside" | Pre-pass |
| --- | --- | --- | --- | --- | --- |
| Standing still (A/B steps) | 16 | 25–60 | 100% | ~0 | 0.06 ms |
| Playing (2 captures' worth) | 21–24 | 96–320 | 87–100% | 0–63 in some 15 s windows | 0.07–0.08 ms |

There were no truncated results and no queries for moves the pre-pass hadn't seen. The first window's 1.16 ms pre-pass is one-time JIT and allocation. The "outside" windows were all during play, likely jumps, vaults or the player's own moves outside the scheduled motion. They fall back to the original query, which is the intended behaviour. No movement oddities were reported.

**Open questions before calling it an optimization:**
- **Query rate.** Controller queries per frame rose steadily, 25 → 320, over the session. The count is colliders × (sub-steps + 1) per `Move`, so a rise means more sub-steps (more bots moving, or moving faster). It could also mean a behaviour change. There's no count with the prototype off to compare.
- **Semantics (from the decompile).** The served set is a superset of the original query:
  - It causes more `ComputePenetration` calls. Non-touching colliders are cheap rejects.
  - `method_11` (the "seep" list used when a character feels tight) walks **every** neighbour, touching or not. So a nearby non-touching collider can join the seep list. `method_6` removes it again on the next pass where it doesn't overlap.
  - Other players move during LateUpdate, after the pre-pass. The 0.15 m margin covers per-frame moves of up to 0.15 m.

**Added:** *Batched overlaps (prototype) → Validate*. For every served query it also runs the original query and logs any collider the batch missed (expected: 0) plus the average number of extra colliders. With the prototype off and Validate on, it logs the original query rate, for comparison.

## 2026-10-19: FIKA headless client, would it help here?

FIKA's headless client is a second EFT instance that hosts the raid: it runs all bot AI and bot movement. The game you play becomes a network client. What this machine would offload, using the instrumented Streets run (`20261019-092351`, 21 bots, vanilla AI):

| Host-only work | ms / frame | Notes |
| --- | --- | --- |
| Bot AI (`BotsClass.UpdateByUnity` + `AICoreControllerClass.Update`) | ~0.45 | Vanilla AI is cheap. SAIN-style AI mods cost several times more |
| Bot movement (`Player.ManualUpdate`) | ~0.4 | |
| Character-controller overlaps | ~1.8 (~1.5 after batching) | ~95% bots |
| Raycasts (AI vision, ground checks) | ~0.4 | |
| AI hitches | spikes | `AICoreControllerClass.Update` 10–18 ms spikes, job-scheduler bot work |
| **Total** | **~2.5–3 of ~10.3 ms main thread** | |

**What stays on the client:**
- Animation and IK of every visible or nearby bot. `Player.LateUpdate` is 1.6 ms, plus native animation.
- All rendering and culling. This is the largest part of the frame.
- Network receive and interpolation of ~20 observed players. Cost unknown; FIKA does this in managed code.

**Estimate:** ~15–25% more FPS on this PC in a CPU-bound Streets raid (95 → ~110–120 fps). The bigger wins are fewer AI hitches and more headroom with more bots. FIKA's quoted 25–50% fits weaker CPUs or heavier AI mods.

**Same-PC headless:**
- The 7800X3D has spare cores (the game keeps ~2–3 busy), so a local headless gives the bots their own main thread. That's real parallelism, which no in-process mod can do.
- Costs:
  - RAM: another ~10–16 GB for Streets. This install already uses ~23 GB resident.
  - The two instances share the 96 MB L3 cache, which costs some of the gain.
  - Setup complexity.
- A separate PC gives the full gain.

**Interaction with this project:**
- The optimizations plugin should run in the headless instance too. GC hitches there freeze the whole raid for everyone (rubber-banding).
- On the client, bots are network-driven, so the batched-overlap work mostly moves to the host.
- Everything else (shadow proxy culling, GC policy) stays useful on the client.

**Verdict:** worthwhile, and the only way to put bot simulation on another core. But it's a deployment change, not a mod optimization. It can be measured with the profiler on a separate FIKA install: same Streets spot, F9 for 60 s, solo raid vs local-headless raid.

## 2026-10-19: Graphics settings, does "higher = faster" hold?

**Current settings (SPT keeps its own copy in `SPT\user\sptSettings\Graphics.ini`, not the AppData one):**
- 2560×1440, frame cap 144, Reflex Off
- Shadows Medium, textures Medium, visibility 1000, object LOD 2 (minimum)
- SSAO Fastest, SSR Low, TAA Low, sharpen 0.6
- Mip streaming off, volumetric light off

**Thread budget on Streets:** main thread 10.3 ms, render thread 4.8 ms, GPU 5.2 ms. The game is **CPU-bound by about 2×**. GPU-only settings (resolution, AA, SSAO, SSR, texture quality) shouldn't change frame time until the GPU passes ~10 ms.

**What could make "higher settings = more FPS" true** (all checkable from the decompile or by measurement):
1. **No setting moves CPU work to the GPU.** EFT has no "GPU culling" or similar toggle. The CPU-heavy settings work the other way: visibility (camera far clip), shadow quality (30/40/60/80 m shadow distance, more casters to cull and draw) and object LOD (`QualitySettings.lodBias`) add CPU work as they go up.
2. **GPU power states.** A GPU at ~50% load runs at lower clocks. Wherever the CPU waits on the GPU, slower clocks lengthen the wait: present, the render-thread sync, and readbacks such as EFT's Hi-Z occlusion (`GPUInstancerHiZOcclusionGenerator`). Higher settings, or Reflex Boost, keep clocks up. This is the most plausible real mechanism.
3. **Reflex and the frame cap.** In raid, EFT ignores the frame cap when Reflex is on (`targetFrameRate = -1`) and applies it when Reflex is off (`GameGraphicsClass.ChangeFramerate`).
4. **Mip streaming.** On adds per-frame CPU work (Unity's texture streaming). Off costs VRAM, which a 24 GB card has. It's already off here.

**Test built (profiler F5, now the default sequence):** 11 graphics steps, each switched through EFT's own settings objects, the same path as the settings menu, and restored afterwards:

| Step | Change | Tests |
| --- | --- | --- |
| GfxRender50 / GfxRender200 | render scale 0.5× / 2× pixels | how CPU-bound the game is; whether GPU load or clocks change frame time |
| GfxShadowsHigh / GfxShadowsUltra | shadow quality Low → High / Ultra | CPU cost of more shadow distance |
| GfxVisibility1500 | visibility 400 → 1500 | CPU cost of the far clip |
| GfxLodBias4 | object LOD 2.5 → 4 | CPU cost of higher-detail LODs |
| GfxMipStreamingOn | mip streaming on | CPU cost of texture streaming |
| GfxTexturesLow | textures High → Low | should be ~0 |
| GfxPostFxOff | SSAO, SSR, AA and sharpen off | GPU post-processing; should be ~0 for frame time |
| GfxReflexOff / GfxReflexBoost | Reflex On → Off / On + Boost | the Reflex, frame cap and clock effects |

Each step runs 12 s, with Baseline in between (23 steps, ~4.6 min). The experiments don't save settings (the SPT `Graphics.ini` was unchanged afterwards).

### Test steps (one Streets raid, clear daytime)
1. **Graphics.** Stand still at a busy spot and press **F5** (~4.6 min). Don't open menus. The screen visibly changes resolution and shadows between steps.
2. **Batched overlaps check.**
   1. F12 → *Batched overlaps (prototype)*: Validate on, Enabled off. Play ~1 min. The log gets the original query rate.
   2. Turn Enabled on and play 2–3 min the same way: stairs, doorways, near bots, jumping and vaulting. The log gets `Batched overlaps validation:` lines plus any `MISMATCH` warnings.
   3. Turn both off afterwards.

## 2026-10-19: Results, graphics A/B and overlap validation (capture `20261019-113652`, Streets, standing still)

### Graphics settings
Each step is one 12 s segment, compared with the Baseline segments either side. `GfxReflexOff` was a no-op here because Reflex was already off. That makes it an A/A test: **+0.17 ms is the noise floor**. Animation time also swings ±0.35 ms between segments as bots move.

| Step | Frame Δ ms | Main thread Δ | Render thread Δ | GPU Δ | Draws Δ | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| GfxRender50 (½ pixels) | +0.05 | +0.04 | +0.07 | −0.16 | −11 | no change: CPU-bound |
| GfxRender200 (2× pixels) | −0.34 | −0.35 | +0.02 | **+2.51** | −4 | free (GPU 4.7 → 7.2 ms, still under the CPU); the −0.34 is animation noise |
| GfxShadowsHigh (Medium → High) | **+0.51** | +0.50 | +0.18 | +0.08 | +179 | real CPU cost: casters 184 → 374 |
| GfxShadowsUltra | **+0.68** | +0.68 | +0.29 | +0.21 | +246 | real CPU cost: casters → 574 |
| GfxVisibility1500 (1000 → 1500) | −0.23 | −0.23 | 0 | 0 | 0 | free on Streets (draws unchanged) |
| GfxLodBias4 (2 → 4) | **+0.92** | +0.88 | **+2.37** | **+1.89** | **+1,611** | most expensive: higher LODs mean many more draw calls |
| GfxMipStreamingOn | **+0.97** | +0.96 | 0 | 0 | 0 | real CPU cost (Update +0.49 ms); keep off |
| GfxTexturesLow (Medium → Low) | −0.03 | −0.03 | −0.05 | −0.05 | −27 | nothing |
| GfxPostFxOff (SSAO, SSR, AA, sharpen) | −0.55 | −0.55 | −0.02 | −0.28 | +4 | ~0 real: the delta is animation noise (−0.38) |
| GfxReflexOff (no-op, A/A) | +0.17 | | | | | noise floor |
| GfxReflexBoost | −0.09 | | | | | nothing |

**Conclusions:**
- The belief "higher settings give more FPS" isn't borne out here. No setting moved CPU work to the GPU, and no "higher" setting made frames faster beyond noise.
- What is true: GPU-side settings are **free while CPU-bound**. Doubling the pixels cost nothing (GPU 7.2 ms, still under the 10 ms main thread). So on this PC, resolution, AA, SSAO, SSR and textures can go up at no FPS cost until GPU time approaches main-thread time. That's probably where the belief comes from: raising GPU settings doesn't lower FPS, and per-run noise of ±0.5 ms makes some runs look faster.
- The CPU-relevant settings:
  - Object LOD: keep at 2, the minimum. 4 costs +0.9 ms of main thread, +2.4 ms of render thread and 1,600 draw calls.
  - Shadow quality: Medium (+0.5 ms for High, +0.7 ms for Ultra).
  - Mip streaming: keep off (+1 ms when on).
  - Overall visibility: free between 1000 and 1500 on Streets.
- Not tested: Shadows Low vs Medium, and visibility 400 vs 1000. Each step was a single 12 s segment, so only the large deltas (≥ 0.5 ms, with matching draw or caster changes) are trustworthy.

### Batched overlaps: validation passed
- **~6 million served queries checked, 0 colliders missed.** On average the batch returns 1.0 extra collider per query.
- The original query rate with the prototype **off** was 240–470 per frame. So the rising query count isn't caused by the prototype.
- Over this session the rate kept growing: 131 → 345 per frame during the A/B, and up to **770 per frame** later, with ~19–23 moving colliders. That's 15–40 queries per collider per frame. A normal move takes 1–2 sub-steps of 0.3 m.
- **Leading hypothesis: a player falling through the map.** The count rises roughly linearly with time, which fits gravity steadily speeding up a falling body: more sub-steps per frame, then `_maxCycleCountToHalt` = 1000 sub-steps before EFT gives up. Queries in empty space are cheap, which is why the frame time didn't explode.
- **Diagnostics added to the stats line:** the largest single move per period (who, role, position, movement state, grounded, velocity), moves over 1 m per frame, and `Move` calls per frame.
- Status: correct, with a measured −0.25 to −0.35 ms. **Ready to promote** after the query-growth question is answered. If a broken player is the cause, that's a separate fix (EFT bug), not a reason to hold the batching back.

## 2026-10-19: The query growth is the parked BTR gunner (diagnostics run, Streets)

The new stats line named it. The culprit is **`shooterBTR`, the bot that mans the BTR's turret**:
- Until the BTR arrives it is parked off-map at **(1000, −498, 1001)**, in movement state Idle and not grounded. Its fall speed grows without limit: 1,289 → 4,171 m/s over ~5 minutes of logs.
- Its position never changes, because something keeps holding it there; likely SPT's BTR code. But every frame `SimpleCharacterController.Move` is still asked to move it 16–135 m.
- It sub-steps that in 0.3 m steps, each with a neighbour query and depenetration pass. That's the 140 → 480 controller queries per frame. Every other bot's move is 0.02–0.4 m/frame, one step.
- When the BTR arrived and the gunner was placed at (285, 5, 219), queries dropped at once to **one per moving bot** (16–24 per frame).
- If the BTR never comes, the fall keeps growing until EFT's own cap of 1,000 sub-steps per frame (`_maxCycleCountToHalt`).
- `Move` calls per frame stayed at 23–26 throughout. Nothing calls `Move` extra; it's only the size of this one move.

**Fix: runaway move guard** (optimizations plugin, on by default):
- A prefix on `SimpleCharacterController.Move` skips any move faster than 200 m/s. Sprinting is ~7 m/s and long falls ~30 m/s, so nothing legitimate comes close.
- For the parked gunner this is visually and behaviourally identical, since its position is held anyway.
- It also caps the cost of any other character that ever falls through the map.
- Each guarded character is logged once (`Runaway move guard: skipping moves of ...`).
- The batched-overlap pre-pass skips such moves too.

**To measure:** profiler F5 is set to `RunawayGuardOff` × 3, which turns the guard off for the step. Run it on Streets while the BTR hasn't arrived yet, ideally a few minutes into the raid, since the cost grows with time. The batched-overlap prototype is switched off for this, to measure EFT's own cost.

### Guard A/B, early in the raid (capture `20261019-140610`)
- The guard engaged on the parked gunner about 20 s into its fall (at 200 m/s), as designed.
- With the guard **off**, the cost was **noise-level**: frame +0.02 ms, LateUpdate +0.06 ms (3 repeats). This early, the fall is only 2–12 m per frame, i.e. 7–40 sub-steps.
- **When the BTR comes** (`SPT_Data\database\templates\locationServices.json`): Streets 100% chance, 300–600 s after raid start; Woods 100%, 180–360 s.
- So the gunner is parked at the start of every Streets and Woods raid. Its per-frame cost grows until the BTR arrives:
  - Streets: up to ~5,900 m/s after 10 min, i.e. ~60 m per frame and ~200 sub-steps at 100 fps. The previous session's log peaked at 135 m per frame.
  - Unmeasured at that size. Estimated at ~3 µs per sub-step, so ~0.5–1.5 ms per frame in the minutes before the BTR arrives.
- **To measure the peak:** start F5 about 4 minutes into a Streets raid, before the BTR shows up. The guard is free and has no effect on gameplay, so it stays on whatever the result.

### Guard A/B, ~4 min into the raid (capture `20261019-141700`): **up to −6 ms per frame**
| Segment | LateUpdate ms | Frame ms |
| --- | --- | --- |
| Baselines (guard on) | 1.54–1.72 | 8.9–9.6 |
| Guard off #1, #2 | 1.78 | 9.15–9.20 (+0.2–0.3 ms) |
| Guard off #3, first 7 s | 1.85 | 9.2 |
| Guard off #3, from 83 s on | **6.6–7.4** | **14.3–15.6** (~108 → ~65 fps) |

- The cost doesn't grow smoothly. It **steps up** once the gunner's per-frame fall gets long enough, here about 4½ minutes into the raid.
  - Likely cause: the swept path below the parking spot starts reaching geometry. Then every 0.3 m sub-step runs real depenetration (`ComputePenetration` against a mesh, up to 4 tightness iterations) instead of hitting empty space.
  - It stays at ~+5.5 ms per frame until the BTR arrives and the gunner is placed in the turret.
- In unmodded SPT this means **every Streets raid has a window from roughly 4–5 minutes until the BTR arrives (5–10 min) where the main thread loses ~5–6 ms per frame**. Woods likely has the same, at its 3–6 min BTR timing.
- With the guard on, the cost is zero for the whole raid.
- **Caveat for batched overlaps.** Its A/B (`20261019-094812`) ran while the gunner was parked, so part of the −0.25 to −0.35 ms may have come from batching the gunner's sub-step queries. Re-measure it with the guard on.

### Batched overlaps re-measured with the guard on (capture `20261019-142622`, Streets, 22–23 bots, standing still)
| Pair | LateUpdate, prototype on | LateUpdate, Baselines either side | Δ |
| --- | --- | --- | --- |
| 1 | 1.83 | 2.04 / 1.89 | −0.14 |
| 2 | 1.75 | 1.89 / 1.95 | −0.17 |
| 3 | 1.83 | 1.95 / 2.28 | −0.29 |

**−0.2 ms of LateUpdate on average, in 3 of 3 repeats.** The frame delta was −0.13 ms (within noise). That's smaller than the first measurement (−0.25 to −0.35 ms), which confirms the parked gunner's sub-steps inflated it. It's a real, validated gain (0 missed colliders in ~6 M checks), but a small one: ~2% of the main thread with ~22 bots. It scales with how many bots are moving.

**Decision (2026-10-19):** batched overlaps is **on by default**, with the toggle kept (*Batched overlaps → Enabled*, renamed from "(prototype)"). Profiler step `BatchedOverlapsOff` measures it.

## 2026-10-19: Combined gain measured directly (capture `20261019-144534`, Streets after the BTR arrived, 24–25 bots, standing still)

The `AllOptimizationsOff` step turns off shadow proxy culling, batched overlaps and the runaway move guard. GC policy stays on. The gunner was already in the BTR, so the guard contributed nothing here: this is the **steady-state** gain.

| Pair | Optimizations off | Baselines either side | Δ |
| --- | --- | --- | --- |
| 1 | 14.43 ms (69.3 fps) | 12.66 / 12.11 | +2.05 |
| 2 | 13.85 ms (72.2 fps) | 12.11 / 12.74 | +1.42 |
| 3 | 13.92 ms (71.9 fps) | 12.74 / 12.60 | +1.25 |

- **Combined: −1.57 ms per frame, 71.1 → 79.9 fps (+12%)**, consistent in all 3 repeats.
- Where it comes from:
  - camera cull −0.66 ms and render setup −0.37 ms (shadow proxy culling)
  - LateUpdate −0.15 ms (batched overlaps)
  - GPU −0.6 ms (more than seen before)
- This spot is heavier than the earlier ones: 12.5 ms frames, ~970 shadow casters, 25 bots. Shadow proxy culling saves more here than at the lighter spots (−0.6 to −0.8 ms there).
- The sum of the separate A/Bs predicted ~1.0–1.2 ms, so the direct measurement agrees or slightly exceeds it.
- **Not included above:**
  - The runaway move guard's 5–6 ms during the pre-BTR window (separately measured).
  - The GC policy's hitch removal: gameplay frames over 100 ms 14 → 0.

## 2026-10-19: Batch 1, test setup

**Framework:**
- Round-robin A/B: 2 experiments per Baseline, order rotated each repeat.
- Auto-start 120 s into Streets raids.
- A screenshot per step, diffed against Baseline shots (`shots/diff.txt`).
- Hitch columns in the report (p99, frames over 25 ms per minute) and the frame delta of every repeat.
- A 30 s `Stats:` counter line from the optimizations plugin.

**Candidates, in one F5 run** (6 × 3 repeats, 12 s steps, ~5½ min):
| Step | Kind | What decides it |
| --- | --- | --- |
| FadedLightCulling | optimization candidate, no visual impact by construction | frame delta; screenshot diff ≈ noise; `fadedLights.*` counters |
| FarVertexLightsOff | earlier experiment (50 m cut), for comparison | how much of its −0.25 ms the faded-only version keeps |
| GfxShadowsLow | setting: shadows Medium → Low | frame delta vs screenshot diff |
| LodBiasLow | trade-off: LOD bias 2 → 1 | frame delta vs screenshot diff |
| IkDistance40, IkDistance20 | trade-off: bot IK only within 40 or 20 m | frame delta |

**Physics diagnostic** (every raid, automatic): what keeps physics stepping (corpses, loot, the BTR), its cost, and corpse settle times.

### Test steps
1. Start a **Streets** day raid and walk to a busy spot within 2 minutes. At 1:50 a warning shows; stand still, without moving the camera, until "A/B sequence finished" (~5½ min). The capture stops by itself.
2. Then play normally for 10+ minutes: fight (kills make ragdolls), loot, and be around when the BTR arrives (5–10 min). Nothing to press. The physics stats go to LogOutput.log.

## 2026-10-19: Batch 1 results (capture `20261019-150921`, Streets, standing still, 0 bots visible)

Frame-level noise was high in this run: the spread between repeats was 0.5–1.6 ms, with Update swinging up to ±1.3 ms. So the verdicts use the per-camera columns, which are stable.

| Candidate | Cull Δ per repeat | Render Δ | Other | Verdict |
| --- | --- | --- | --- | --- |
| **FadedLightCulling** | −0.22, −0.26, −0.21 | ~0 | 4,644 faded lights held off | **−0.22 ms, consistent.** Matches the 50 m `FarVertexLightsOff` cut (−0.22 ×3), but with no visual change by design. Promote after the batch 2 screenshot check |
| FarVertexLightsOff | −0.22, −0.22, −0.23 | ~0 | | superseded by FadedLightCulling |
| LodBiasLow (2 → 1) | −0.12, −0.11, −0.10 | −0.14, −0.09, −0.12 | draws −410, render thread −0.4 ms | ~−0.23 ms main + −0.4 ms render thread; visual trade-off, so judge on screenshots (batch 2) |
| GfxShadowsLow | ~0 | −0.05 to −0.10 | casters −124, draws −108 | ~−0.1 ms. Not worth the shorter shadow distance; dropped |
| IkDistance40 / 20 | 0 | 0 | `bots_vis` = 0 all run | **untestable here**: no bot was visible, so their frame deltas (±1 ms) are noise. Needs a spot with bots at 20–90 m |

**Screenshot method, v1: failed.** Baseline-vs-Baseline differed on 14.5% of pixels, so no experiment could stand out. The causes were weapon idle sway, shading drifting as clouds and sun move over 12–36 s, foliage, and the `fps 3` counters.

**Replaced with v2:** inside each step's settle window it takes three shots ~0.7 s apart: experiment, a brief switch back to Baseline, experiment again. A pixel counts as a real change only if both experiment shots agree and differ from the Baseline shot between them. Frames during the switch-back keep the step's experiment tag, so segments don't split. Output: `shots/NN_<step>_exp.png`, `_base.png`, `_change.png`, `diff.txt`.

**Faded light culling fix:** `LampController` calls `TurnLights` every frame (it returns early unless the state changes). The lamp-switch postfix acted on every call (164k per 30 s). It now acts only on real switches, with a prefix recording whether the call switches.

**Physics diagnostics (play session after the A/B):**
- Each physics step costs **2–3 ms (max 7–60 ms)** regardless of what's active, so the cost is in *how long* physics stays on.
- **Raid start:** 650 loot bodies settle over ~11 s (43 ms max step). That's during loading, so acceptable.
- **~60 s of vehicle-only physics early in the raid:** 2 vehicle bodies, ~70 steps/s × 2 ms ≈ **2 ms per frame for nothing visible**. Which vehicle isn't known yet; the next run logs body names (`physics.vehicle[...]`).
- **During play:** physics was on 2–14 s of every 30 s, ≈ 0.05–0.6 ms per frame on average. It was kept on by corpses, single loot items and one unnamed "other" body (now logged by name).
- **Corpses settle fast:** 0.02–2.1 s each, with one 15 s timeout out of ~17. **A corpse-settle cap would gain almost nothing; dropped.**

### Batch 2 test (≈2 min)
Same as batch 1: Streets day raid, busy spot, stand still from the 1:50 warning until "A/B sequence finished". Ideally **type `fps 0` in the console first**, to hide the counters from the screenshots. Then play a few minutes; the physics names go to the log.

## 2026-10-19: Batch 2 results (capture `20261019-153229`, Streets, standing still, `fps 0`)

**Screenshot method v2 works.** The noise floor (Baseline steps) is **0.00%** "real" change.

| Candidate | Frame Δ per repeat | Cull Δ | Render Δ | Render thread | Visible change | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| FadedLightCulling | −0.17, −0.41, −0.46 | **−0.24 ×3** | 0 | 0 | **0.00%** (3/3) | **Promoted: on by default** |
| LodBiasLow (1) | −0.13, −0.01, −0.19 | −0.11 | −0.12 | −0.37 | **1.9%** (3/3): mid-distance trees thin out, debris at ~40–60 m vanishes | **Opt-in trade-off** (*Object LOD bias override*), off by default |

- **Faded light culling fix confirmed:** `lampSwitchWhileHeld` is gone, and 4,643–4,644 lights were held.
- **Physics:**
  - One "other" body was `ammo_belt_fake(Clone)`, a machine-gun belt (`MgBelt`).
  - A single loot item kept physics stepping for 4–7 s per 30 s at ~1.2 ms per step.
  - No vehicle-only phase in this raid, so the 60 s seen in batch 1 isn't in every raid.
  - Next diagnostic: `physics.allSleepingCalls`, which counts steps where every registered body was asleep, i.e. physics that could be skipped.

## 2026-10-19: Trade-offs (visible / gameplay), batch 3 setup

The user asked to also explore optional changes that affect the look or gameplay. The catalog, with costs, is in [Optimizations/README.md](Optimizations/README.md#trade-off-catalog-what-else-could-be-traded-measured-costs-on-streets).

**Biggest lever: bot count.** Each bot costs ~0.25–0.3 ms per frame all-in, so SPT's Streets cap of 24 → 16 would be about −2 to −2.5 ms. That's a server config choice (`bot.json` → `maxBotCap`) and is left unchanged.

**New prototype: distant bot animation at half rate**, built on how EFT drives bots:
- Root motion is applied from the animator's own `OnStateMove`, which doesn't fire on a paused frame, so no motion is lost or doubled.
- It's divided by `MovementContext.LastDeltaTime`, set in `ManualUpdate` before Unity's animation update; the prototype stretches it on double frames.
- Footsteps follow the sign of the `FootStep` curve.
- EFT itself pauses and resumes animators with `keepAnimatorControllerStateOnDisable` (stationary weapons).
- Validation: bot speed with the feature on vs off.

**Also added:** *Bot IK distance* option (visible trade-off) and the profiler steps `BotAnimHalfRate` and `GfxVisibility400`.

### Batch 3 test (~3¼ min A/B, auto-starts 2 min into a Streets raid)
Steps: `BotAnimHalfRate`, `GfxVisibility400`, `IkDistance20`, × 3 repeats.
1. **Stand-still A/B.** Type `fps 0`. Stand at a busy spot, ideally with a few bots moving within view at 20–90 m, which IK needs. Stand still from the warning until "A/B sequence finished".
2. **Half-rate play check.** In F12, turn on *Trade-offs (gameplay) → Distant bot animation at half rate* and play ~5 minutes. Watch distant bots: do they walk, run and turn normally, and do you hear their footsteps as they approach? Then turn it off. The log's `Stats:` lines carry the speed check.

## 2026-10-19: Batch 3 results (captures `20261019-155625`, `20261019-160715`)

- **BotAnimHalfRate: no valid measurement.** *Distant bot animation at half rate* was switched on in the config during both A/Bs (both captures' headers record `BotAnimLod on`). Baseline and experiment were therefore identical, and the deltas (≈0) mean nothing.
  - There is a hint from across raids, not a measurement: animation time in these runs (Baseline 1.47–1.65 ms, half-rate on throughout) is well below batches 1, 2 and AllOptimizationsOff (2.7–3.3 ms, half-rate off). Different raids and spots, so it needs the re-run.
  - **Framework fix:** steps that turn an optimization on (`BatchedOverlaps`, `FadedLightCulling`, `BotAnimHalfRate`) now hold it off for the whole sequence if the config has it on, so Baseline really is "off". The setting is restored afterwards, with a warning in the log.
- **GfxVisibility400 (1000 → 400): no gain.** Frame +0.08 / −0.17 ms (noise), draws −7 to −35, screenshot change 0.00% from this spot. Visibility isn't a CPU lever on Streets.
- **IkDistance20: untestable again.** No bot was visible at either spot. The earlier measurement (−0.3 to −0.7 ms with 2–4 bots on screen) stands; it's offered as the *Bot IK distance* option.
- Faded lights held: 4,660 in the first raid vs 1,321 in the second (different time of day or lamp states).

**Re-run:** `BotAnimHalfRate` × 4 (9 steps, < 2 min), with the config switched off.

## 2026-10-19: Half-rate bot animation, v1 measured: net zero (capture `20261019-162816`, 4 repeats, valid: `BotAnimLod off` in Baseline)

| Slot | Δ |
| --- | --- |
| `PreLateUpdate.DirectorUpdateAnimationBegin` (animation update) | **−0.955 ms** |
| `Update.DirectorUpdate` | **+0.954 ms** |
| `Update.ScriptRunBehaviourUpdate` | +0.225 |
| `PreLateUpdate.ScriptRunBehaviourLateUpdate` | −0.185 |
| frame | +0.05 (noise) |

- The skipped evaluations saved what was predicted (~1 ms for the off-screen bots).
- But **re-enabling an Animator re-initialises it** in `Update.DirectorUpdate`, which costs the same again. Pausing by `Animator.enabled = false` is a dead end.
- Screenshots: 0.02% change, the same as the noise floor (the affected bots are off-screen).

**v2:** skip by setting `cullingMode = CullCompletely` for the frame. Unity skips unseen animators with no re-initialisation, and EFT's `VisualPass` resets the mode every LateUpdate anyway.
- The catch-up frame only scales time for frames where we culled and **no root-motion callback came**. If Unity evaluated anyway (a visible shadow, say), nothing is scaled, so motion can never be added.
- On-screen bots are never touched.
- Validation counters now accumulate without per-frame string allocations: `botAnimLod.skippedUpdates`, `catchUpUpdates`, `evaluatedAnyway`, and on/off speed.

## 2026-10-19: Half-rate v2 (CullCompletely skip), first A/B (capture `20261019-164325`, 4 repeats, valid)

- **Animation update −0.51 to −0.57 ms in all 4 repeats**, and no `DirectorUpdate` penalty. LateUpdate −0.26 ms: half as many character-controller moves for the affected bots.
- **Frame: −0.94 and −0.70 ms in the two clean repeats.** In the other two, a large Update spike swamped it (2.91 and 4.01 ms vs Baselines of 2.05–2.42; one coincided with a bot spawn). The average of −0.38 ms is pulled down by those.
- `skippedUpdates` = `catchUpUpdates` (consistent), and `evaluatedAnyway` = 0.
- Screenshots: 0.07% change vs 0.06% noise floor, so no visible change from the player's view (only off-screen bots are affected).
- **Open concern: bot speed.** Affected bots averaged ~0.65 m/s with it on vs ~1.07 m/s off, in the same 30 s windows (on and off alternate every 12 s).
  - The controller does get the stretched time step (`ApplyMotion` → `ScheduledDeltaTime` = `LastDeltaTime`), so EFT's speed clamps aren't the cause.
  - It could be activity noise (which bots qualify when), or a real slowdown.
  - **Added:** root-motion vs displacement, bucketed by movement state (run, sprint, other), on vs off. Equal root motion with lower displacement would point at collision; equal both, at noise.
- Re-run with 5 repeats.

## 2026-10-19: Half-rate v2, 5-repeat A/B (capture `20261019-165316`): −1.1 ms, but bots run ~25% slower

**Performance: frame −1.21, −1.09, −1.12, −1.01, −1.05 ms (mean −1.09, spread 0.20), ≈ +11% FPS.**
- Animation update −0.75 ms, LateUpdate −0.13, Update −0.13.
- No `DirectorUpdate` cost.

**Behaviour: not acceptable yet.** Within the A/B window (on and off interleaved every 12 s), affected bots moved:
| State | On | Off |
| --- | --- | --- |
| Run | 1.86 m/s (159 bot-s) | 2.50 m/s (402 bot-s) |
| Sprint | 3.84 m/s (1.4 bot-s) | 5.45 m/s (32 bot-s) |
| Share of time in Run | 14% | 22% |

**Not the cause:**
- EFT never sets `Animator.speed` itself: `PlayerAnimatorSetSprintToIdleSpeed` has no callers.
- The controller receives the stretched time step.
- BotMover's stuck check is debug-only.

**Measurement bug found:** `RunStateClass` overrides `ManualAnimatorMoveUpdate` without calling the base. So the "evaluated this frame" detection and the root-motion reading never fired in Run (hence root = 0).

**Leading hypothesis:** an Animator resuming from a culled frame doesn't deliver the full two frames of root motion.

**Next measurement:** hook `PlayerStateContainer.OnStateMove` (Unity's callback, shared by all states), mapped to the bot through its Unity Animator. That gives correct "evaluated" detection and root-motion-per-second on vs off, per state. If root motion is lower with it on, Unity loses it on resume. If root motion is equal but displacement lower, the loss is in the movement pipeline.

## 2026-10-19: Half-rate v2, repeat (capture `20261019-172440`)

- **Frame: −0.86, −0.71, −0.84, −0.71, −0.47 ms (mean −0.72).**
- **Slowdown reproduced:** Run 1.82 vs 2.49 m/s, Sprint 3.83 vs 5.29 m/s.
- The new `OnStateMove` hook **failed to install**: Unity's `StateMachineBehaviour.OnStateMove` has two overloads, so Harmony's lookup was ambiguous. The other two patches were already in, so the feature ran, but root motion and "evaluated" detection read 0. Fixed by specifying the parameter types.
- `LimitMotionXZ` doesn't clamp speed (it only adds an overlap-prediction offset), so it isn't the cause.

## 2026-10-19: Half-rate v2, Factory A/B with working root-motion hook (capture `20261019-173830_factory4_day`)

- **Frame:** −0.21 ms on Factory (few bots; spread 1.46).
- **Root motion vs actual movement:**

| State | On: root / moved | Off: root / moved |
| --- | --- | --- |
| Run | 2.14 / 2.06 m/s | 2.48 / 2.50 m/s |
| Sprint | 5.19 / 4.37 m/s | 5.36 / 5.12 m/s |

- **Root motion itself is ~14% lower in Run**, and displacement follows it. So the animator produces less motion; EFT's movement pipeline isn't losing it.
- With half-rate on, more time is classified as "other" states (0.49 m/s vs 0.07), so bots spend more time in transitions.
- `evaluatedAnyway` = 64 of 13,136 skips.
- **Ruled out:** damped `SetFloat` parameter smoothing. EFT's movement code doesn't use it; only `NpcLookOffset` gestures do.
- **Next diagnostic counters:**
  - root motion per catch-up evaluation vs per normal evaluation (expect 2.0×)
  - whether the scaled speed was in effect at evaluation
  - whether the animator evaluated before that frame's ManualUpdate (decisions a frame late)

## 2026-10-19: Half-rate v2, catch-up diagnostics (capture `20261019-174426_factory4_day`): the loss is inside Unity's animator

| Check | Result |
| --- | --- |
| Scaled speed in effect at the catch-up evaluation | **12,118 of 12,118** |
| Animator evaluated before that frame's ManualUpdate | 3 (negligible) |
| Root motion per catch-up (Run) | 0.0219 m, vs ~0.027 m expected for 2 frames at 2.42 m/s and ~180 fps → **~81%** |
| Run speed, on vs off | 1.89 / 1.95 (root) vs 2.42 m/s |

- The timing and plumbing are correct. **Unity's Animator, evaluated once over two frames' worth of time (speed × 2 after a culled frame), produces ~20% less root motion** than two normal evaluations. That's a property of the animator, which a mod can't change.
- **Possible workarounds, none clean:**
  - Over-speed the animator (×~2.45): this also plays animation time faster, so footstep cadence speeds up.
  - Rescale the root motion from occasional full-rate samples per bot: fragile.
  - Skip only the state machine: not exposed by Unity.
- **Verdict:**
  - The half-rate option stays **off and marked "not recommended"**: it saves −0.7 to −1.1 ms on Streets, but off-screen bots move ~20–25% slower, which changes gameplay (slower flanks and rotations).
  - What was learned is solid: off-screen bot animators cost ~1 ms on Streets, `CullCompletely` per frame skips them at no extra cost, and `Animator.enabled` toggling does not work.

## 2026-10-20: FIKA headless (host on a second PC), Streets, capture `20261020-100541_TarkovStreets`

Setup: FIKA 2.3.9 client in this install, headless host on another machine on the LAN (192.168.4.53). 370 s capture with 844 methods instrumented, two `AllOptimizationsOff` ×3 sequences, ~19 players. **The display mode also changed:** Graphics.ini now has `FullScreenMode: 0` (exclusive fullscreen); every earlier capture ran borderless (`FullScreenWindow`).

**Main-thread work fell by ~4–5 ms, compared with the instrumented single-player Streets run `20261019-092351`:**

| Slot (ms/frame) | Single player | Headless client |
| --- | --- | --- |
| Update scripts | 2.76 | 1.91 |
| LateUpdate scripts | 2.51 | 1.06 |
| Animation (DirectorUpdateAnimationBegin) | 2.45 | 0.63 |
| Camera cull + render (main thread) | 3.42 | 3.43 |
| Character-controller overlaps | 1.80 | 0.04 (only the local player moves) |
| AI (`AICoreControllerClass`, `BotOwner`) | ~0.45 | gone |
| **Main thread busy** (frame minus present wait) | ~12.8 | **8.0** |

The animation drop (−1.8 ms) is more than the earlier estimate: observed (network) players animate more cheaply than local bots. New on the client: `Player.ArmsUpdate` 0.47 ms (19 calls/frame, observed players' arms).

**But the frame only went to 11.2 ms (Baseline), because the main thread now waits 3.2 ms per frame in `TimeUpdate.WaitForLastPresentationAndUpdateTime`.** That slot was 0.002 ms in every single-player capture. Every frame waits at least 0.5 ms. The client is now limited by the render-thread / GPU / present path, not by game logic:

| Draw calls | Share | Frame | Present wait | Main busy | Cull+render (main) | Render thread | GPU |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1500–2500 | 7% | 8.6–9.5 | 1.6–1.8 | 6.8–7.9 | 2.3–2.6 | 2.8–3.1 | 5.0 |
| 3000–3500 | 48% | 10.5 | 2.9 | 7.5 | 3.1 | 4.5 | 5.9 |
| 4500–5500 | 32% | 12.9–13.2 | 4.0–4.3 | 8.9 | 3.7–3.8 | 6.1–6.4 | 7.0–7.2 |

- **This is the "FPS swings with where I look" effect:** a dense view (≈5,000 draw calls) costs ~4 ms more than a sparse one (≈2,000). About 1.5 ms of that is main-thread cull and submit; the rest is waiting on the render thread and GPU. SetPass/draw ratio is 0.68, so there's almost no batching. Every draw is a state change, which is why the render thread scales with it.
- Frame ≈ render thread + GPU time in most buckets. So the render thread and the GPU barely overlap, and the main thread waits for the previous frame to finish presenting. Even at 1,500–2,500 draw calls, where render + GPU (7.8 ms) is below the frame, it waits 1.6 ms. **Prime suspect: exclusive fullscreen** (it changes how DXGI present queues frames). The other candidate is GPU downclocking at ~55% load. **Untested:** the same headless raid in borderless is the deciding test.
- **A/B (`AllOptimizationsOff` vs Baseline):** +1.29 ms (per repeat +1.05, +0.86, +0.76, +2.12, +1.37, +1.58), almost all render-side: cull +0.54, render +0.50. Shadow proxy culling and faded-light culling still pay off on the client. Batched overlaps and the runaway guard have nothing to do here (one controller, no BTR gunner). They belong on the headless host, which should run the Optimizations plugin too.
- **Hitches:** all 13 frames > 100 ms are the A/B switches (12 s cadence: step change ≈95 ms, screenshot switch-back ≈140 ms, re-apply ≈95 ms). Outside the switches: 8 frames > 33 ms in 6 minutes, all near capture or sequence start. **No AI hitches**: those happen on the host now.
- **Implication for the graphics A/B results:** they were measured main-thread-bound in single player. On a headless client, render-thread and GPU cost now hit the frame directly. LOD bias 1 (−0.37 ms render thread), Shadows Low, and visibility 400 should be re-measured here.

## 2026-10-20: Exclusive fullscreen vs borderless, headless client (capture `20261020-102132_TarkovStreets`)

One raid, standing at one spot, with an `AllOptimizationsOff` ×3 sequence run in each mode. The first sequence ran in exclusive fullscreen; Screen mode was switched to Borderless at ~185 s (Graphics.ini saved 10:24, hitches of 97–170 ms during the switch); then the second sequence ran. The view was almost the same (4,521 vs 4,300 draw calls). Each segment's first 3 s is skipped.

| Mode | Step | Frame | FPS | p99 | Present wait | Main busy | Render thread | GPU |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Exclusive | Baseline | 12.30 | 81 | 14.60 | 4.18 | 8.12 | 6.02 | 6.54 |
| Exclusive | AllOptimizationsOff | 13.27 | 75 | 16.13 | 4.22 | 9.05 | 6.15 | 6.53 |
| **Borderless** | **Baseline** | **8.83** | **113** | **10.53** | **0.00** | 8.83 | 5.79 | 6.38 |
| Borderless | AllOptimizationsOff | 9.89 | 101 | 11.54 | 0.00 | 9.89 | 5.88 | 6.55 |

- **Borderless is −3.5 ms (+39% FPS) at the same spot, with the same render-thread and GPU load.** In exclusive fullscreen, every frame waited ~4 ms in `WaitForLastPresentationAndUpdateTime`. In borderless the wait is 0.00 for every 5 s window after the switch, at every draw-call count, so the client is main-thread-bound again.
- The mechanism is DXGI presentation in exclusive mode (the main thread waits for the previous frame's present, so the render thread and GPU don't overlap with the next frame). It's not the GPU: GPU time is ~6.5 ms in both modes. This explains the 2026-10-20 exclusive capture, and it means that capture understated the headless gain.
- **Optimizations on the client, borderless:** +1.06 ms with all off (cull + render +0.84 ms). In exclusive mode the same toggles were +0.97 ms; they still counted because the main thread was part of the chain.
- **Recommendation:** Screen mode **Borderless** (Graphics.ini `FullScreenMode: 1`). Every single-player capture was already borderless; the setting changed to exclusive when FIKA was set up. EFT re-applies exclusive mode on focus if the setting says so (`GameGraphicsClass`, line ~457), so it has to be changed in the setting, not just by alt-tabbing.
- Profiler fix: the summary's `Screen:` line was read when the capture stopped. It now prints the mode at start and at stop.

## 2026-10-20: Declutter v1 (capture `20261020-104328_TarkovStreets`): froze 14 s per switch, no A/B data

Declutter was reimplemented from the idea of the Simple-Declutter mod (3.11, no license) as an opt-in option. See Optimizations/README.md.

- **Scan (Streets):** 135,689 LOD groups in 1,541 ms of main-thread time, time-sliced at 3 ms per frame, so no hitch. 18,184 candidates. The largest groups: deferred decals named graffiti 5,424, garbage 3,405, paper 1,795, debris 1,227, bottles 1,091, posters 731, puddles 554. Average heights 0.0–0.3 m (posters 1.0 m).
- **Rejected keyword matches:** 15,745 for having a collider, 872 for a `BallisticCollider` script. The "protected name" count (25,704) was a bug: it counted every LOD group, not just keyword matches.
- **The freeze:** every switch to on took **~14,000 ms** and grew the heap by ~3 GB, which opened the GC safety valve. Switching off took 80–90 ms. Cause: disabling a `StaticDeferredDecal` calls `StaticDeferredDecalRenderer.method_9`, which recomputes the bounds of every remaining decal in the group. Each of those calls allocates a copy of the cube mesh's vertices (`Mesh.vertices`), so 5,482 removals are O(n²) with allocations. Re-enabling (`method_8`) is a cheap add.
- **A/B:** unusable. Each 12 s Declutter step was swallowed by the freeze (1 frame per step), and the screenshot triples broke. The single frames showed −66 draw calls at a sparse spot (~2,400 draws).
- **v2:**
  - deferred decals are never touched
  - mesh decals (`dust_decal`, `decal_dirt`, `blood_decal`, stains, footprints, wall cracks, cobwebs; ~2,000 on Streets per the report) are hidden through their renderers instead
  - protected names are counted only for keyword matches
  - keywords added from the report's unmatched list
  - a new **Small decor** category (posters, toys, candles, curtain holders, sockets, shoes), off by default

## 2026-10-20: Declutter v2 (capture `20261020-105921_TarkovStreets`): safe, but only −0.08 ms

Headless client, borderless, standing in a littered alley (~2,460 draw calls, ~140 fps). `Declutter` ×3, default categories (Small decor off).

- **Switching:** 3–5 ms per switch, no freezes, no GC. Scan: 135,689 LOD groups in 1,895 ms of main-thread time, time-sliced. It hid 16,421 objects (24,025 renderers) map-wide.
- **A/B:** −0.083 ms (per repeat −0.01, −0.16, −0.08; spread 0.15, so at the noise floor). Draw calls −52 (−2%), SetPass −16, render thread −0.07 ms, shadow casters −5. **Camera culling unchanged (+0.000):** `forceRenderingOff` renderers still go through Unity's culling, so only the draws are saved.
- **Visible change:** 0.44% of pixels (noise floor 0.00%). The change map shows only small debris: cans and fragments. The large items in view stay: cardboard boxes and garbage bags have colliders, and the flat paper sheets are probably EFT's projected decals, which v2 no longer touches.
- **Rejections:** 18,042 keyword matches for colliders, 929 for `BallisticCollider` scripts. Most of the clutter that's big enough to matter is physical, so hiding it would leave invisible objects that still stop bullets and players. That's not acceptable.
- **Verdict:** keep it as an opt-in visual preference ("less mess"), off by default. It isn't a meaningful performance option: ~1 ms per 1,000 draw calls means ~50 draws ≈ 0.05 ms. The original mod's larger claimed gains would have to come from hiding objects with colliders (a gameplay change) or from its 3.11-era map content. Not worth pursuing further for frame time.

## 2026-10-20: Aggressive declutter upper bound (capture `20261020-111026_TarkovStreets`): not worth it

Same alley, headless client, borderless. `Declutter` and `DeclutterSolid` ×3, round-robin. `DeclutterSolid` (measurement only) also hides the meshes of keyword-matched clutter that has a collider or a `BallisticCollider` script. Colliders stay active, so it's the render-side upper bound of an aggressive declutter. Map-wide: 16,417 objects (24,021 renderers) for `Declutter`, 33,023 objects (76,633 renderers) for `DeclutterSolid`. Switching took 4–15 ms.

| Step | Frame | Per repeat | Draws | SetPass | Cam render | Render thread | Shadow casters | Pixels changed |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Declutter | +0.10 (noise) | −0.19, +0.49, +0.01 | −50 | −18 | −0.04 | −0.03 | −6 | 0.25% |
| DeclutterSolid | −0.09 (noise) | −0.33, +0.16, −0.10 | −166 (−7%) | −80 | −0.09 | **−0.25** | −28 | 2.45% |

- This run was noisier: the Update slot drifted by +0.16 ms in Declutter steps, with nothing hidden that runs scripts. Both frame deltas are inside the spread (0.5–0.7 ms).
- **Visually, `DeclutterSolid` empties the alley:** every paper sheet, can and bottle, plus a big cardboard box in the foreground, which was still solid.
- **Still no main-thread gain.** Hidden renderers still go through culling (cam cull ±0.00). The draw-call saving shows on the render thread (−0.25 ms), but the client is main-thread-bound here (main 6.6 ms vs render thread 3.4 ms), so the frame doesn't move. Only a render-thread-bound case (exclusive fullscreen, or very dense views) would see ~0.25 ms.
- **Conclusion:** even the upper bound of hiding everything clutter-like, colliders and all, is ≤0.25 ms of render-thread time and ~0 ms of frame time at this spot. Real collider removal would add gameplay and Fika fairness problems on top. **Dropped.** `DeclutterSolid` stays as a measurement-only step.

## 2026-10-20: Self-hosted vs headless, like for like (captures `113603`/`113740` self-hosted, `114756`/`114933` headless)

Two Streets raids on the same install: borderless, all optimizations on, method timing off. Raid 1 was hosted by this PC in Fika, so it ran the bots. Raid 2 was on the headless host (second PC). Each raid had a 60 s fixed-spot capture, standing still at the same landmark and view (3,261 vs 3,067 draw calls), and a ~3 min normal-play capture. The first 3 s of each capture are skipped.

| Main thread (ms) | Fixed, self-hosted | Fixed, headless | Play, self-hosted | Play, headless |
| --- | --- | --- | --- | --- |
| Rendering (cull + render + finish) | 3.40 | 3.11 | 3.18 | 3.13 |
| Update scripts | 1.84 | 1.91 | 1.97 | 1.68 |
| LateUpdate scripts | 1.53 | 0.93 | 1.48 | 0.99 |
| Animation | 1.69 | 0.75 | 1.89 | 0.75 |
| Other (UI, engine) | 0.49 | 0.47 | 0.55 | 0.50 |
| **Frame** | **8.95 (112 fps)** | **7.16 (140 fps)** | **9.07 (110 fps)** | **7.05 (142 fps)** |
| 1% low | 86 fps | 70 fps (one menu/GC event at 33 s) | 61 fps | 86 fps |
| Frames > 25 ms | 1 | 7 (same event) | 10 | 1 |
| Bots alive / on screen | 22 / 3.0 | 18 / 1.6 | 22 / 0.6 | 18–20 / 0.5 |

- **Measured: −1.8 ms at the fixed spot, −2.0 ms in play (+25–29% FPS).** Render thread (4.0–4.8 ms) and GPU (5.1–5.4 ms) stay below the main thread in both modes, and the present wait is ~0.
- **Where it comes from:** animation (−0.9 to −1.1 ms) and LateUpdate scripts (−0.5 to −0.6 ms). Update scripts barely change: the AI and bot movement that leave are replaced by observed-player work, chiefly `Player.ArmsUpdate`, ~0.8 ms for ~19 network players. Rendering is unchanged, as expected.
- **Hitches:** in play, frames > 25 ms fell 10 → 1 and the 1% low rose 61 → 86 fps. The self-hosted worst frames are AI spikes in `Update` (10–17 ms).
- **Confound:** the headless raid had 4 fewer bots (18 vs 22). Scaling the headless client's per-player costs (animation and observed-player scripts, ~0.1 ms per player) up to 22 adds ~0.4 ms. **Like-for-like estimate: −1.4 ms (fixed) to −1.6 ms (play), about +18–22% FPS.** That's below the measured 25–29%, and the gain grows with bot count (~0.15–0.2 ms per bot).
- This is less than the 2026-10-20 instrumented estimate (main thread −4 to −5 ms). That compared against an older single-player run with fewer optimizations and method timing on, which inflated script costs.

## 2026-10-20: Research report vs SPT 4.1.6 / Fika 2.4.6 (decompile check, no new capture)

Checked the research report's recommendations against the decompiled Fika 2.4.6 `Fika.Core.dll` and the 4.1.6 `Assembly-CSharp`.
- **Fika already does part of rank 1.** `ObservedPlayer.ObservedVisualPass` updates FBBIK every third frame (staggered per player) and gates the IK block on `CULL_GROUNDER`, which Fika's *IK Cull Distance* (40 m) sets. Human players are still never culled: `botPlayerCulling` is disabled for player profiles.
- **The report's rank-1 target was wrong.** `ProceduralWeaponAnimation.LateTransformations` costs 0.002 ms per frame in the 4.1.6 profile (capture `20261020-164936`, 5.7 calls per frame), and `HandsController.ManualLateUpdate` only applies latched flags (weapon overlap, recoil kick). The real cost is `Player.ArmsUpdate`, at 0.47 ms self time for ~19 observed players on the 4.0.13 headless client (capture `20261020-100541`). Fika creates observed players with `ArmsUpdateMode.Manual` (`CoopHandler`: `EUpdateMode` 1), so `ArmsUpdate` calls `ArmsAnimatorCommon.Update(dt)` for each of them every frame. Observed shots are replayed from packets, not from this animator. → new option *Far player arms at reduced rate*.
- **Half-rate body animation can work on Fika clients.** `ObservedPlayer.ManualStateUpdate` writes `Player.Position` from the interpolated snapshot every frame, so on an observed bot root motion moves nothing, and the ~20% catch-up shortfall that parked the feature can't slow it down. Fika's `ObservedPlayer.LateUpdate` doesn't call EFT's `Player.LateUpdate`, so `MovementContext.AnimatorStatesLateUpdate` (where the feature undid its changes) never runs for observed players; it now hooks `ObservedPlayer.LateUpdate` too. → *Distant bot animation at half rate* now applies to observed bots only by default.
- **Our *Bot IK distance* option fought Fika.** With the option at 0, every config change re-applied the game's 90 m to `CULL_GROUNDER`, silently undoing Fika's IK Cull Distance. Removed.
- **Two cheap knobs don't apply.** EFT already sets `Camera.layerCullDistances` for every layer from *Overall visibility* (`CameraManager.SetOverallVisibility`, layer 28 excepted), which `GfxVisibility400` measured. The Low shadow preset already has fewer than 4 cascades.
- **Scope camera:** `OpticComponentUpdater` copies the main camera's far clip to the optic camera, and `OpticCameraManager` shows and hides it with `SetActive`. Toggling only the `Camera` component every other frame (profiler step `ScopeHalfRate`) measures its cost without touching EFT's state.

Test steps: TESTING.md, batch 4.

## 2026-10-22: Batch 4 results, 4.1.6 headless client (captures `20261022-092607` … `094904`)

Runs from before the startup fix (`20261022-0906`…`0910`) are invalid: the Optimizations plugin died in `Awake` (a config key with an apostrophe), so every optimization was off.

Main A/B (`092607`, Streets, standing, 24 bots alive, ~3 visible at 70 m+, 4 repeats):

| Step | Δ frame time | Spread | Notes |
| --- | --- | --- | --- |
| ObservedArmsReducedRate | **−0.31 ms** | 0.09 | all of it in Update (−0.30); ~1,400 arms evaluations skipped per second |
| AllOptimizationsOff | **+1.27 ms** | 0.38 | the default-on optimizations are still worth ~1.3 ms on a headless client (cull +0.55, render +0.28) |
| BotAnimHalfRate | +0.15 ms | 0.87 | noise: **the feature never ran** (no `botAnimLod.*` counters). Fika's `ObservedMovementContext.ManualUpdate` overrides `MovementContext.ManualUpdate` without calling it, so the hook never fired for observed bots. Fixed: it now also hooks the override. To re-measure |

Scope (`093533`, variable scope zoomed in, 4 repeats): **ScopeHalfRate −1.77 ms** (spread 0.25) while scoped: camera cull −0.76, render −0.60, −990 draw calls. The scope camera is a second full scene render; at 12.7 ms scoped baseline it's the largest single cost found on the client so far. Candidate for an opt-in option (the lens image would update at half the frame rate).

IK distance: inconclusive. Both dedicated captures (`094755`, `094904`) ran at 20 m, and the 40 m captures were play clips from other views. Fika applies *IK Cull Distance* only at startup and when the setting changes, so the profiler's `IkDistance20` step can A/B it within one run. Census: most bots were 70 m+ and off screen, where IK doesn't run at either distance.

Census detail: off-screen observed bots already have all renderers forced off and both animators on `CullUpdateTransforms` (arms animator component disabled; Fika updates it by hand). So Unity already skips their transform writes; half rate can only save the state-machine evaluation.

## 2026-10-22: ScopeHalfRate, second run (capture `20261022-102433`, indoors, 4 repeats)

**−0.89 ms** (spread 0.63), half the first run's −1.77 ms. Valid: in every half-rate step the draw count alternates by 350–900 between frames, so the scope camera was rendering and being skipped. The difference is the view. Indoors at a wall a few metres away, the scope camera draws ~350–900 objects per render, against ~2,000 on the open street in the first run. **The scope camera's cost scales with what it sees: ~1.8 ms here, ~3.5 ms outdoors.** The spread is high because the sight picture changed between steps (Baselines ranged 7.2–9.7 ms).

In step 3 the player aimed through the backup red dot on top of the 3–20× scope and the scope camera still appeared to render. **Checked and ruled out** (capture `20261022-121644`, outdoors on the street, red dot only, 10 repeats): no draw-count alternation in 9 of 10 half-rate steps, and no frame-time change (+0.14 ms, noise). EFT switches the scope camera off while the backup sight is in use. The one exception (step 9: 6,482 draws, alternating by ~3,000, 14.1 ms) matches a brief switch to the scope itself, and the step-3 reading in the indoor run was the same thing.

## 2026-10-22: Half rate and IK distance retest (capture `20261022-123703`, Streets, 19 bots, 4 repeats)

The first repeat was during a firefight (Update 2.8–3.7 ms, p99 up to 20 ms); the other three are clean. Per segment, steps 3–12:

| | Unity animation (`DirectorUpdateAnimationBegin`) | Script Update | Frame |
| --- | --- | --- | --- |
| Baseline | 0.64–0.66 ms | 1.68–1.93 ms | 7.76–7.86 ms |
| BotAnimHalfRate | **0.53 ms** (−0.11) | 1.82–1.86 ms (**+0.14**) | 7.78–7.91 ms |
| IkDistance20 | 0.63–0.64 ms | 1.64–1.69 ms | 7.58–7.81 ms |

- **Half rate now runs on Fika clients** (`botAnimLod.skippedUpdates` up to ~28,000 per 30 s) **but nets about zero.** It saves 0.11 ms of Unity animation and spends ~0.14 ms more in Update, about 8 µs per bot per frame: switching each animator's culling mode and speed are native calls, and the validation counters are on by default. There's little to win: off-screen observed bots already have their renderers forced off and animators on `CullUpdateTransforms`, so Unity only evaluates their state machines. **Verdict: leave it off on clients.** At most ~0.1 ms could be recovered by cutting the overhead; not worth pursuing.
- **IK distance 40 → 20 m: −0.08 ms** (spread 0.28) with about one bot in the 20–40 m band. Roughly 0.1 ms per on-screen bot at 20–40 m, the bots you're most likely fighting, so their stiffer feet and weapons would show. **Verdict: keep Fika's 40 m default.**
- Play capture `20261022-124029` (F9, firefight): one 271 ms stall, almost all of it outside the player loop (`Loop.Gap` 255 ms, no GC), next to inventory-screen and icon-creation work; a game stall, not one of our features. The scope camera (`BaseOpticCamera`) shows up in most of the other worst frames.

## Next
0. Done and verified: GC policy (no gameplay collections), shadow proxy culling (−0.6 to −1.0 ms). Culling visual check: passed in clear weather. Still open: a `GcOn` A/B step to measure the per-frame cost of an enabled GC, far vertex lights as an optimization (≈ −0.25 to −0.35 ms cull, needs a visual check), and then the main-thread script and animation costs (Player.LateUpdate, Animators).
1. **GC policy (first optimization):** keep SPT's collection while the inventory is open, but turn the GC back off when returning to the raid. Add a heap-size safety valve. A/B: hitch count and p99.9 after looting.
2. ~~Break down `FinishFrameRendering`~~ Done: 2.2 ms cull + 2.2 ms render, all native. Render-thread and GPU time measured: both have lots of headroom. Next: **live A/B toggles** in one session at a fixed spot: (a) disable ShadowsOnly renderers beyond N m, (b) disable all ShadowsOnly (upper bound), (c) shadows off (upper bound), (d) lodBias 2→1, (e) disable far ForceVertex lights. Each toggle is tagged in frames.csv so the analyzer can compare segments.
3. **Player animation LOD:** try `CULL_GROUNDER`/`AnimatorCullDistance` at runtime, then `Player.LateUpdate`/Animator update-rate LOD for distant or off-screen players. Target: about 3.5 ms (Animators plus `Player.LateUpdate`).
4. **Allocation attribution:** while GC is off, Mono used-size deltas per instrumented method show who allocates the 2.8 MB/s.
5. Instrument async and coroutine continuations (`MoveNext` of compiler-generated state machines) to catch the `ScriptRunDelayedTasks` and coroutine spikes.
