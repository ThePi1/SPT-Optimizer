# EFT client performance, BSG optimizations, SPT performance mods, and community settings knowledge (0.15–0.16 / 1.0 era)

Scope note: the target is EFT 0.16.9 (Unity 2022.3.43f1) under SPT 4.0.13. SPT 4.1.x now exists and targets a newer (1.0-series) client; several mods found are only listed for 4.1.6. Forge (forge.sp-tarkov.com) returned HTTP 525 to the fetcher; the mirror sp-mod.com served the same pages. The Fandom changelog returned 402, and Forbes returned 403, so some patch-note details come from third-party aggregators.

## Q1. Known EFT performance issues and what is known about their causes

### Takeaway
The community and the SPT project agree that EFT is CPU/main-thread bound. In SPT this is made worse because all bot AI runs on the local client, on the same main thread as rendering. Streets is consistently the worst map. Scopes are costly because the scene is rendered a second time. Large boss/guard spawns and high bot counts are the most cited in-raid spikes. No public, rigorous profiler breakdowns from modders were found; our own measurements are more detailed than anything public.

### Cited Findings
- The SPT wiki says SPT is worse than live because "all bot AI" is run locally, so the CPU bottlenecks and both GPU and CPU usage stay low. It suggests disabling bots as a diagnostic. It recommends strong single-thread CPUs and calls AMD X3D CPUs optimal — [SPT wiki Performance_Tuning.md](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- The same wiki gives the root cause of the headless option: "the game uses the same CPU thread for bot processing and rendering." Live raids offload bots to servers. A same-PC headless setup can split rendering and bots across cores, optionally with Process Lasso, but is unsupported and risks crashes and heavy pagefile use — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- The wiki lists "Remove mods that add new AI functions" and says bots are the main performance cost — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- In an SPT hub thread, a Ryzen 9 5900X / RTX 2080 Ti user reported 35–40 FPS on Streets while other maps ran higher. Commenters blamed a large boss + guard spawn at the start of the raid (10–20 guards by one count) on top of other AI. Fixes offered: lower AI spawn percentages in Donuts, or set boss spawn chance to 0 in SWAG. Another commenter said Declutter helped on other maps but was "negligible" on Streets. (Older, roughly 2024 / SPT 3.x) — [SPT hub thread 4209](https://hub.sp-tarkov.com/forum/thread/4209-modded-tips-to-increase-performance/)
- In a forum comment on the 0.15.0.0 patch notes, a user explains that scopes render the scene twice, which is why they are costly. Another commenter asks BSG to fix the scope FPS drop — [EFT forum 0.15.0.0 notes](https://forum.escapefromtarkov.com/topic/182464-patch-notes-for-01500/)
- After 0.15, a forum user reported Streets and Lighthouse dropping from 130+ to about 60–80 FPS, with further drops when the Goons spawn. This is anecdotal — [EFT forum 0.15 notes, page 3](https://forum.escapefromtarkov.com/topic/182464-patch-notes-for-01500/page/3/)
- After 0.16.0 (the Unity 2022 upgrade), Forbes reported that "many players noticed a drop in performance" and suggested the engine upgrade was likely involved. This is press inference, not a BSG statement — [Forbes, 2024-12-26](https://www.forbes.com/sites/mikestubbs/2024/12/26/escape-from-tarkov-patch-notes-add-hidden-extraction-engine-upgrade/); [Forbes follow-up "Patch Fails To Fix Major Tech Issues", 2024-12-28](https://www.forbes.com/sites/mikestubbs/2024/12/28/escape-from-tarkov-patch-fails-to-fix-major-tech-issues/) (403 to the fetcher; the claim was taken from the search snippet)
- Settings guides call the game "very CPU-dependent." One author sees 60–90% GPU usage, rarely above 80%, on a 9800X3D + RTX 5080 at 1440p, and calls a 4090/5090 "mostly wasted" at 1080p/1440p — [GEPC Performance 0.16 guide](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16)
- BSG's own in-game "Automatic RAM Cleaner" runs a garbage collection each time the player presses Escape in raid, causing about a 1 s freeze. Guides recommend it on, while avoiding Escape mid-fight. This fits EFT disabling GC in raid and collecting at controlled points — [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16); [GEPC 1.0](https://www.gepcperformance.com/post/escape-from-tarkov-1-0-best-settings-for-performance-clarity)
- Server-side SPT stutter source: vanilla SPT forces a blocking, compacting full GC whenever enough flea offers expire. The CompoundingPerf author measured 54 forced GCs skipped in one session, and the worst frame pause fell from about 8 s to about 1.7 s. This matters on a same-PC server — [CompoundingPerf (sp-mod mirror of Forge)](https://sp-mod.com/mod/2728/compoundingperf)
- EFT's own BotOwner hit-handling (`BotOwner.method_9`) was judged costly enough that the PerformanceImprovements author rewrote it as an experimental patch. A comment says it "did a lot of unnecessary checks before" — [CJ-SPT/PerformanceImprovements BotOwnerPatches.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Performance/Bots/Patches/BotOwnerPatches.cs)
- The 0.15 notes show audio occlusion was expensive enough to be moved off the main thread: "Transferred the whole algorithm to multithreaded calculations" — [EFT forum 0.15.0.0 notes](https://forum.escapefromtarkov.com/topic/182464-patch-notes-for-01500/)
- In 1.0.5.0 (May 2026), the animation system was optimized to lower CPU usage, and an audio-system memory leak was fixed. These are 1.0-series, not in 0.16.9 — [Fandom changelog via search snippet](https://escapefromtarkov.fandom.com/wiki/Changelog)

### Inferences
- Our measured profile (main-thread bound, about 1.7–2 ms Update and 1.2–1.6 ms culling) matches the community diagnosis. On a self-hosted client, bot AI is the dominant extra cost. Our headless-host numbers already remove most of it, which is why the remaining cost is mostly render submission and culling.
- BSG later optimized animation CPU (1.0.5.0) and player culling (1.0.4.x). That suggests animation and per-player rendering were known hot spots in 0.16-era code. Our 0.75 ms headless vs 1.9 ms self-hosted animation numbers are consistent with that.
- Scope PiP re-rendering doubles camera culling and draw submission while scoped. That is a likely spike our steady-state measurements may not capture.

### Gaps
- I found no public profiler analysis from modders or dataminers of BotsController, GPUInstancer grass, AreaLight, Physics, or inventory costs. Discord-only "Optimization Megathread" content (referenced by the SPT wiki) was not accessible.
- I found no quantified community analysis of the Unity 2022 upgrade's effect. There were only anecdotal "worse performance" reports.

## Q2. BSG's own optimization changes across patches (0.13–0.16 and 1.0)

### Takeaway
BSG's patch notes rarely give specifics. In the 0.15–0.16 era they mostly cover audio occlusion multithreading (0.15), FSR 3.0 (0.15), the Unity 2022.3.43f1 upgrade with no stated perf goals (0.16.0), Customs/memory/low-end shadow tuning (0.16.2), and Reserve optimizations (0.16.7). The real rendering and CPU work came in the 1.0 series (2026): a new player culling system per map, Streets geometry/environment optimization, and animation CPU reduction. None of the 1.0-series work is in the 0.16.9 client SPT 4.0.x uses.

### Cited Findings
- 0.15.0.0: Sound occlusion moved to multithreaded calculation, with refined occlusion depth/angle handling. FSR updated to 3.0, FSR 1.x removed, and FSR 2.2 artifacts fixed on Ground Zero and Streets. Culling changes are listed only as fixes ("visual bugs in geometry, textures and culling on all locations"). There is no dedicated optimization section — [EFT forum 0.15.0.0 notes](https://forum.escapefromtarkov.com/topic/182464-patch-notes-for-01500/)
- 0.16.0.0 (2024-12-26): "Unity was updated to 2022.3.43f1." It also added a physics-based reverb system on all locations, updated Meta XR Audio to 69.0.0, removed the binaural audio option, and reworked visual recoil. The first 100k characters of the notes have no explicit FPS/optimization section — [EFT forum 0.16.0.0 notes](https://forum.escapefromtarkov.com/topic/184196-patch-notes-for-01600/); [Forbes](https://www.forbes.com/sites/mikestubbs/2024/12/26/escape-from-tarkov-patch-notes-add-hidden-extraction-engine-upgrade/)
- 0.16.2.0: "a lot of optimization." Items: Customs performance improvements, lower in-raid memory use on all locations, and shadow settings tuned for low-end PCs. Streets remains the hardest map to run — [Prima Games 0.16.2.0 notes](https://primagames.com/tips/escape-from-tarkov-0-16-2-0-complete-patch-notes)
- 0.16.7.0: "changes to optimize the performance in various parts of Reserve," plus audio changes — [Sportskeeda 0.16.7.0](https://www.sportskeeda.com/esports/escape-tarkov-patch-notes-0-16-7-0-all-major-changes-audio-updates)
- 1.0.4.0 (2026-03-27): "Multiple improvements implemented to optimize client performance across all locations." An "updated player culling system" was added on Customs, Interchange and Reserve; it "has already been active on Streets of Tarkov." BSG says the effect depends on hardware and settings. Raid loading times were also reduced — [patched.gg 1.0.4.0](https://patched.gg/games/escape-from-tarkov/patch-1040); [Insider Gaming](https://insider-gaming.com/latest-tarkov-update-makes-game-easie/)
- 1.0.4.5 (2026-04-20): The new player culling system was added to Lighthouse, Shoreline, Woods and Ground Zero. Changes: environment and geometry optimizations on Streets; animation system and audio engine adjustments "for better resource usage"; off-raid screen optimizations; a higher menu FPS limit with a settings option; and fixes to culling, shadows, textures and geometry — [tarkov-stammtisch 1.0.4.5](https://tarkov-stammtisch.de/en/news/patch-notes-1045); [patched.gg 1.0.4.5](https://patched.gg/games/escape-from-tarkov/patch-1045)
- 1.0.5.0 (May 2026): Animation system optimized to lower CPU usage, and an audio memory leak fixed — [Fandom changelog (search snippet)](https://escapefromtarkov.fandom.com/wiki/Changelog)
- Older (2020, 0.12.4.6716): BSG shipped a fix for "Unity incorrectly setting some parameters in the config file during project build" (boot.config) and said they "don't have any noticeable boost in performance" — [Dexerto](https://www.dexerto.com/escape-from-tarkov/escape-from-tarkov-drop-april-12-update-to-fix-performance-issues-1352900/)

### Inferences
- BSG's 1.0 "player culling system" is functionally the same thing SPT bot limiters and our shadow/proxy culling approximate: not rendering or animating distant or occluded players. Backporting the idea (culling renderers, animators and skinned meshes of distant or occluded bots) is probably the highest-value client-side change not already in our mod.
- 0.16.9 already includes 0.16.2's memory and low-end shadow tuning and 0.16.7's Reserve work. It lacks all 1.0.4+ work.

### Gaps
- I could not find official BSG notes for 0.14.x Streets optimizations (Fandom was blocked). Treat Streets-specific 0.14 work as unverified.
- I found no BSG statement on what changed in culling, occlusion or GPUInstancer between 0.15 and 0.16.

## Q3. SPT/EFT performance mods: what they do, gains, status, SPT 4.x and Fika compatibility

### Takeaway
Most SPT "performance" mods attack AI cost by sleeping distant bots, through AI Limit, PerformanceImprovements' limiter, or Questing Bots' built-in limiter. They limit looting logic, reduce bot counts through spawner configs, or cut scene clutter and VRAM. Few publish measured gains. The best-documented measured gain is a server-side GC fix (CompoundingPerf). Fika generally disables client-side bot limiters, because bot state must be authoritative on the host.

### Cited Findings

**PerformanceImprovements (Dirtbikercj / CJ-SPT), latest Forge listing 0.2.4, SPT 3.10.5. No 4.x listing found.**
- Forge: version 0.2.4 adds dynamic optic resolution tied to the EFT scaling mode. It has a range-based AI limiter configurable in F12. The selected mode must match EFT's graphics setting or the image flickers. The AI limiter is disabled when Fika or Questing Bots is present. The author says scope resolution "should vastly improve fps when using scopes" (no numbers) — [Forge listing (search snippet)](https://forge.sp-tarkov.com/mod/1890/performance-improvements)
- The bot limiter prefixes `BotStandBy.Update` and checks each bot every 10 s. It sets `StandByType = paused` for bots beyond a per-map distance. Bots stay active if they are within distance minus 25 m (hysteresis), in combat (`Memory.HaveEnemy`) or healing (`FirstAid.Have2Do`). Per-type toggles cover scav, sniper, PMC and boss, with a cap (default "Max Active Bot Count" 16). It reuses EFT's own BotStandBy pause state rather than disabling components. Patches carry `[FikaDisablePatch]` and `[QBDisablePatch]` attributes. Code issues visible: a possible counter double-decrement and a throw if the map is not in the dictionary — [BotStandByPatch.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Performance/Bots/Patches/BotStandByPatch.cs); [BotsControllerPatches.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Performance/Bots/Patches/BotsControllerPatches.cs)
- Default per-map disable distances (m): Factory 50, Labs 75, Streets 150, Interchange/Reserve/GZ 175, Customs/Lighthouse 225, Woods/Shoreline 250 — [Settings.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Config/Settings.cs)
- Scope resolution: a super-sampling factor (default 0.5) while scoped, or a per-optic DLSS/FSR2/FSR3 mode. Off by default — [Settings.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Config/Settings.cs)
- A "SceneCleaner" deactivates (via SetActive(false), never destroys) objects whose names match garbage, heaps, spent cartridges, food and drink, decals, puddles, or shards. It skips any object with an ObservedLootItem, LootableContainer or WorldInteractiveObject component, and spreads the work one object per frame. The config bind is commented out in the current source, so the feature is inactive — [SceneCleaner.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Performance/Graphics/SceneCleaner.cs); [Settings.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Config/Settings.cs)
- An experimental patch replaces `BotOwner.method_9` (bot hit handling) and runs it through `Task.Run` on the thread pool. It touches Unity and game state off the main thread, which is a thread-safety risk — [BotOwnerPatches.cs](https://raw.githubusercontent.com/CJ-SPT/PerformanceImprovements/master/PerformanceImprovements/Performance/Bots/Patches/BotOwnerPatches.cs)
- Repo contents also show an "EFTProfiler" (ClassProfiler/AnalyticsModel), DeadBodiesController/StaticManager/BotEnemyChooser/BotsClass patches, an EnvironmentManager.Update patch, and a prepatcher — [GitHub tree API](https://api.github.com/repos/CJ-SPT/PerformanceImprovements/git/trees/master?recursive=1)

**AI Limit (dvize, maintained fork by asler/wizard83), BepInEx client mod.**
- It "temporarily disable[s] far bots to reduce cpu load." Only the N closest bots within a distance are enabled, set in BepInEx config. It is incompatible with Questing Bots, which has its own limiter — [Forge AI Limit (sp-mod mirror)](https://sp-mod.com/mod/572/ai-limit); [GitHub asler/SPT-AILimit](https://github.com/asler/SPT-AILimit)
- Forge shows version 1.8.6 for SPT 4.0.13 (search snippet). The mirror page showed 1.8.0 / SPT 3.9.8, with about 75.8K downloads. Fika compatibility is listed as unknown — [Forge AI Limit](https://forge.sp-tarkov.com/mod/572/ai-limit); [sp-mod mirror](https://sp-mod.com/mod/572/ai-limit)
- Anecdote: one user said it "saved my performance on Lighthouse and Streets" (2024) — [SPT hub thread 4209](https://hub.sp-tarkov.com/forum/thread/4209-modded-tips-to-increase-performance/)
- The SPT wiki recommends AI Limit but warns it affects gameplay. Questing Bots users should use QB's limiter instead — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)

**Questing Bots (dwesterwick), built-in AI limiter plus spawn system.**
- The limiter must be enabled in F12 and then per map. By default only Streets is limited. A questing bot can sleep only if it is more than 200 m from humans (global default; map-specific defaults are 1000 m, and the smaller applies). It must also be at least 75 m from other non-sleeping questing bots. Zero distances require `allow_zero_distance_sleeping` — [GitHub README](https://github.com/dwesterwick/SPTQuestingBots)
- Per-frame budgeting: `max_calc_time_per_frame_ms` defaults to 5 ms, and quest selection is capped at 1 ms per frame. Pathing updates every 100 ms by default (`bot_pathing_update_interval_ms`), and lowering it hurts performance. Full PMC lobbies are off by default "to improve performance." Many quest waypoints can affect performance — [GitHub README](https://github.com/dwesterwick/SPTQuestingBots)
- Incompatible: Phobos, ORBIT, AI Limit, and similar AI-disabling mods. Fika is supported with a FikaSync plugin on the host only. `debug.broadcast_debug_data_to_fika_clients` (on by default) significantly increases network traffic — [GitHub README](https://github.com/dwesterwick/SPTQuestingBots)
- 1.1.0-rc1: "slight performance improvements for the AI limiter" — [newreleases.io](https://newreleases.io/project/github/dwesterwick/SPTQuestingBots/release/1.1.0-rc1)

**Looting Bots (Skwizzy).** Version 1.7.0 appears in a 4.0.13 mod list.
- Performance settings: "Maximum looting bots" limits how many bots run looting logic at once (0 = unlimited). "Limit looting by distance to player" stops looting logic for bots beyond that distance. The loot scan interval activates the looting layer every 10 s by default. "Calculate value from attachments" is a "slightly more expensive check." "Use flea market prices" queries Ragfair at client start, which slows startup — [GitHub README](https://github.com/Skwizzy/SPT-LootingBots); [4.0.13 mod list](https://sp-mod.com/list/115765/4013)

**SAIN (Solarint)**, version 4.4.3 in a 4.0.13 mod list.
- v4.1.0 notes: "performance improved for large numbers of bots fighting across the map." Old presets were set up incorrectly, so users should check tooltips. No tick-rate setting names were found — [newreleases.io SAIN v4.1.0](https://newreleases.io/project/github/Solarint/SAIN/release/v4.1.0); [4.0.13 mod list](https://sp-mod.com/list/115765/4013)

**Waypoints – Expanded Navmesh (DrakiaXYZ)** and **BigBrain** (DrakiaXYZ), versions 1.8.2 and 1.4.0 in the 4.0.13 list.
- The SPT wiki lists Waypoints as a perf optimization: "Optimises AI pathfinding" — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md); [4.0.13 list](https://sp-mod.com/list/115765/4013)

**CompoundingPerf (EchoStarz)**, version 1.3.0, SPT 4.0.13, MIT, server-only as of 1.3.0.
- S8: runs flea expiry without the forced compacting full GC. Measured: 54 forced GCs skipped, worst pause about 8 s to about 1.7 s.
- S9: zlib Fastest instead of SmallestSize for HTTP responses ("several-fold less CPU").
- S7: one zero-alloc scan instead of five regex passes per response.
- S10: locks on handbook, base-class and blacklist caches to stop an intermittent concurrent-collection crash.
- Client log-filter patches were removed in 1.3.0. The listing says a "Fika compatible version" is available.
- Source: [sp-mod mirror of Forge](https://sp-mod.com/mod/2728/compoundingperf)

**VRAM Cleaner (matsix)**, version 1.0.3 in the 4.0.13 list.
- Recommended by the SPT wiki to free GPU VRAM. The wiki separately says the "RAM Cleaner Fix" will "at best" not help and may overuse the pagefile — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md); [4.0.13 list](https://sp-mod.com/list/115765/4013)

**CWX's MegaMod (GrassCutter, EnvironmentEnjoyer).**
- The wiki suggests it only "if you're severely GPU limited." It removes grass and environment detail — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)

**Dynamic Maps (mpstark)**, version 1.1.3 in the 4.0.13 list.
- The wiki says to disable the minimap for performance — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)

**Bot count via spawners (SWAG+Donuts, APBS/ABPS).**
- The wiki says to "spawn fewer bots" through your spawner. The hub thread suggests lowering Donuts spawn percentages or setting SWAG boss chance to 0 on Streets — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md); [hub thread](https://hub.sp-tarkov.com/forum/thread/4209-modded-tips-to-increase-performance/)

**Advanced Bot Culling.**
- It hid bot bodies outside BSG's culling radius. The author says "As of SPT 3.10 this mod is no longer needed," expects "very little, if anything" of a gain on modern hardware, and has stopped maintaining it — [hub file 2342](https://hub.sp-tarkov.com/files/file/2342-advanced-bot-culling/)

**Remove The Dead (jbs4bmx)**, version 4.0.1 in the 4.0.13 list.
- Removes corpses. The name was inferred from the list; no technical details were obtained — [4.0.13 list](https://sp-mod.com/list/115765/4013)

**Declutter mods.**
- Community: "helped on other maps but on Streets negligible" (SPT 3.x era) — [hub thread](https://hub.sp-tarkov.com/forum/thread/4209-modded-tips-to-increase-performance/)

**SPT 4.1.x-only perf mods (not for 0.16.9).**
- Performance Scope 0.2.0 (PJ568, SPT 4.1.6): adjusts in-scope resolution — [sp-mod](https://sp-mod.com/mod/3076/performance-scope)
- NoMenuFPSLimit 1.1.0 (Lacyway, 4.1.6): removes the 60 FPS menu cap — [sp-mod search](https://sp-mod.com/mods?query=fps)
- Escape From Low Frames 1.0.4 (NRK.Z, 4.1.6; Fika version available): an NVIDIA Profile Inspector (Revamped) preset package. No FPS numbers are given — [sp-mod](https://sp-mod.com/mod/2634/escape-from-low-frames)

**Amands's Graphics (Amands2mello)**, version 1.6.5 in 3.x lists.
- A graphics enhancer that removes haze and increases clarity. Its performance options were not found — [StarVox 3.11.3 list (search snippet)](https://starvox.notion.site/StarVox-s-SPT-3-11-3-Mod-List-1c047ac3697780b4aeadd0bedb706f4b)

### Inferences
- Every bot limiter reuses EFT's BotStandBy pause (or equivalent sleep) rather than disabling rendering. On a headless-host Fika client, bot AI is not running locally, so these limiters give little or nothing (and Fika disables them anyway). Our mod's render-side culling of remote players and proxies is the relevant lever there.
- PerformanceImprovements' 10 s per-bot check cadence with 25 m hysteresis, and Questing Bots' per-frame ms budget, are reasonable design references if we ever add AI throttling for the self-hosted case.
- PerformanceImprovements' SceneCleaner (name-substring match, loot-component exclusion, one SetActive per frame) is essentially the same approach as our optional declutter.
- CompoundingPerf's forced-GC finding is server-side. It only affects the client frame if the SPT server shares the CPU. It is worth recommending alongside our GC policy.

### Gaps
- No measured FPS or frametime numbers were found for AI Limit, the Questing Bots limiter, SAIN, Looting Bots limits, VRAM Cleaner, GrassCutter, declutterers, or corpse removal.
- I could not verify whether PerformanceImprovements has a 4.x port. Forge shows 3.10.5 only.
- No information was found on "FPS Booster," "Lag-free/Smooth AI," Fontaine's FOV mod perf, SPT-Realism perf notes, Tyrian's Declutterer internals, "Item Spawn limiter," "Shadow tweaks," or "LOD override" mods.
- SAIN's specific performance and tick-rate settings were not found. The tooltips and globalsettings JSON are the place to check.

## Q4. Community knowledge on graphics settings, boot.config, priority and affinity (incl. X3D)

### Takeaway
Community guidance is mostly qualitative and contradictory. Borderless windowed is strongly favoured over exclusive fullscreen, which matches our measured about 4 ms present wait. Object LOD, overall visibility and shadows are the CPU-relevant knobs. Textures and mip streaming are VRAM concerns. Reflex and DLSS/FSR only help when GPU-bound. The SPT wiki says boot.config edits give no performance gain. Its listed defaults already enable gfx jobs. "Physical cores only" and Process Lasso affinity advice conflicts, and there is no Tarkov-specific X3D CCD data.

### Cited Findings
- The SPT wiki says boot.config edits give "no performance improvement." The default should be `gfx-enable-gfx-jobs=1`, `gfx-enable-native-gfx-jobs=1`, `wait-for-native-debugger=0`, `hdr-display-enabled=0`, `gc-max-time-slice=3`, `single-instance=`, and `build-guid=…` — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- Contrary claim: a commercial guide recommends `gfx-enable-gfx-jobs=1` and `job-worker-count` set to logical cores minus 1, and claims 30–80% FPS. That figure bundles in-game and Windows changes and is unverified — [iqondigital guide](https://iqondigital.com/learn/games/optimize-escape-from-tarkov-performance)
- The SPT wiki says graphics settings give "minor gains." It recommends Reflex OFF and V-Sync OFF, Low or Medium textures, the Streets low-texture mode, and vaulting set to "Press" instead of "Auto" — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- Screen mode: GEPC calls Borderless "very important and will make a big difference in FPS." On Windows 11 it recommends enabling "Optimizations for windowed games." Disabling fullscreen optimizations lowered FPS in the author's testing — [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16). A cheat-vendor-affiliated guide instead recommends Fullscreen for input lag and steadier frametimes — [bigproject.cc](https://bigproject.cc/en/guides/tarkov-settings-fps-2026)
- Reflex: GEPC says to try On and On+Boost and keep whichever gives more FPS. It can "unlock" FPS, and G-Sync may cap at refresh. On AMD, tick "Disable game FPS limit" instead. The NVIDIA Low Latency Mode (global) may cause stutter — [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16). The SPT wiki says to disable Reflex — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- DLSS/FSR: no gain when CPU-limited, and may increase latency. DLSS was described in 1.0 as "bugged right now, does not help with FPS" and blurs scopes. FSR 3 Quality only if GPU usage is high — [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16); [GEPC 1.0](https://www.gepcperformance.com/post/escape-from-tarkov-1-0-best-settings-for-performance-clarity)
- "Higher settings/FOV give more FPS": GEPC states higher FOV gives "slightly" or "better" FPS in the current version. This is unexplained and unquantified — [GEPC 1.0](https://www.gepcperformance.com/post/escape-from-tarkov-1-0-best-settings-for-performance-clarity); [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16)
- Object LOD and visibility: guides converge on LOD about 2.0–2.5 (closer to 2.0 on weak CPUs) and visibility about 1000–1500. Higher LOD "hit[s] CPU harder on heavy maps." Unmeasured estimates: LOD +10–15 FPS, visibility +20–30 FPS, Low shadows +15–20 FPS. These are commercial or hobbyist guides with no methodology — [Tarkovforge](https://tarkovforge.com/blog/best-tarkov-graphics-settings-2025); [bigproject.cc](https://bigproject.cc/en/guides/tarkov-settings-fps-2026); [Switchblade Gaming](https://www.switchbladegaming.com/game-settings/escape-from-tarkov-best-pc/). GEPC says visibility 1500+ has only a slight impact on a recent GPU — [GEPC 1.0](https://www.gepcperformance.com/post/escape-from-tarkov-1-0-best-settings-for-performance-clarity)
- Mip streaming: guides disagree. One suggests buffer 32 / disk limit 768, others say leave it off — [search summary of Tarkovforge/LagoFast/Gameleap guides](https://tarkovforge.com/blog/best-tarkov-graphics-settings-2025)
- Textures are a VRAM issue. Use Medium unless 12 GB+ VRAM. Streets "can still cause problems" at higher settings, so enable "Streets of Tarkov Lower Texture Resolution Mode" — [GEPC 1.0](https://www.gepcperformance.com/post/escape-from-tarkov-1-0-best-settings-for-performance-clarity)
- PostFX is not recommended (can hurt FPS). NVCP guidance: Prefer maximum performance, texture filtering High performance, shader cache Unlimited — [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16)
- "Only use physical cores": GEPC says test both, "most of the time" OFF — [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16). Older SPT 3.9 guides recommend ON — [StarVox 3.9.8 (search snippet)](https://starvox.notion.site/StarVox-s-SPT-3-9-8-Mod-Guide-e829c138f12a42d480989fb5225046ee). A low-quality 9800X3D guide reports physical-core limiting caused a minor regression in 1% lows — [AFFL](https://affl.com/7-9800x3d-performance-guide-2026-2d5a.html)
- Process Lasso: one guide recommends affinity to physical cores plus High (not Realtime) priority, with in-game physical cores off. No benchmarks — [Evetech](https://evezone.evetech.co.za/performance-pulse/process-lasso-tarkov-fix-cpu-stutter). The SPT wiki mentions Process Lasso only for splitting a same-PC headless client and game across cores — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- X3D: GEPC recommends 5800X3D/5700X3D/7800X3D/9800X3D and says a properly tuned 9950X3D can match a 9800X3D — [GEPC 0.16](https://www.gepcperformance.com/post/escape-from-tarkov-settings-and-optimizations-2025-patch-0-16). For dual-CCD parts (7950X3D/9950X3D), generic reports: wrong-CCD parking was fixed through BIOS CPPC preferred-cores, the AMD 3D V-Cache Optimizer service, the Balanced plan and Game Mode; Game Bar pinning can cause stutter when the other CCD spikes — [overclock.net 9950X3D](https://www.overclock.net/threads/how-i-fixed-core-parking-on-my-9950x3d-and-taichi-lite.1815819/); [Steam HFW thread](https://steamcommunity.com/app/2420110/discussions/0/4292566217060325595); [AMD community 7950X3D](https://community.amd.com:443/t5/processors/why-not-use-cpu-sets-for-soft-affinity-on-7950x3d-instead-of-the/td-p/596038)
- Pagefile: SPT wiki recommends system-managed, with more than 30 GB free disk space for memory-crash avoidance — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md)
- Frame generation (NVIDIA Smooth Motion, AFMF, Lossless Scaling) is suggested by the SPT wiki, with added latency — [SPT wiki](https://github.com/sp-tarkov/wiki/blob/main/Performance_Tuning.md). One SPT user saw little or no difference with Lossless Scaling — [Steam discussion](https://steamcommunity.com/app/993090/discussions/0/4628106589631465352)

### Inferences
- Our measured about 4 ms present wait in exclusive fullscreen independently confirms the community's "Borderless gives big FPS" claim. Under borderless on Windows 10/11 with flip-model composition, present doesn't block the main thread in the same way. This is a strong default to ship as a recommendation.
- Gfx jobs are already on by default per the SPT wiki. That makes boot.config "gfx jobs" tweaks a no-op. Only `job-worker-count` and the GC time slice are real changes, and EFT disables GC in raid, so `gc-max-time-slice` matters mainly outside raid.
- Object LOD and visibility map directly onto our measured culling and draw-submission cost (135k LODGroups on Streets). They are the in-game settings most likely to move CPU frame time. Textures and mip streaming should be near-neutral on our CPU-bound profile unless VRAM is exhausted.
- On single-CCD X3D parts (7800X3D/9800X3D), affinity and physical-core tweaks have little upside and may hurt 1% lows. On dual-CCD X3D parts, keeping EFT (and on a same-PC setup, the headless or server process on the other CCD) pinned correctly is the plausible win. No Tarkov-specific measurements exist.

### Gaps
- I found no controlled, frametime-based benchmark isolating Object LOD, visibility, shadows, mip streaming, HBAO or Reflex on a CPU-bound EFT system. All FPS deltas found are unsourced guide estimates.
- I found no measured data for `job-worker-count`, process priority, or GC boot.config settings on 0.16.
- I found no Tarkov-specific X3D CCD-affinity measurements. The Reddit r/EscapefromTarkov and r/SPTarkov threads did not surface through search.
