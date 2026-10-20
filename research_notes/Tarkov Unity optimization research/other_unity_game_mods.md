# Performance Mods for Other Unity Games: Techniques Transferable to EFT (SPT/Fika)

Research date: 2026-10-20. Scope excludes EFT/SPT/Fika-specific mods. Most mod pages are self-reported claims with no independent benchmarks; this is flagged throughout. The only rigorous before/after numbers found are from KSPCommunityFixes and Facepunch (Rust) devblogs.

## Valheim: what do its performance and networking mods change?

### Takeaway
Valheim mods split into (a) client CPU/GPU optimizers (ValheimPerformanceOptimizations) that rewrite hot paths, cache data, cull lights, particles and audio outside view, time-slice expensive renders and push math into Burst jobs, and (b) server-side networking mods that throttle distant-object (ZDO) sync, compress traffic, scope RPCs by area and move AI simulation to the server. Networking mods conflict with one another, and the main visible side effect is coarser motion or lag for distant or throttled objects.

### Cited Findings
- ValheimPerformanceOptimizations (ontrigger, GitHub) is presented as making no gameplay change: its Nexus page says it "does not disable, modify, throttle or otherwise change vanilla behavior." — [Thunderstore/Nexus search summary](https://www.nexusmods.com/valheim/mods/3303); [GitHub](https://github.com/ontrigger/ValheimPerformanceOptimizations)
- VPO techniques listed in its README — [GitHub README](https://github.com/ontrigger/ValheimPerformanceOptimizations):
  - ZNetScene object-streaming rewrite. Creating and destroying networked world objects has "no per-frame overhead" when no work is needed.
  - Faster server ZDO ownership scans.
  - Caching of ZDO int values in VisEquipment (character equipment visuals), plus an allocation fix.
  - Burst-compiled jobs for water wave math (floaters, fish, surface queries).
  - Burst-compiled terrain vertex-color generation.
  - Optional, experimental threaded terrain collision baking. Its documented failure mode is that terrain can disappear.
  - Light flicker updates skipped for point lights outside the camera.
  - Reflection cubemap faces rendered across several frames (time-slicing).
  - Fire particles not rendered outside the view frustum; particle culling and GPU instancing.
  - Looping AudioSources (ZSFX) culled when out of audible range.
  - Cached WearNTear structural-support results.
  - Optional cap on physics steps per frame (default 8, range 5–15), traded against physics accuracy.
  - No benchmarks are given.
- VPO 1.2.3 fixed infinite loading when entering dungeons and notes vanilla was "incorrectly destroying non owned zdos". This is a reminder that object-streaming rewrites touch ownership semantics. — [GitHub](https://github.com/ontrigger/ValheimPerformanceOptimizations)
- FiresGhettoNetworking 1.3.10 (server-focused) — [Thunderstore](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresGhettoNetworking/v/1.3.10/):
  - Distant objects beyond about 500 m sync at a lower rate; "Combat-range objects stay full speed".
  - Distant AI runs its physics update at half rate.
  - ZSTD compression with a dictionary, and ZDO delta compression.
  - Position-based RPCs go only to peers within range.
  - WearNTear support recalculation is skipped for full-health or invulnerable pieces.
  - Server-authoritative AI simulation.
  - Steam send rate raised to 1024 KB/s, versus about 150 KB/s in vanilla.
  - Side effects: distance throttling can be visible to nearby players, interpolation adds render lag, and prediction can overshoot at low ping.
  - It conflicts with BetterNetworking and Serverside Simulations.
  - Moving to server authority broke raids, which later had to be fixed.
  - It credits BetterNetworking, Serverside Simulations and Comfy's BetterZeeRouter.
  - No benchmarks; the gains are qualitative ("Big CPU win").
- SkadiNet 1.0.4 (server):
  - Adaptive ZDO scheduling.
  - Suppresses "tiny, repeated position and rotation updates" for non-critical ZDOs.
  - Ownership handoff based on peer quality, with cooldown and hysteresis to limit ownership churn.
  - Negotiated compression that turns itself off per peer after a failure.
  - Area-of-interest routing only for whitelisted visual RPCs (DamageText, TalkerSay); all other RPCs keep vanilla routing.
  - A client-side guard that delays coalesced garbage collection.
  - No benchmarks.
  - [Thunderstore](https://thunderstore.io/c/valheim/p/sighsorry/SkadiNet/v/1.0.4/)
- MagiCorp Valheim Optimizer reduces ZDO transfer volume, but its full benefit requires the same version on the server and every client. — [Thunderstore](https://thunderstore.io/c/valheim/p/MagiCorp/ValheimOptimizer/)
- Nexus mod 3303, an all-in-one optimizer, replaces VPO, Smoke_Collision and VBNetTweaks and warns against running it alongside overlapping mods. — [Nexus](https://www.nexusmods.com/valheim/mods/3303)

### Inferences
- Several of these ideas map directly onto EFT:
  - Culling light flicker, audio and particles outside the view.
  - Time-slicing reflection probes.
  - Caching per-character equipment lookups.
  - Capping physics catch-up steps; this is analogous to your "runaway physics" skip.
  - Whitelisting only cosmetic RPCs for area-of-interest filtering, which keeps gameplay-critical messages on vanilla routing. This is a safe pattern for Fika.
- Distance-tiered sync (full rate near combat, reduced when far) is the Valheim equivalent of AI or network LOD. The lesson is to keep anything in combat range at full fidelity.

### Gaps
- No measured FPS or ms gains for any Valheim mod were found.
- Valheim Plus's own performance options were not checked.
- VPO's CHANGELOG (with dates) was not retrieved.

## Rust (Facepunch): official optimization history

### Takeaway
Rust's biggest documented CPU win for populated scenes was client-side culling of other players (stopping their animator, skinning and LOD processing when they cannot be seen), worth 20–30% FPS. Other wins came from pooling, GC-allocation removal, impostors/billboards, LOD tuning, atlasing and graphics jobs. The custom CPU occlusion culling was sometimes a net loss.

### Cited Findings
- Devblog 122 (Aug 2016): visible-player cost on populated servers ("LOD selection, animators, state machines, skinning") was "upwards of 10 ms" of CPU. — [Devblog 122](https://rust.facepunch.com/news/devblog-122)
- How the client-side player culling works — [Devblog 122](https://rust.facepunch.com/news/devblog-122):
  - It raycasts from each player to the camera and turns off players who cannot be seen.
  - Players beyond `maxPlayerDist` are disabled, and sleepers beyond `maxSleeperDist`.
  - Players closer than `minCulldist` are force-enabled.
  - Players are force-enabled if you aim toward them or they aim toward you.
  - Players behind the camera are disabled.
  - Reported gain: "20-30% performance gains on populated servers."
  - Kill switch: `playercull.enabled false`. The aim-direction rule was added after players popped in and out during fights.
- Same devblog: player models were rebuilt every time a wearable changed, including condition loss, which caused micro-stutters. The fix compares clothing before and after a network update and rebuilds only on a real change. — [Devblog 122](https://rust.facepunch.com/news/devblog-122)
- Later changelist: pooling for player collision, model and nametag objects; optimized runtime player-model LOD assignment and frame updates; animators "warmed up" so they snap to the correct state when re-enabled. — [Changelist 2672 via search](https://rust.facepunch.com/changelist/2672)
- "The Performance Update" (6 Sep 2018) — [Rust blog](https://rust.facepunch.com/blog/the-performance-update):
  - Unity graphics jobs (multithreaded rendering) enabled; they say Rust is "no longer CPU-bound on most hardware".
  - GC allocations fixed in ambient sounds, image effects, prefab pooling and HUD.
  - Prefab pools warmed during loading to avoid first-use hitches.
  - A specialized tree-impostor renderer that "nearly eliminates impostor CPU cost".
  - A Max Tree Meshes limit: the nearest trees render as meshes, the rest as billboards.
  - CullingGroups for ambience updated less often.
  - LOD distances tightened on dozens of small objects.
  - Swamp-tree texture atlas halved their draw calls.
  - A nametag hotfix gave about 10–20% FPS on slow CPUs.
  - AI sensory modules update at different frequencies.
  - Occlusion culling became a toggle; Facepunch admits its CPU cost can outweigh its GPU benefit.
  - Physics runs at 32 Hz; the camera moves and rotates every frame for smoothness.
- Unity's built-in occlusion culling could not be used because Rust maps are procedural. The custom system initially did not improve performance (Devblog 140), and Hi-Z GPU occlusion was hampered by slow GPU-to-CPU readback (Devblog 137). — [Devblog 140](https://rust.facepunch.com/news/devblog-140); [Devblog 137](https://rust.facepunch.com/news/devblog-137)

### Inferences
- The player-culling rule set (distance, behind the camera, force-enable when close or when aiming at each other) is a ready-made template for EFT bot/player animation and render culling. EFT's animation cost (0.75–1.9 ms) is the same category of cost Rust cut.
- The "aim at me" force-enable rule matters for fairness in PvP and co-op: never hide or freeze something that could be shooting you.
- Warming animators on re-enable prevents T-pose or stale-pose pops.
- CPU-side occlusion can cost more than it saves. Measure before adding it on top of Unity's culling, which already costs EFT 1.2–1.6 ms.

### Gaps
- No Oxide plugin specifically aimed at reducing client or server load was researched in detail.
- No recent (2022+) Rust devblogs on GPU occlusion or Burst were retrieved.

## RimWorld: Performance Fish, Performance Optimizer, RocketMan

### Takeaway
RimWorld's mods use three main levers:
- Equivalent rewrites of hot methods (Performance Fish; its patch details live in the source, not the README).
- Caching or throttling around 20+ vanilla methods and faster component lookups (Performance Optimizer: 20–35% claimed).
- Stat caching plus "time dilation", which ticks distant or idle pawns and animals at lower rates (RocketMan: claimed +30 TPS for the cache, +150 TPS for dilation).

Many of the gains were later absorbed into vanilla 1.6. Throttling and caching mods have stale-cache and desync risks.

### Cited Findings
- Performance Fish "patches various methods for improved efficiency, while keeping functionality identical". Every patch has an in-game toggle and is fully unpatched immediately when disabled. It requires Prepatcher (which allows injecting fields into game types, useful for per-object caches) and Fishery. It is listed as incompatible with RimThreaded and Better GC, among others, and stores nothing in saves. — [GitHub](https://github.com/bbradson/Performance-Fish)
- Performance Optimizer (Taranchuk) — [GitHub](https://github.com/Taranchuk/PerformanceOptimizer):
  - A faster `GetComp` replacement.
  - "About 20+ vanilla methods are cached or throttled", some recalculated only every N refreshes.
  - UI elements hidden until hovered.
  - Optional sound disabling.
  - Claimed "about 20–35%" gain depending on mod list, with little benefit if TPS is already at target.
- RocketMan (per its Steam description, as summarized in search results):
  - Caches stat results (for example equipment protection) that vanilla recomputes every tick, cutting about 90% of that work, for a claimed ~+30 TPS.
  - "Time dilation" lowers the tick rate of animals and pawns from 60 Hz to 5 Hz, claimed ~+150 TPS. These are the author's own claims.
  - A warmup system, and an API for other mods to clear the stat cache.
  - Community comments say it is broken or redundant on 1.6, with key parts now in vanilla; this is disputed.
  - [Steam discussions](https://steamcommunity.com/workshop/filedetails/discussion/2479389928/3150808675349205760); [Steam comments](https://steamcommunity.com/sharedfiles/filedetails/comments/2479389928)
- The MissileGirl fork keeps stat caching with an "adaptive training" phase that learns how often each stat needs updating (runs about 45 minutes, then switches off). — [smods catalogue](https://catalogue.smods.ru/archives/433158)
- Multiplayer: the RimWorld Multiplayer mod requires identical mod lists and load order. One user reported that turning off RocketMan's cache and throttle settings removed red errors. A 2023 report shows a RocketMan patch warning about GlowGridHelper being out of sync. All anecdotal. — [Steam discussion](https://steamcommunity.com/workshop/filedetails/discussion/2479389928/3802776070761713726); [rimworldhub guide](https://rimworldhub.com/post/rimworld_desyncs_lets_get_back_in_sync)

### Inferences
- The "tick distant agents at a lower rate" idea (RocketMan time dilation, Valheim distant AI at half rate) is the most transferable to EFT bots. In Fika, apply it only on the authority that simulates the bot (the host), or only to visual and animation work on clients. Otherwise it changes AI decisions per machine.
- Provide per-patch toggles plus clean unpatching (as Performance Fish does) to triage compatibility reports.
- Adaptive caches need invalidation hooks for other mods; stale caches are the common failure mode.

### Gaps
- Per-patch internals of Performance Fish (which methods, transpiler vs prefix) were not extracted. The source folder would need code review.
- No independent benchmarks were found for any RimWorld performance mod.

## Kerbal Space Program, Cities: Skylines, Subnautica, Lethal Company, 7 Days to Die, and others

### Takeaway
KSPCommunityFixes is the best-documented example: dozens of narrowly scoped Harmony patches (caching, interval throttling, allocation removal, batching per-vessel work, skipping updates when UI or features are hidden) together gave +32–86% average FPS in measured runs. In other games the common wins are:
- Per-layer cull distances (Subnautica).
- Interior/portal culling (Lethal Company's CullFactory).
- Caching repeated `FindObject`-style searches and trimming allocations (LethalPerformance).
- Removing decorative clutter (7 Days to Die).

### Cited Findings
- KSPCommunityFixes measured gains (v1.37.0 vs stock, same craft, 720p) — [GitHub README](https://github.com/KSPModdingLibs/KSPCommunityFixes):
  - Ryzen 5800X3D: average FPS on the launchpad 46.4 → 69.4 (+50%); low Kerbin orbit 49.5 → 92.0 (+86%); 1% lows +41–72%.
  - Pentium N5030: +32–58% average.
- KSPCommunityFixes patch types with direct analogues — [GitHub README](https://github.com/KSPModdingLibs/KSPCommunityFixes):
  - **OptimizedModuleRaycasts**: avoids extra physics state syncs before raycasts and caches solar-panel raycast results. Analogous to Unity `Physics.SyncTransforms` / `autoSyncTransforms` cost.
  - **CollisionManagerFastUpdate**: 3–4x faster inter-part collision update.
  - **CommNetThrottling**: updates CommNet every 2.5 in-game seconds instead of continuously.
  - **DisableMapUpdateInFlight**: skips orbit-line updates when the map is hidden.
  - **DisableHiddenPortraits**: stops rendering hidden crew-portrait cameras.
  - **ActiveRadiatorPerf**: per-module updates replaced by a batched per-vessel integrator.
  - **OnDemandPartBuoyancy**: skips buoyancy when unnecessary.
  - **MemoryLeaks**: removes event delegates of destroyed objects on scene switches.
  - **IMGUIOptimization**: removes OnGUI GC allocations.
  - **FasterPartFindTransform**: faster transform finds.
  - **GameDatabasePerf**: dictionary-backed lookups.
  - **RestoreMaxPhysicsDT**: avoids extra FixedUpdates per frame.
  - **FastLoader**: multithreaded asset loading with optional DXT5 texture cache, 20–40% faster loading.
- KSPCF 1.35.1 notes a bug in which cached PNG loading re-read files synchronously instead of using the threaded reader data; 1.35.2 fixed a regression that caused empty mipmaps. This is a reminder that caching patches regress. — [Release 1.35.2](https://github.com/KSPModdingLibs/KSPCommunityFixes/releases/tag/1.35.2)
- Cities: Skylines Ultimate Level of Detail (boformer) only multiplies the distance at which objects switch to LOD for all objects. Users report FPS drops when it is raised. A commenter argues that size-relative distance scaling (Visibility Control / Prop Control) is more efficient than a global multiplier; this is opinion. The last changelog entry is May 2021. — [Steam Workshop comments](https://steamcommunity.com/sharedfiles/filedetails/comments/1680642819)
- Subnautica:
  - A Nexus mod limits base and vehicle render distance (vanilla draws them up to about 1700 m) to about 200 m underwater and 1000 m above. The author reports "sometimes double" FPS when looking at distant bases. It is a work in progress, with water-surface artifacts below 100 m. — [Nexus 389](https://nexusmods.com/subnautica/mods/389)
  - Below Zero's "Fabulous Optimization" uses Unity's native `Camera.layerCullDistances`, with separate surface and underwater distances. — [search summary, Nexus BZ](https://www.nexusmods.com/subnauticabelowzero/mods/716)
- Lethal Company (BepInEx 5, HDRP):
  - LethalPerformance (DiFFoZ, v1.5.3) caches frequently searched game, network and level objects to avoid repeated object searches, and reduces allocations and main-thread work.
  - It schedules saves, tunes HDRP defaults (reflection-probe cache), and pre-generates ProBuilder meshes and instantiates dungeon tiles asynchronously.
  - It ships Burst code; antivirus blocking `lib_burst_generated.data` causes a black screen.
  - No benchmarks.
  - [Thunderstore](https://thunderstore.io/c/lethal-company/p/DiFFoZ/LethalPerformance/changelog)
  - CullFactory culls interior geometry the player cannot see and is described as the "biggest FPS win in interiors". — [AurelionPack listing](https://thunderstore.io/c/lethal-company/p/PAXANDDOS/AurelionPack_Performance/)
  - LethalBoost advertises AI throttling and LOD, with camera culling off by default. Its page returned 404 when fetched, so details are unverified. — [Thunderstore](https://thunderstore.io/c/lethal-company/p/Obrigashki/LethalBoost/)
- Paralives' ParaPerf (BepInEx 5) claims about 96% less GC stutter from AI logic, culls off-screen mirror renders and trims per-frame allocations. Self-reported. — [Nexus](https://www.nexusmods.com/paralives/mods/268)
- 7 Days to Die:
  - Performance+ removes decorative clutter (grass, rubble, gore piles) for FPS and requires a new save. — [Nexus 7170](https://nexusmods.com/7daystodie/mods/7170)
  - No Harmony-based AI-optimization mod was found. Server guides point to zombie caps (BloodMoonEnemyCount is per player) as the lever. — [esagames guide](https://esagames.ro/how-to-reduce-lag-on-a-7-days-to-die-server.php)

### Inferences
- High-value, low-risk ideas for EFT drawn from these:
  - `Camera.layerCullDistances` per layer (small props, foliage, debris) for culling cost and draw submission.
  - Disabling secondary cameras (scopes, portraits, mirrors) and reflection updates when not visible.
  - Caching `FindObjectsOfType`/`GetComponent` results in hot Update loops.
  - Removing redundant `Physics.SyncTransforms` before raycasts.
  - Throttling non-critical managers (UI, map, comms-like systems) to fixed intervals.
- Like KSPCF, many micro-patches with per-patch toggles add up (+50% or more in KSP). Expect no single silver bullet in a main-thread-bound game.

### Gaps
- Loading Screen Mod, Render It, Kopernicus and Sigma were not researched.
- No 7 Days to Die AI-throttling mod was found.
- No Unity-generic "UnityPerfOptimizer"-style framework or DXVK-async data was found in this pass.

## Techniques for CPU-bound Unity games with many NPCs, and multiplayer-safe lessons

### Takeaway
The proven pattern for many characters:
- Cull or freeze off-screen and far characters' animators, skinning and LOD selection (Rust: 20–30%).
- Update distant characters' AI and animation at reduced rates (RocketMan 60 → 5 Hz; Valheim distant AI physics at half rate).
- Keep force-enable rules for close or threatening entities.

In multiplayer, do these throttles only on the authoritative simulator, or only to purely visual work. Restrict network filtering to cosmetic RPCs. Expect conflicts between networking mods and require matching versions when wire formats change.

### Cited Findings
- Unity `AnimatorCullingMode`: `CullUpdateTransforms` disables retargeting, IK and transform writes when renderers are invisible; `CullCompletely` disables animation entirely when invisible. — [Unity docs](https://docs.unity3d.com/ScriptReference/AnimatorCullingMode.html)
- General animation-LOD guidance (non-authoritative industry blog):
  - Update distant characters every 2nd, 4th or 8th frame and hold the last pose in between.
  - Use reduced tiers (smaller blend trees, no IK, no additive layers).
  - Prefer screen coverage over raw distance as the metric.
  - [MocapOnline guide](https://mocaponline.com/blogs/mocap-news/animation-lod-performance-guide)
- Rust: invisible players cost animator, state machine, skinning and LOD work, more than 10 ms on busy servers. The client-side culling rules gave 20–30%. Animators are warmed so they snap to the correct state on re-enable. — [Devblog 122](https://rust.facepunch.com/news/devblog-122); [Changelist 2672](https://rust.facepunch.com/changelist/2672)
- Rust AI sensory modules update at different frequencies. — [Performance Update](https://rust.facepunch.com/blog/the-performance-update)
- Multiplayer and host/client lessons from Valheim:
  - Server-only features are auto-disabled on clients, and server gains benefit unmodded clients.
  - Client-installed parts are limited to interpolation and tuning.
  - Throttling is visible to nearby players, so combat range stays at full rate.
  - Ownership changes need hysteresis and cooldown to avoid churn.
  - Compression is negotiated and falls back on failure.
  - Mods that change the wire format need the same version everywhere.
  - Moving to server authority broke raid events.
  - Sources: [FiresGhettoNetworking](https://thunderstore.io/c/valheim/p/VerdantsAscent/FiresGhettoNetworking/v/1.3.10/); [SkadiNet](https://thunderstore.io/c/valheim/p/sighsorry/SkadiNet/v/1.0.4/); [MagiCorp Optimizer](https://thunderstore.io/c/valheim/p/MagiCorp/ValheimOptimizer/)
- RimWorld Multiplayer requires identical mod lists. Caching and throttle mods are anecdotally linked to errors and desyncs. — [rimworldhub](https://rimworldhub.com/post/rimworld_desyncs_lets_get_back_in_sync); [Steam](https://steamcommunity.com/workshop/filedetails/discussion/2479389928/3802776070761713726)

### Inferences
- For EFT/Fika, a safe split:
  - **Visual-only on every client:** Animator culling/LOD for bots and remote players, skinned-mesh and LOD culling, IK off at distance. Always force-enable bots that are close, in line of sight, aiming at the player, or recently fired, following Rust's rules.
  - **Host-only:** AI think-rate LOD for distant bots.
  - **Never on clients:** any throttling of hit registration, ballistics or authoritative physics.
- The exact `AnimatorCullingMode` choice matters. `CullCompletely` can break gameplay code that reads bone transforms (weapon aim, hit-box positions). If gameplay depends on bone positions, use `CullUpdateTransforms` or reduced-rate manual `Animator.Update`. This is an inference from the Unity docs, not tested in EFT.

### Gaps
- No public measured data was found on IK-disabling at distance, or on bone-LOD gains in shipping Unity mods.
- No documented case was found of a specific client-side performance mod causing competitive unfairness. Evidence is limited to desync anecdotes.
