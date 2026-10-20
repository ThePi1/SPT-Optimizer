# CPU/script-side optimization for Unity 2022.3 Mono games from runtime mods (BepInEx 5 + HarmonyX)

Scope: EFT 0.16.9, Unity 2022.3.43f1, Mono, Win/DX11, main-thread bound. All feasibility judgments assume runtime modding only (Harmony patches, added MonoBehaviours, Unity API calls, boot.config/command line). Items marked [inference] are reasoned from general Unity/Mono knowledge and were not confirmed by a fetched source in this session; treat them as hypotheses to measure.

## 1. Mono vs IL2CPP; what can be done to Mono at runtime

### Takeaway
No reliable 2022-era Mono-vs-IL2CPP benchmark exists; published results are workload-dependent (IL2CPP sometimes slower). EFT is Mono and cannot be switched; no documented runtime switch (boot.config/env var) for Mono JIT optimisation flags was found, so the lever is writing/patching code to be Mono-JIT-friendly.

### Cited Findings
- Unity docs: Mono compiles C# to machine code at runtime (JIT); IL2CPP recommended for "more predictable performance", Mono for faster iteration/builds on desktop. — [Unity scripting back ends intro](https://docs.unity.com/en-us/engine/6000.7/manual/programming-environment/scripting-backends/intro)
- Jackson Dunstan (Unity 4.6/5.0 era) found IL2CPP performance "can vary from wildly slower to wildly faster" than Mono depending on code. — [jacksondunstan.com/articles/3001](https://www.jacksondunstan.com/articles/3001)
- Unity issue (2018.4–2020.2): byte[] ops similar on both; IList<byte> ops ~3x slower on IL2CPP (interface dispatch path). — [Unity issue tracker](https://issuetracker-mig.prd.it.unity3d.com/issues/il2cpp-il2cpp-scripting-backend-is-a-lot-slower-than-mono-when-performing-operations-with-ilist)
- Anecdote (2019.4): H.264 decoding Mono 38.42 fps vs IL2CPP 19.55 fps; a Unity staff member said IL2CPP is better at floating point math than Mono. — [Unity Discussions: IL2CPP super slow vs Mono](https://discussions.unity.com/t/il2cpp-super-slow-vs-mono-standalone/800338)
- Mono runtime selection flags such as `--gc=boehm`/`--gc=sgen` exist as Mono command-line options; Rider can pass env vars to a standalone player, but no source confirmed Unity's player honours `MONO_ENV_OPTIONS`. — [JetBrains Rider: Unity executable run config](https://www.jetbrains.com/help/rider/2024.2/Run_Debug_Configuration_Unity_Executable.html)

### Inferences
- [inference] Unity's Mono fork JIT does modest optimisation (no tiered JIT, limited inlining across generic shared code, no devirtualisation of interface calls, no SIMD auto-vectorisation). Practical Mono-friendly rules for hot patched code: avoid interface/virtual calls inside tight loops (use concrete types, arrays over List<T>/IList<T>), avoid LINQ/foreach over interfaces (enumerator boxing), avoid boxing in string formatting/Dictionary with enum keys without custom comparer, cache `transform`/component lookups, prefer `for` over arrays.
- [inference] Replacing a hot game method body via prefix-returning-false (or transpiler) with an allocation-free, array-based version is the main available "compiler" lever; Mono AOT of game assemblies at runtime is not feasible for a shipped player.
- The Unity "10000 Update calls" post found that switching a manager list from List to array made its loop ~5x faster (IL2CPP context) — see section 5; array-over-List advice applies to Mono too, by smaller margins.

### Gaps
- No primary source quantifying Mono vs IL2CPP on Unity 2022.3 specifically.
- Could not confirm whether `MONO_ENV_OPTIONS` (e.g. `-O=all`) is read by Unity's embedded Mono (mono-2.0-bdwgc.dll); this is testable by setting the env var and checking behaviour/`--version` output in the player log. Unity's Mono fork may ignore or partially support it.

## 2. Job System, Burst, threads and thread-safe Unity APIs from a mod

### Takeaway
The C# Job System (IJob/IJobParallelFor/IJobParallelForTransform/NativeArray) is part of the engine and works from mod code in a Mono player, but jobs run as Mono-JIT'd managed code unless Burst-compiled. Burst can be loaded by mods via `BurstRuntime.LoadAdditionalLibrary` (Unity 2021.1+), with a working precedent (Risk of Rain 2 "Bursts of Rain"), but requires the game to contain the Burst runtime and the mod to be Burst-compiled with a matching Burst version in an Editor/bcl.exe.

### Cited Findings
- Since Unity 2021.1, `BurstRuntime.LoadAdditionalLibrary`/`LoadAdditionalLibraries` lets a game load extra Burst-compiled libraries for mods; libraries must be loaded before the first Burst-compiled use of any method; works only in Play mode or Standalone; example built by copying `lib_burst_generated.dll` from a Standalone build of the mod project into the mod folder, Win64 proof of concept. — [Burst 1.8 manual: modding support](https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/modding-support.html)
- Precedent: "Bursts of Rain" (Thunderstore, Risk of Rain 2) is a loader for Burst-compiled assemblies in mods; bundles Unity.Burst 1.8.18, Unity.Collections 1.5.1, Unity.Mathematics 1.2.6; entry point is `BurstRuntime.LoadAdditionalLibrary`; without ThunderKit you invoke Unity's internal Burst compiler (bcl.exe) by hand. — [Thunderstore: BurstsOfRain](https://thunderstore.io/c/riskofrain2/p/RiskofThunder/BurstsOfRain/)
- Precedent: KSPBurst made Burst available to Kerbal Space Program mods (forum page could not be fetched, 403). — [KSP forum: KSPBurst](https://forum.kerbalspaceprogram.com/topic/201112-18-111-kspburst-burst-compiler-for-kerbal-space-program)
- In player builds only jobs the AOT compiler can discover are Bursted; generic jobs are missed unless the concrete type is declared. — [Unity forum: jobs burstified in editor but not standalone](https://forum.unity.com/threads/jobs-are-burstified-in-editor-but-not-on-standalone-x64-windows-client.807990/)
- Without Burst, jobs "run slowly but correctly". — [bugnet.io](https://bugnet.io/blog/fix-unity-job-parallel-for-burst-compile-error)
- Anecdote: same job ~25 ms non-Burst (editor and Mono build) vs 2.7–4 ms with Burst; much of the non-Burst cost was NativeArray safety checks; completing a job immediately after scheduling effectively runs it on the main thread. — [Unity Discussions: Why jobs are slower](https://discussions.unity.com/t/why-jobs-are-slower-in-this-case/712209)
- Anecdote: a mesh-deformation case where plain threading (0.58 ms) beat Jobs+Burst (2.67 ms) — not controlled. — [Unity Discussions: Burst and Jobs speed](https://discussions.unity.com/t/burst-and-jobs-speed-question/815018)
- IJobParallelForTransform runs one independent op per transform in a TransformAccessArray (position/rotation/scale); arrays must be disposed manually; schedule early, complete late; copy deltaTime into the job. — [Unity 2022.3 IJobParallelForTransform](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Jobs.IJobParallelForTransform.html)

### Inferences
- In a release (non-development) player, NativeArray safety checks (ENABLE_UNITY_COLLECTIONS_CHECKS) are compiled out, so the 25 ms "non-Burst" editor figures above overstate the managed-job cost in EFT. [inference]
- Burst feasibility for EFT: check whether `EscapeFromTarkov_Data/Plugins/x86_64/lib_burst_generated.dll` and `Unity.Burst.dll` exist in Managed. If the game ships Burst, a mod can compile with the same Burst package version in a Unity 2022.3.43 editor and load with `LoadAdditionalLibrary` early (BepInEx preloader/Awake). If not, Burst is effectively unavailable. Version mismatch risk is high.
- [inference] Jobs from Mono worker threads: managed jobs execute JIT'd code on Unity worker threads; the threads are attached to the Mono domain so managed allocations are allowed but trigger GC contention; avoid allocations and any UnityEngine object access in Execute.
- [inference] Plain .NET threads/ThreadPool/Parallel.For work in Unity Mono (System.Threading on .NET 4.x/Standard 2.1 profile), but most UnityEngine APIs throw or are undefined off the main thread; only pure math (Vector3/Mathf/Quaternion structs) and data copies are safe. The thread-safe engine paths are the batch query commands (RaycastCommand, SpherecastCommand, CapsulecastCommand, BoxcastCommand, and since 2022.2 OverlapSphereCommand/OverlapBoxCommand/OverlapCapsuleCommand/ClosestPointCommand), plus TransformAccessArray jobs. The team already uses OverlapSphereCommand; RaycastCommand batching of LOS/visibility raycasts (bot vision, AI cover) is the next analogous target.
- [inference] IJobParallelForTransform parallelises per hierarchy root (transforms under one root are processed on one thread), so it only scales when TransformAccessArray contains many separate roots — relevant if moving many independent objects (e.g. loot physics, bot root transforms), not bones of one rig.

### Gaps
- Did not confirm whether EFT 0.16.9 ships the Burst runtime. Check game folder.
- No controlled numbers for managed IJobParallelFor speedups in a Mono release player.

## 3. Harmony/HarmonyX overhead

### Takeaway
No published per-call overhead benchmark for HarmonyX prefixes/postfixes was found. Mechanistically, Harmony replaces the original with a generated replacement method that calls prefixes/postfixes as ordinary static calls, so overhead is roughly one static call per patch plus argument marshalling (`__instance`, `ref` args, `__state`), but inlining/generic sharing are correctness hazards.

### Cited Findings
- Inlined methods cannot be patched: patches silently do nothing; workarounds are running in debug mode ("large speed penalty") or patching a non-inlined caller. — [Harmony docs: edge cases](https://harmony.pardeike.net/articles/patching-edgecases.html)
- Generic methods may be shared across reference-type T, so patching one patches all; value-type instantiations are separate. — [Harmony docs: edge cases](https://harmony.pardeike.net/articles/patching-edgecases.html)
- Methods whose body only throws (no RET) can cause InvalidProgramException on Mono when patched; a transpiler fixes it. — [Harmony docs: edge cases](https://harmony.pardeike.net/articles/patching-edgecases.html)
- RimWorld community practice: mods converted their busiest Harmony prefix/postfix patches to transpilers to cut overhead (Toggleable Overlays / Toggleable Readouts changelogs, as summarized by search). — [Steam changelog](https://steamcommunity.com/sharedfiles/filedetails/changelog/2661792499)
- Performance Fish (RimWorld) patches methods for efficiency while keeping behaviour, each patch toggleable; requires Prepatcher (load-time assembly rewriting) and Fishery. — [GitHub: Performance Fish](https://github.com/bbradson/Performance-Fish)
- MonoMod.RuntimeDetour (HarmonyX's backend) offers Hook/ILHook APIs; low-level MonoMod.Core allows only one detour per method. — [NuGet: MonoMod.RuntimeDetour](https://www.nuget.org/packages/MonoMod.RuntimeDetour)

### Inferences
- [inference] Practical rules: (a) patch the per-frame outer method (e.g. Player.LateUpdate / ArmsUpdate) rather than inner helpers called thousands of times; (b) prefer one prefix that returns false and runs a rewritten body over several prefix+postfix pairs; (c) avoid `__args` (object[] boxing allocation per call), `__originalMethod` lookup and `__state` of reference types in hot patches; (d) use transpilers for small in-place edits in hot loops (zero extra call); (e) for small hot methods, Mono may already inline them into callers, so a patch on the callee may not take effect — patch the caller.
- [inference] Patching itself also defeats Mono's inlining of the patched method into its callers (the replacement is a dynamic method), so patching tiny getters called very often can add a call where there was none.
- Prepatcher-style load-time IL rewriting (BepInEx 5 preloader patchers / Mono.Cecil) can add fields to game types (e.g. caches) with zero runtime detour cost — the BepInEx equivalent of Performance Fish's Prepatcher. [inference based on Performance Fish's dependency on Prepatcher]

### Gaps
- No measured ns/call for HarmonyX prefix/postfix on Mono. Easy to measure in-game with Stopwatch over 1e6 calls of a patched vs unpatched dummy method.

## 4. Animator optimisation in 2022.3

### Takeaway
Unity 2022.3 offers cullingMode, keepAnimatorStateOnDisable, manual `Animator.Update(dt)` and PlayableGraph manual evaluation; a per-Animator "update every N frames" API exists only in Unity China's Tuanjie engine, not stock 2022.3. Throttling must therefore be done by manual Update calls (or cullingMode toggling), with root motion handled explicitly.

### Cited Findings
- Animators evaluate every frame by default; when no paired renderer is visible the Animator (in cull modes) only updates root position; `Animator.Update` evaluates immediately regardless of update mode. — [Unity Animator scripting reference](https://docs.unity.cn/6000.3/Documentation/ScriptReference/Animator.html)
- `Animator.SetUpdateFrequencyMode` (every N frames / fixed time interval) is documented in the Tuanjie (Unity China) manual, not the stock Unity 2022.3 API. — [Tuanjie: Animator.SetUpdateFrequencyMode](https://docs.unity.cn/cn/tuanjiemanual/ScriptReference/Animator.SetUpdateFrequencyMode.html)
- With root motion, the root joint displacement for the frame is computed and added to the GameObject transform. Real-project write-up: many off-screen Animators still updating was a usual cause of high CPU; disable Apply Root Motion where displacement isn't needed. — [dtf.ru: Unity Performance Optimization III: Animation](https://dtf.ru/gamedev/1070001-unity-performance-optimization-iii-animation-module)
- Anecdote: moving ~800 Animators into a PlayableGraph and throttling caused a large performance loss. — (summarized in the same search; [dtf.ru](https://dtf.ru/gamedev/1070001-unity-performance-optimization-iii-animation-module))
- `keepAnimatorStateOnDisable` (default false) keeps controller state and animated values while the GameObject is inactive; applies only to the AnimatorController, not AnimatorControllerPlayable; docs do not say whether re-enable skips reinitialisation. — [Unity 2022.3 keepAnimatorStateOnDisable](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Animator-keepAnimatorStateOnDisable.html)
- Community thread on preventing Animator auto-update (manual update approach). — [Unity Discussions](https://discussions.unity.com/t/prevent-animator-from-auto-updating/633734)

### Inferences
- [inference] Manual-update pattern: set `animator.enabled = false` is NOT suitable (re-enable = rebind, as the team found). Instead keep the Animator enabled but prevent the automatic update: e.g. set `animator.playableGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual)` then call `animator.playableGraph.Evaluate(accumulatedDt)` every N frames, or `animator.speed = 0` on skipped frames and `Animator.Update(accumulatedDt)` on update frames. Calling `Animator.Update(dt)` on an enabled animator in addition to the automatic update double-evaluates, so the automatic update must be suppressed. Both are main-thread, synchronous evaluations, so they lose the multithreaded batched animation path Unity uses for automatic updates — measure; batching loss can outweigh savings (consistent with the 800-animator PlayableGraph anecdote).
- [inference] Root motion when throttling: with one large dt, Unity integrates root motion from the sampled clip delta, but state-machine transitions/blend-tree weights and parameter changes during the skipped frames are applied at once, which can under-shoot (matching the observed ~20% shortfall). Common game approach: drive networked/AI character position from the authoritative movement code (not root motion) for throttled characters, i.e. set `applyRootMotion=false` (or intercept `OnAnimatorMove`) on distant/remote players and compute displacement from velocity; or accumulate `animator.deltaPosition` and re-scale to expected velocity. For EFT networked players, position is server/network-driven, so root motion on remote ObservedPlayers may be unnecessary — check whether ObservedPlayer animators use root motion at all.
- [inference] Animation LOD ladder used in games: full rate near/on-screen; half rate mid distance; quarter rate far; CullCompletely off-screen; reduce `SkinnedMeshRenderer.quality` (bones per vertex) and disable IK/LookAt layers (layer weight 0, `animator.SetLayerWeight`) for far characters; `AnimatorCullingMode.CullUpdateTransforms` keeps state machine and root motion but skips retargeting/IK/transform writes when invisible (cheaper than AlwaysAnimate, preserves root motion unlike CullCompletely).
- [inference] `AnimatorUtility.OptimizeTransformHierarchy` at runtime removes bone GameObjects (only exposes listed ones); it saves TransformChangeDispatch and transform write cost but breaks any game code that references bones (EFT's weapon/arms code reads many bones via PlayerBones) — high risk.

### Gaps
- No primary source on the exact 2022.3 internal behaviour of root motion catch-up for large dt; team's measured 20% loss is the best data.
- No source verifying that PlayableGraph manual mode on an Animator-owned graph is supported in 2022.3 without side effects.

## 5. Update throttling, update managers, PlayerLoop

### Takeaway
Unity's native→managed Update dispatch is dominated by per-call overhead, not the Update body; a manager loop over an array is much cheaper. Mods can insert systems into the PlayerLoop via `PlayerLoop.GetCurrentPlayerLoop/SetPlayerLoop`.

### Cited Findings
- Unity blog "10000 Update() calls": a 1-minute iPhone 6 profile spent 9,979 ms in Update dispatch: iterating Behaviours 1,517 ms, validity checks 2,188 ms, preparing invocation 2,061 ms, argument validation 900 ms, Runtime::Invoke 1,520 ms — while the Update bodies took only 42 ms. A manager loop with an array was 0.23 ms on Mono; array vs List made it ~5x faster; conclusion holds on IL2CPP too. — [Unity blog: 10000 Update() calls](https://unity.com/blog/engine-platform/10000-update-calls)
- Unity's current manual recommends a custom update manager for many per-frame updates. — [Unity manual: custom update manager](https://docs.unity.com/en-us/engine/6000.3/manual/scripting/managing-update-order/events-per-frame-optimization.md)
- Forum caveat: the 2015 post produced some misconceptions; numbers are dated. — [Unity Discussions: 50k Update conundrum](https://discussions.unity.com/t/should-i-use-an-update-manager-the-50k-update-conundrum/765661)
- PlayerLoop API: GetCurrentPlayerLoop, GetDefaultPlayerLoop, SetPlayerLoop to set a custom order including new script entry points. — [Unity 2022.3 PlayerLoop](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/LowLevel.PlayerLoop.html)

### Inferences
- [inference] For EFT, the measured costs (ArmsUpdate 0.8 ms for 19 players ≈ 42 µs/player) are dominated by body work, not dispatch overhead, so update managers give little; time-slicing the body (update far/off-screen players every 2–4 frames, round-robin so per-frame cost is flat) is the real lever. Implement via Harmony prefix on the per-entity Update/LateUpdate: `if (!ShouldUpdateThisFrame(id)) return false;` with accumulated dt passed when it does run. Correctness risk: anything assuming per-frame state (interpolation, network snapshot consumption, sound, IK) — throttle visual-only work, never network/state consumption.
- [inference] PlayerLoop insertion (e.g. a system right after `PreLateUpdate.ScriptRunBehaviourLateUpdate` or before `PostLateUpdate.UpdateAllSkinnedMeshes`) is the cleanest place to schedule jobs early and complete them late, e.g. start RaycastCommand batches in EarlyUpdate and consume them in Update. Always build from GetCurrentPlayerLoop (not Default) to keep game/other mods' inserted systems.
- [inference] Removing engine systems via PlayerLoop (e.g. unused `PostLateUpdate.UpdateVideo`, XR, Director systems) is possible but savings are typically tiny unless the system shows in a profile.

### Gaps
- No 2022.3 Mono-specific measurement of per-Update dispatch cost on desktop.

## 6. GC in Unity 2022.3 Mono

### Takeaway
Unity uses Boehm–Demers–Weiser, non-compacting; incremental mode time-slices marking but adds write-barrier overhead to all reference writes in managed code, and can fall back to a full collection if references change too fast. The team's "incremental off in gameplay, collect in menus" approach is consistent with docs.

### Cited Findings
- Incremental GC is Unity's default; uses Boehm in incremental mode; does not make GC faster, only spreads it; uses leftover frame time when VSync or targetFrameRate is set; `Scripting.GarbageCollector` lets you pass a custom time budget; stop-the-world pauses can be hundreds of ms; incremental mode emits write barriers that add overhead on every reference change; if references change too frequently marking may never finish and falls back to full collection; non-incremental is stop-the-world and non-compacting. — [Unity 2022.3 manual: incremental GC](https://docs.unity3d.com/2022.3/Documentation/Manual/performance-incremental-garbage-collection.html)

### Inferences
- [inference] Write barriers are compiled into JIT'd code when the player was built with incremental GC enabled; toggling `GarbageCollector.GCMode = Disabled/Manual` at runtime stops collections but does not remove barrier cost. Whether EFT's build has incremental enabled is visible in `boot.config` (`gc-max-time-slice=` line present implies incremental).
- [inference] `GarbageCollector.GCMode = Disabled` during raids with `GC.Collect()` at load screens works only if raid allocation volume fits in memory; monitor `GC.GetTotalMemory`/`Profiler.GetMonoUsedSizeLong` and force a collect when exceeding a threshold. Use `GarbageCollector.CollectIncremental(nanoseconds)` in Manual mode to spend a budgeted slice per frame when headroom exists.
- Allocation hotspots typical of Unity Mono code: LINQ, closures capturing locals, `foreach` over interface-typed collections, string concatenation/formatting in per-frame logging, `GetComponents` without list overload, `Physics.RaycastAll/OverlapSphere` (use NonAlloc), `Animator.GetCurrentAnimatorClipInfo` (allocating array overloads), boxing of enum dictionary keys. [inference]

### Gaps
- No source found for exact default `gc-max-time-slice` value and the boot.config key semantics in 2022.3.

## 7. Physics settings at runtime

### Takeaway
`Physics.autoSyncTransforms=true` inserts a transform sync before every physics query; off is recommended since 2017.2. Other runtime knobs (simulationMode, reuseCollisionCallbacks, fixedDeltaTime) are settable from a mod.

### Cited Findings
- With autoSyncTransforms true, alternating Transform edits and physics queries causes performance loss; docs say only keep it true for pre-2017.2 compatibility. — [Unity 2018.4 Physics.autoSyncTransforms](https://docs.unity3d.com/es/2018.4/ScriptReference/Physics-autoSyncTransforms.html)
- Unity 6 manual: enabling it adds implicit synchronization points before every physics query. — [Unity manual: optimize transform value syncing](https://docs.unity.com/en-us/engine/6000.3/manual/physics-section/physics-overview/physics-optimization/cpu/transform-sync.md)
- Forum: with autoSync false, transforms still sync once before simulation; with true, also at query points; one user measured manual sync ~1 ms slower than 2017.1 (old, single test). — [Unity Discussions: SyncTransforms questions](https://discussions.unity.com/t/physics-synctransforms-questions/673683); [Case 940893](https://discussions.unity.com/t/case-940893-physics-synctransforms-performance-issue/673769)

### Inferences
- [inference] Check EFT's value of `Physics.autoSyncTransforms` at runtime; if true, set false and add explicit `Physics.SyncTransforms()` before the game's query-heavy phases (e.g. before the batched OverlapSphereCommand). Risk: stale query results for colliders moved this frame (bullets hitting old positions) — must sync before ballistics.
- [inference] `Physics.reuseCollisionCallbacks=true` removes Collision object allocations in OnCollision* callbacks (GC win). `Time.fixedDeltaTime` increase reduces FixedUpdate count (big CPU win when low fps causes multiple FixedUpdates per frame) but changes simulation/gameplay; `Time.maximumDeltaTime` caps catch-up steps ("spiral of death"). `Physics.simulationMode = Script` lets a mod call `Physics.Simulate` itself (e.g. skip when nothing moves) — high risk for EFT. Broadphase type (`PhysicsManager`/`Physics` broadphase SAP vs MBP vs Automatic Box Pruning) is a project setting, not settable at runtime via public API.
- Moving many static colliders (Rigidbody-less) each frame forces broadphase rebuilds — a known Unity cost; ensure moving colliders have kinematic Rigidbodies. [inference]

### Gaps
- No 2022.3 measurement of SyncTransforms cost per transform.

## 8. Transform hierarchy and skinning costs

### Takeaway
SkinnedMeshRenderer skinning stops off-screen unless `updateWhenOffscreen` is enabled; setting it false and providing conservative bounds is the documented performance path. GPU/compute skinning is a build-time Player Setting not exposed to mods.

### Cited Findings
- `Update When Offscreen` is off by default for performance; when on, bounds are recomputed every frame even when not visible; when off, skinning stops off-screen and resumes when visible; prefer setting bounds to the maximum volume. Quality caps bones per vertex (Auto/1/2/4); more bones cost more computation; global limit in Quality settings (Skin Weights). — [Unity 2022.3 SkinnedMeshRenderer manual](https://docs.unity3d.com/2022.3/Documentation/Manual/class-SkinnedMeshRenderer.html)

### Inferences
- [inference] Mod actions: scan all `SkinnedMeshRenderer`s at raid start and set `updateWhenOffscreen=false` where the game enabled it (verify bounds via `localBounds`), set `quality = SkinQuality.Bone2` for distant characters, and `QualitySettings.skinWeights = SkinWeights.TwoBones` globally (runtime-settable) as an option. GPU skinning (PlayerSettings.gpuSkinning, compute-shader skinning on DX11) is chosen at build time; check the player log/boot.config for `gfx-enable-gfx-jobs`/skinning flags rather than assuming.
- [inference] TransformChangeDispatch cost scales with the number of transforms marked dirty per frame and the number of systems interested in them; deep hierarchies make each SetPosition dirty all children. Reduce by: avoiding writing the same transform value every frame (check-before-set in patched code), using `SetPositionAndRotation`/`SetLocalPositionAndRotation` (2021.3+) instead of separate writes, avoiding `SetParent` churn (use `SetParent(p, false)`), and keeping dynamic objects at root level.
- [inference] `boot.config` options worth inspecting: `gfx-enable-gfx-jobs`, `gfx-enable-native-gfx-jobs`, `job-worker-count`, `wait-for-native-debugger`; command line `-job-worker-count N` controls worker threads for OverlapSphereCommand/animation jobs.

### Gaps
- No source fetched documenting 2022.3 compute skinning behaviour or boot.config keys in detail.

## 9. Profiling a release Unity build from a mod

### Takeaway
ProfilerRecorder exists in release players but not all counters are available; check `.Valid` and enumerate with `ProfilerRecorderHandle.GetAvailable`. FrameTimingManager can be enabled for release builds. Stopwatch-based self-instrumentation via Harmony prefix/postfix is the universally available fallback.

### Cited Findings
- "Not all Profiler counters are available in Release Players"; use `ProfilerRecorder.Valid` or `ProfilerRecorderHandle.GetAvailable` to enumerate available stats; docs example uses `ProfilerCategory.Internal, "Main Thread", 15`. — [Unity ProfilerRecorder](https://docs.unity3d.com/ScriptReference/Unity.Profiling.ProfilerRecorder.html)
- Frame timing statistics can be enabled for release builds (example counter "CPU Main Thread Frame Time"). — [Unity manual: FrameTimingManager](https://docs.unity3d.com/Manual/frame-timing-manager-record-timing-data.html)
- Unity's Burst debugging docs: `lib_burst_generated` symbol files are what native profilers/debuggers need for Burst code. — [Unity Burst debugging/profiling tools](https://docs.unity.com/en-us/engine/6000.7/manual/programming-environment/script-compilation/burst/optimization-overview/debugging-profiling-tools.md)

### Inferences
- [inference] Practical toolkit: (1) Harmony prefix/postfix Stopwatch (`Stopwatch.GetTimestamp`, store in `__state` as long — no allocation) on suspect methods, aggregate per frame, display rolling averages; (2) `ProfilerRecorder` on available engine counters (GC alloc, memory, possibly "Main Thread" timing) validated at runtime; (3) native sampling profilers (Superluminal, Intel VTune, Windows Performance Recorder/ETW, PIX timing captures) work on release players for native frames; Mono JIT frames appear as unknown addresses unless the profiler supports Mono's perf-map/JIT symbol output — Superluminal advertises Unity Mono support [unverified here]; (4) converting EFT to a development player (swapping UnityPlayer.dll + boot.config `player-connection-debug=1`) is a known community trick to attach the Unity Profiler, but it changes performance and may violate game/BSG terms — test-only.

### Gaps
- Did not confirm which ProfilerRecorder counters (e.g. "Main Thread", "Animators.Update") are valid in a 2022.3 release player; testable in-game in minutes.
- Superluminal/Tracy Mono-symbol support claims not verified in this session.
