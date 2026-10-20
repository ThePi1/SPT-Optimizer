# SPTOptimizer.Optimizations

The optimizations that came out of the profiling in [../FINDINGS.md](../FINDINGS.md). Each one can be switched off in ConfigurationManager (F12) under its own section, or in `BepInEx\config\com.sptoptimizer.optimizations.cfg`. `dotnet build -c Release` builds the plugin and copies it to `BepInEx\plugins\SPTOptimizer\`.

## GC policy

**Problem.** EFT turns the Mono garbage collector off at raid start. SPT's `MemoryCollectionPatch` turns it back on when the Inventory opens and never turns it off again. From the first loot onward, every collection is a 50–90 ms freeze in the middle of gameplay, because the heap is about 3 GB on Streets.

**Change.** While a raid is running:
- **Inventory open** (or any menu, with *Collect in any menu*): the GC is on. SPT's incremental collection runs where a hitch matters least.
- **Back in the game view:** the GC is off again, as EFT intended.
- **Safety valve:** if the managed heap grows past its raid-start size plus *Heap headroom MB* (default 3,072), the GC turns on until the heap drops back below half that headroom. This protects low-RAM machines in long raids without looting. Outside raids EFT manages the GC itself and the plugin does nothing.

The log shows each switch (`GC policy: ...`) and, for every inventory visit, the heap size before and after plus how many collections ran while it was open.

**Trade-off.** Between inventory visits the heap grows at EFT's normal rate (about 2.8 MB/s on Streets). The valve caps the worst case.

## Shadow proxy culling

**Problem.** Streets has about 173k enabled `ShadowsOnly` renderers (shadow proxy meshes). About 160k of them are more than 100 m from the player, while the sun's shadow distance is 40 m. Unity still culls all of them every frame, which measured at about 0.54 ms of camera culling plus 0.40 ms of shadow setup on the main thread. The shadows actually drawn didn't change.

**Change.** About 5 s into a raid, the static ShadowsOnly renderers on the *Layers* list are bucketed into a 32 m grid. Whenever the camera moves 4 m, cells farther than the threshold are disabled and nearer cells re-enabled. Cells on the boundary are decided per renderer, by the distance to the nearest point of its bounds. The threshold is the larger of *Distance* (100 m) and 2.5 × the current shadow distance. Skinned meshes and anything under a Rigidbody are skipped, as are renderers the game disabled itself. EFT's far shadows (`DistantShadow`) are drawn from its own command buffer and don't use these renderers.

**Expected.** About 1 ms per frame on Streets with no visual change. The first pass (about 160k renderers switched off) causes one hitch of a few hundred ms about 5 s after spawning.

## Experimental: BSG fast body animator (off by default, needs a game restart)

**Problem.** Every bot's 22-layer body `Animator` is evaluated every frame, even off-screen. EFT can't skip it, because bots move by root motion (`MovementState.ProcessAnimatorMovement` reads `Animator.deltaPosition`) and footsteps come from the animator's `FootStep` curve. On Streets that was about 65 µs per off-screen bot, ~1.3 ms for 19 bots.

**Change.** Turns on `ApplicationConfigClass.UseBodyFastAnimator`, BSG's own animator, which EFT only uses for observed players in online games:
- the body state machine runs in managed code
- root motion comes from a precomputed `RootMotionBlendTable`
- the visual PlayableGraph is stopped while a player is off-screen

The flag is set by a Harmony postfix on `TarkovApplication.ConfigureApplication`, before any player pools exist. The two root-motion table bundles (~46 MB) are added to EFT's preload list. It applies to every player, including you and the hideout.

**Status (tested 2026-10-19): works, but slower; leave it off.** On Streets it engaged for every player without errors and cut native animation from ~1.65–2.24 ms to 0.22 ms. But the managed state machine added ~3.2 ms of Update scripts (~150 µs per bot per frame under Mono), so the frame was ~1.6–2.4 ms slower. See FINDINGS.md. The log says whether it's active (`Fast body animator: UseBodyFastAnimator = true`), and 15 s into a raid it lists the body animator type every player got (expected: `FastAnimatorProcessorClass`). The profiler census shows the same per player.

## Batched character-controller overlaps (on by default)

**Problem.** Every player's move runs EFT's `SimpleCharacterController.Move`. For each movement sub-step that calls `method_7` → `Physics.OverlapSphereNonAlloc` to find neighbouring colliders, one player at a time on the main thread. On Streets the instrumented run measured `OverlapSphereNonAlloc` at 126 calls/frame and 1.8 ms/frame (14 µs each).

**Change.** All players' motions are known before LateUpdate applies them. At the first `MovementContext.AnimatorStatesLateUpdate` of each frame, the plugin issues one `OverlapSphereCommand` per player collider, covering the whole move path plus a margin (default 0.15 m), as a single batch on the job worker threads. `method_7` then uses the batched result whenever its own query sphere lies inside the batched one. Extra colliders in the superset are harmless, because the controller runs `ComputePenetration` on each neighbour and ignores non-overlapping ones. In these cases the original query runs instead:
- the query sphere isn't covered by the batched one
- the batched result is full (64) or empty
- the move wasn't seen by the pre-pass

**Measured (2026-10-19, Streets, ~22 bots, runaway move guard on):**
- LateUpdate −0.2 ms in each of 3 A/B repeats (−0.14, −0.17 and −0.29 ms); the frame-level change is within noise.
- 87–100% of controller queries are served from the batch, and the pre-pass costs ~0.07 ms per frame.
- **Validation:** about 6 million served queries were checked against the original query. None missed a collider; on average the batch returned one extra collider per query.
- No movement oddities were seen.
- The gain scales with the number of moving bots. An earlier measurement of −0.25 to −0.35 ms was inflated by the parked BTR gunner (see *Runaway move guard*).

**Toggle:** *Batched overlaps → Enabled* (applies immediately, no restart). The profiler's `BatchedOverlapsOff` A/B step turns it off for that step.

**Stats.** Every 15 s while it's enabled, LogOutput.log gets a `Batched overlaps:` line. It shows per-frame averages: queries batched, controller queries, how many were served from the batch, the fallback reasons, the pre-pass cost, the largest single move (who and where), and `Move` calls per frame.

**Validate** (testing only, costs time). Every served query is also run the original way. Any collider the batch missed is logged as `Batched overlaps MISMATCH` (it must stay at 0), along with how many extra colliders the batch returned. With batching off and Validate on, the log shows the original query rate, for comparison.

## Runaway move guard (on by default)

**Problem.** On Streets, the BTR's gunner bot (`shooterBTR`) is parked off-map at about (1000, −500, 1000) until the BTR arrives. Its position is held there, but it's not grounded, so its fall speed grows without limit (4,000+ m/s after a few minutes). Every frame, `SimpleCharacterController.Move` sub-steps that whole fall in 0.3 m steps, each with a collision query and depenetration pass. That's hundreds of sub-steps per frame, growing until the BTR arrives (or EFT's cap of 1,000 per frame), all for a move that changes nothing.

**Change.** A Harmony prefix on `SimpleCharacterController.Move` skips moves faster than *Max speed m/s* (default 200). Sprinting is ~7 m/s and long falls ~30 m/s. Each affected character is logged once.

**Trade-off.** None expected. The only characters affected are ones already in an impossible state.

**Measured (2026-10-19, Streets, F5 A/B about 4 min into the raid).** With the guard off, the cost is +0.2–0.3 ms per frame at first. Once the gunner's fall per frame is long enough (here about 4½ minutes into the raid), it jumps to **+5–6 ms per frame**: LateUpdate 1.8 → 7 ms, frames 9.2 → 15.3 ms, ~108 → 65 fps. It lasts until the BTR arrives (5–10 min on Streets). With the guard on, the cost is zero.

## Faded light culling (on by default)

**Problem.** EFT's `CullingLightObject` fades each lamp out by distance (by default from 50 to 80 m) by setting its intensity to 0. The `Light` component stays enabled, and Unity still culls and sorts every enabled light for every camera: about 4,600 invisible lamps on Streets.

**Change.** A light is disabled while EFT's own fade value for it is 0 (a postfix on `CullingLightObject.UpdateCullingObject`, plus a sweep 5 s into each raid). It's re-enabled as soon as EFT fades it back in.
- **Lamp switches:** the game can switch lamps on or off (power switches, via `LampController.TurnLights` and `ControlledLampGroup.method_8`). For a light being held off, a real switch is recorded as the state to restore later. `TurnLights` also runs every frame but returns early, so a prefix records whether a call actually switches.
- **Excluded:** lights with a flicker or volumetric component.

**Measured (2026-10-19, Streets, batches 1 and 2):** −0.21 to −0.26 ms of camera culling in all 6 repeats, and **0.00% visible change** in the screenshot check. **Counters:** `fadedLights.*`. **Profiler step:** `FadedLightCullingOff`.

## Visible trade-offs (opt-in)

### Object LOD bias override
*Trade-offs (visible) → Object LOD bias override*: 0 (default) uses the game's Object LOD setting, whose minimum is 2. The override substitutes its value where EFT applies the setting (`GameGraphicsClass.smethod_9`). EFT's per-camera scaling for optics still works on top of it.
- **At 1 (Streets):** −0.11 ms main thread, −0.37 ms render thread, −390 draw calls.
- **Visible cost:** the screenshot check shows a real change on 1.9% of pixels. Mid-distance trees thin out and small debris ~40–60 m away disappears.
- Only worth it if you're render-thread or GPU bound.

### Bot IK distance (removed in 0.3.0)
Fika 2.4.4+ owns this value (`EFTHardSettings.CULL_GROUNDER`) through its own *IK Cull Distance* setting (default 40 m), and also updates IK only every third frame. Our option re-applied its own value, or the game's 90 m when set to 0, on every config change, which silently undid Fika's setting. Use Fika's setting instead. Measured on 4.0.13: 20 m saved 0.3–0.7 ms with 2–4 bots on screen at 20–90 m.

### Far players' arms at reduced rate (off by default, Fika clients only)
*Trade-offs (visible) → Far players' arms at reduced rate*.

**Problem.** Fika creates every observed player (on a client: every bot and every other human) with a manually updated arms animator, so `Player.ArmsUpdate` evaluates it every frame for each of them. On the 4.0.13 headless client that was ~0.47 ms for ~19 players, the largest single script cost left. (The weapon late transforms the research report pointed at cost only ~0.002 ms per frame in the 4.1.6 profile.)

**Change.** For qualifying players the arms animator is evaluated every Nth frame (*every N frames*, default 2) and then advanced by all the time it missed. Unity fires all the animation events of that time in the one evaluation, so none are lost; they arrive up to N−1 frames late. The hands controller's own update and `ArmsLateUpdate` (event dispatch, ballistics) still run every frame. Players are staggered across frames.

**Qualifies:** an observed bot (or human, with *human players too*), alive, farther than the *near distance* (25 m), and off-screen or farther than the *on-screen distance* (60 m). Never while you aim down sights at an on-screen player. Never on the headless host. Keep the on-screen distance at or above Fika's IK Cull Distance, so on-screen players that still get IK aren't throttled.

**Why it is multiplayer-safe:** an observed player's shots are replayed from the shooter's packets, not produced by this animator, and the bones you shoot at come from the body animator, which this doesn't touch.

**Visible effect:** distant players' weapon handling (reloads, weapon switches) looks less smooth; its sounds can start a frame late.

**Counters:** `observedArmsLod.skippedUpdates`, `observedArmsLod.catchUpUpdates` in the `Stats:` line. **Measure:** profiler step `ObservedArmsReducedRate`.

### Declutter small / medium objects (both off by default; a cleaner look, not a speed-up)
Two switches under *Trade-offs (visible)*. Both are the idea of the [Simple-Declutter](https://github.com/gitTerebi/Simple-Declutter) mod (3.11 only, no license), reimplemented for 4.0.13 with stricter rules:

| Setting | What stops being drawn | Gameplay | Measured (Streets, littered alley) |
| --- | --- | --- | --- |
| **Declutter small objects** | Visual-only clutter: paper, litter, cans, bottles, casings, debris bits, puddles, dirt/blood/footprint mesh decals | unchanged: none of it has a collider, trigger, sound or script | −52 draw calls (−2%), 0.25–0.44% of pixels, frame time within noise |
| **Declutter medium objects** | Clutter up to 2 m that **has a collider**: cardboard boxes, garbage bags, bricks, scrap | **the objects stay solid**: they become invisible obstacles that still stop bullets, players and grenades, and still give everyone else in Fika cover | with small on too: −166 draw calls (−7%), −0.25 ms render thread, 2.45% of pixels, frame time within noise |

- **Why there's no frame-time gain:** hidden renderers still go through Unity's camera culling, which is where the main-thread time is. The saving is draw calls on the render thread, and that only counts when rendering is the limit (exclusive fullscreen or very dense views). In borderless the client is main-thread-bound.
- **Fixed rules:** keywords cover litter, packaging, debris, casings, puddles, glass and tile shards, and mesh decals, up to 2 m tall. Wall and room decoration (posters, toys, candles, curtain holders) is in the keyword list but never hidden. Protected names are always skipped: doors, windows, loot, containers, switches, lamps, mines, zones, street-number plates. Anything with a rigidbody, light, audio source, animator, particle system, skinned mesh or game script is skipped too; the one exception is EFT's `BallisticCollider` impact script, which makes an object "medium".
- **EFT's projected decals (`StaticDeferredDecal`) are never touched.** Disabling one makes EFT's decal renderer recompute the bounds of every other decal in its group, allocating a mesh copy per decal. Switching off ~5,500 on Streets froze the game for 14 s and allocated ~3 GB.
- **How it hides:** `Renderer.forceRenderingOff`, not deactivating the object. EFT's PerfectCulling occlusion system toggles GameObjects, LOD groups and `Renderer.enabled` itself, so those would be undone or fight it. Colliders are never disabled.
- **Scan:** once per raid, 5 s after either switch is first on, time-sliced at 3 ms per frame (~1.9 s of work on Streets). Both switches then apply instantly, mid-raid too (3–15 ms).
- **Fika:** it only changes what you see. With *medium* on, you can't see cover that is still there for everyone, including you.
- **Tuning:** *Diagnostics → Declutter report* writes `SPTOptimizer\declutter_<map>.txt`. It lists matches per keyword (medium ones marked), why keyword matches were rejected, and the most common small visual-only objects that no keyword matched.
- **Profiler steps:** `Declutter` (small) and `DeclutterSolid` (small + medium).

## Gameplay trade-offs (opt-in)

### Distant bot animation at half rate (experimental; since 0.3.0 Fika clients only)
**Measured (Streets self-hosted, 5 repeats, 4.0.13):** −0.72 to −1.09 ms per frame. **Problem there:** Unity's animator delivers ~20% less root motion when it catches up two frames in one update, so affected off-screen bots moved ~20–25% slower (run 1.9 vs 2.45 m/s). Details in FINDINGS.md.

**Since 0.3.0** it applies only to bots on a Fika client. There, Fika writes each observed bot's position from the host's snapshots every frame (`ObservedPlayer.ManualStateUpdate`), so root motion moves nothing and the shortfall can't slow them down. *Half rate: local bots too* (advanced) brings back the old behaviour for bots simulated on your own PC; not recommended. Never on the headless host. Fika's `ObservedPlayer.LateUpdate` doesn't call EFT's `Player.LateUpdate`, so the speed and culling-mode restore hooks it directly.

*Trade-offs (gameplay) → Distant bot animation at half rate*, off by default.

**Problem.** Every bot's 22-layer body animator runs every frame, even off-screen, because EFT needs its root motion and footstep curve. That's ~65 µs per off-screen bot, ~1.3 ms for 20 bots.

**Change.** Bots off-screen beyond 10 m, or on screen beyond 80 m, get their body animator paused every other frame. On the other frames it advances two frames' worth: EFT's own animator speed is scaled, never overwritten. EFT's movement time step (`MovementContext.LastDeltaTime`) is stretched to match, so its speed limits and gravity see the real elapsed time. Bots are staggered, so half of them animate on any given frame. The arms animator, which fires weapon handling events, is untouched.

**Visible effect:** distant bots' animation and movement update at half the frame rate.

**Validation counters** (`botAnimLod.on.*` vs `botAnimLod.off.*`): metres moved and seconds for the affected bots. Average speed is metres / seconds; on and off should match. `botAnimLod.pausedUpdates` and `doubleUpdates` show how much was skipped.

**Measure:** profiler step `BotAnimHalfRate`.

## Trade-off catalog (what else could be traded, measured costs on Streets)
| Option | Kind | Frame-time gain | What you give up | Status |
| --- | --- | --- | --- | --- |
| Fewer bots: SPT `SPT_Data\configs\bot.json` → `maxBotCap.tarkovstreets` (24) | gameplay | ~0.25–0.3 ms per bot (animation ~0.1, IK and per-player scripts ~0.08, AI ~0.02, movement and physics queries ~0.05); 24 → 16 ≈ −2 to −2.5 ms | fewer bots alive at once | server config; not changed |
| Distant bot animation at half rate | visual on Fika clients (gameplay when local) | −0.7 to −1.1 ms self-hosted; Fika client to be measured (batch 4) | off-screen bots animate at half rate | option, Fika clients only |
| Far players' arms at reduced rate | visual | to be measured (batch 4); the arms animator costs ~0.47 ms on the headless client | distant weapon handling less smooth | option, Fika clients only |
| IK distance 20–40 m | visual | 0.3–0.7 ms with bots on screen (4.0.13) | distant bots' foot IK | Fika's *IK Cull Distance* (our option removed) |
| Object LOD bias 1 | visual | −0.11 ms main, −0.37 ms render thread | trees thin out and debris vanishes at 40–60 m | option |
| Overall visibility 400 (game setting) | visual | to be measured (batch 3) | far skyline cut off | setting |
| Shadows Low (game setting) | visual | ~−0.1 ms | shadows end at 30 m instead of 40 m | not worth it |
| Shadows off | visual | ~−1 ms | all sun shadows | not offered |
| Corpse ragdoll cap | visual | ≈0: corpses settle in 0.02–2.1 s | — | dropped |
| Declutter small objects / medium objects | visual | ~0 ms frame time; −2% / −7% draws, medium −0.25 ms render thread | litter, debris, casings, puddles, mesh decals / boxes and bags become invisible (still solid) | options, off |
| Small-prop screen-size culling | visual | est. ≤0.1 ms (only ~20k enabled renderers remain beyond 200 m, ~40 visible) | tiny far props | not built |
| FIKA headless client | setup | measured −1.4 to −2.0 ms (+18–29% FPS), 1% lows 61 → 86 fps | a second PC (or 64 GB on one PC) running the headless host | see FINDINGS 2026-10-20 |

## Multiplayer (Fika)

Fika splits the raid between machines:
- **Host or headless:** runs bot AI, bot movement and spawning, grenade and loot physics, and approves inventory changes.
- **Each client:** simulates only its own player. Every bot and every other human appears on the client as a network-driven *observed player*, which is animated and rendered locally.
- **Hit authority:** each shooter's machine decides whether its shots hit. A bot's bullet hitting a player is decided on that player's machine. So anything that changes what is solid or where a visible body is posed changes hits.

| Feature | Matters on | Multiplayer-safe? |
| --- | --- | --- |
| GC policy | every machine | yes: memory management only |
| Shadow proxy culling, faded light culling | clients (rendering) | yes: visual only, no colliders |
| Declutter small objects | clients | yes: renderers only |
| Declutter medium objects | clients | visually unequal: the objects stay solid for everyone, but you can't see them |
| Batched overlaps | the machine moving the characters: the host for bots, each client for its own player | yes: same query results (validated with 0 misses) |
| Runaway move guard | the host or headless (the BTR gunner is a host-side bot) | yes |
| IK distance | clients | Fika's own *IK Cull Distance* (our option was removed in 0.3.0) |
| Half-rate bot animation | clients only, observed bots only | yes: off-screen bots only, never humans, never on the host. Position comes from the host |
| Far players' arms at reduced rate | clients only | yes: arms animator only (shots come from packets, hit bones from the body animator); never on the host |

**Installing:**
- Fika checks each plugin's GUID and a CRC32C hash of its DLL against the server's lists.
- With empty *required* and *optional* lists (the default), every mod is allowed. If the lists are in use, add `com.sptoptimizer.optimizations` (and `com.sptoptimizer.profiler`) to *optional*.
- Every machine must run the identical build, or the hash check fails.
- The headless host benefits from the GC policy, batched overlaps and the runaway guard. The Fika wiki advises against declutter-type mods on the headless; they'd do nothing there anyway, because it barely renders.

## Diagnostics

### Physics and corpse stats (on by default, no behaviour change)
EFT only steps physics (`Physics.Simulate`) while some registered rigidbody is active: corpse ragdolls (up to 15 s, or until asleep), dropped loot, grenades, and the BTR's wheels and suspension. The 30 s `Stats:` line reports:
- `physics.simCalls`, `physics.simMs`, `physics.simMaxMs`: how often physics stepped and what it cost
- `physics.vehicleOnlyCalls`: steps where only the BTR's bodies kept physics on
- `physics.secondsOn`: seconds with physics on
- `physics.bodies.*`: registered bodies by kind
- `corpse.settled` vs `corpse.timeout`, `corpse.settleSecMax`, `corpse.settleSecTotal`: how long ragdolls take to settle
- `physics.allSleepingCalls`: steps where every registered body was asleep, i.e. could have been skipped
- `physics.other[...]`, `physics.vehicle[...]`: names of the bodies

This decides whether a corpse-settle cap or a BTR physics fix is worth building.

## How to verify

1. **Shadow proxy culling, performance:** stand still somewhere busy on Streets and press **F5** (profiler A/B sequence). It now includes a `ShadowProxyCullingOff` step. In `summary.txt`, that step's delta versus Baseline is the optimization's gain (it should be positive, about +1 ms).
2. **Shadow proxy culling, visuals:** walk a route and watch shadows near and far, including at a low sun angle if possible. Switch *Shadow proxy culling → Enabled* off and on in F12 at the same spot to compare. Watch for shadows that pop in or out at about 100 m.
4. **Fast body animator:** one Streets raid with *Experimental → BSG fast body animator* on, one with it off, each with a game restart in between. In each, stand at the same spot and press **F9** for about 60 s. With it on, also check that bots walk, run, aim and reload normally, that you hear their footsteps as they approach, and that LogOutput.log has no new exceptions.
3. **GC policy:** do two similar Streets raids of 5+ minutes, looting several times, each recorded with **F9** (or *Auto capture raids*): one with *GC policy → Enabled* on, one off. Compare frames over 50 ms after the first loot, and the collections listed in `summary.txt`. With the policy on, collections should only happen while the inventory is open.
