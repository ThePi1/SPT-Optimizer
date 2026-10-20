# Project Fika architecture and what it means for client-side performance mods

Source note: Most findings come from reading the Fika source directly (Fika-Plugin `main` = v2.4.6, plus the `v2.3.9` tag, which is the user's version), Fika-Headless `main`, Fika-Server-CSharp `main`, and the official GitBook wiki repo (`project-fika/gitbook-wiki`, published at wiki.project-fika.com). Line numbers refer to the files as fetched on 2026-10-20.

Version caveat: **Fika 2.3.9 (released 2026-07-28, EFT 0.16.9.40087) is the last SPT 4.0.x release.** v2.4.0 (2026-08-03) says "Updated to SPT 4.1.X" — [Fika-Plugin releases](https://github.com/project-fika/Fika-Plugin/releases). Some performance work, notably the "IK Cull Distance" setting and 3-frame IK staggering, arrived in v2.4.4 and is **not** in the user's 2.3.9. These differences are flagged below.

Abbreviations: "host" = the raid server peer (FikaServer: a player's game or the headless). "client" = FikaClient peer. "SPT.Server" = the backend HTTP server (profiles, mod validation), which is a separate thing from the raid host.

---

## 1. Roles and authority: host vs client vs headless

### Takeaway
Fika uses a client↔host↔client UDP model. The raid host is a full EFT game instance. It runs the real BotsController, so all bot AI, bot movement and bot spawning happen there. Clients see bots as `ObservedPlayer` puppets driven by 10/20/30 Hz state snapshots. Hit registration is split: a human player's shot is resolved on the **shooter's** machine, which sends a `DamagePacket`. A bot's shot is re-simulated on every machine, and each victim's own machine resolves the damage to it. Grenades and loose-loot physics are simulated by the host and smoothed on clients. Inventory actions (with "Strict Inventory Sync") are approved by the host. The headless is simply a host with no local player and its graphics removed.

### Cited Findings
- The wiki describes Fika's gameplay model as "`Client <-> Server <-> Client` UDP" (BepInEx plugin + SPT server mod). — [Fika wiki: General information](https://github.com/project-fika/gitbook-wiki/blob/main/General-information.md)
- Headless: it is "basically another SPT+Fika client instance that will automatically host raids. All players becomes clients (including the player who clicked `HOST RAID`)", and its purpose is to "offload the AI calculation and other resource-intensive task". — [Wiki: Headless Client](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/README.md)
- In code, the comment on `FikaBackendUtils.IsHeadless` says "Headless clients are always raid hosts (IsServer is always true)". — [FikaBackendUtils.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Utils/FikaBackendUtils.cs)
- **Bot AI only runs on the host.** `HostGameController` owns `_botsController` (BotsController/BotSpawner, boss spawn scenario, `StopBotsSystem`) and a `BotStateManager`. `ClientGameController` has no BotsController. — [HostGameController.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/GameMode/HostGameController.cs), [ClientGameController.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/GameMode/ClientGameController.cs)
- Host bots are `FikaBot` instances. The host's `BotStateManager` batches their state, sending at `_updatesPerTick = 1f / server.SendRate`. — [BotStateManager.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Components/BotStateManager.cs)
- On clients, `CoopHandler.SpawnPlayer` creates remote humans and bots as `ObservedPlayer.CreateObservedPlayer(..., isAi, EUpdateQueue.Update, armsUpdateMode: Player.EUpdateMode.Manual, bodyUpdateMode: Player.EUpdateMode.Auto, ...)`. Bots get the "Bot_" prefix and `aiControl=true`, but `AIData` is built with a null BotOwner (`new AIData(null, player)`). — [CoopHandler.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Components/CoopHandler.cs) (≈L465), [ObservedPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L329–432)
- **Shooter-side hit registration for player shots.** On a client, `ObservedClientBridge.ApplyShot` only resolves damage when `damageInfo.Player.iPlayer.IsYourPlayer`, by calling `ObservedPlayer.ApplyClientShot`. That method computes armor (`ProceedDamageThroughArmor`), applies the damage locally, and sends `DamagePacket` (ReliableOrdered) to the host. Any other shot hitting an observed player only plays `ShotReactions` and `ApplyHitDebuff`, which are cosmetic. — [ObservedClientBridge.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/ObservedClasses/PlayerBridge/ObservedClientBridge.cs), [ObservedPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L932–970)
- On the host, `ObservedHostBridge.ApplyShot` applies damage when the shooter is the host's own player **or an AI**. Sniper damage, landmines and artillery are handled host-side. — [ObservedHostBridge.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/ObservedClasses/PlayerBridge/ObservedHostBridge.cs)
- **Bot bullets hitting the local player are resolved by the victim's machine.** `FikaPlayer.ApplyShot` calls `SimulatedApplyShot` (armor plus damage) when `damageInfo.Player.IsAI` or the damage is Sniper/Landmine. For shots from other humans it returns `null`, because that damage arrives via the shooter's DamagePacket. — [FikaPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/FikaPlayer.cs) (≈L746–790)
- **Every client re-simulates every observed shot.** `ObservedFirearmController.HandleShotInfoPacket` calls `InitiateShot(Item, ammo, packet.ShotPosition, packet.ShotDirection, ...)`, which runs local ballistics for bots' and other players' bullets. — [ObservedFirearmController.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/ObservedClasses/HandsControllers/ObservedFirearmController.cs) (≈L391–600)
- **Grenades are host-simulated.** Clients spawn `FikaClientGrenade : ObservedGrenade`. Each `GrenadeSyncPacket` is lerped in on `WaitForFixedUpdate` (smoothing 10/s), and the final position/velocity are snapped when `Done`. — [FikaClientGrenade.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/ClientClasses/FikaClientGrenade.cs)
- **Thrown or dropped loot physics** are synced with `ItemPositionSyncer`, which records position, rotation, velocity and angular velocity until the rigidbody is gone. It feeds `LootSyncPacket`s to the host world or client world. — [ItemPositionSyncer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Components/ItemPositionSyncer.cs)
- Airdrops have patches on the server-side offline logic (`ServerAirDrop_UpdateOfflineClientLogic_Patch`, `ServerPlane_...`). BTR has its own patch set and packets (`BTRInteractionPacket`, `SpawnBTRPacket`, `BTRViewSynchronizer`). — [Fika-Plugin file tree: Main/Patches/Airdrops, Main/Patches/BTR](https://github.com/project-fika/Fika-Plugin/tree/main/Fika.Core/Main/Patches)
- **Inventory authority:** the wiki's headless mod guide says "the raid host is authoritative on inventory actions". The v2.3.5 release added "Strict sync": "you cannot shoot/reload/switch weapons unless the server host thinks you can… Only the host's setting will be applied for the raid". — [Wiki: mod guidance for headless](https://github.com/project-fika/gitbook-wiki/blob/main/faqandguides/headless-client-faq-and-common-issues/guidance-on-which-mods-should-be-used-on-the-headless-client.md); [v2.3.5 release notes](https://github.com/project-fika/Fika-Plugin/releases/tag/v2.3.5)
- Lights and switches ("Turnables") are "always synced from the server to clients" (since v2.3.5). — [v2.3.5 release notes](https://github.com/project-fika/Fika-Plugin/releases/tag/v2.3.5)
- `CoopHandler` on the host resyncs the full character list every 15 s in raid (5 s before start) with `CharacterSyncPacket`. — [CoopHandler.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Components/CoopHandler.cs) (≈L186–222)

### Inferences
- **Your local player's movement and character controller are client-authoritative.** The client simulates its own player and sends state snapshots, and remote peers only interpolate. No source shows host-side movement validation for human players.
- The BTR gunner in the user's log ("parked off-map") is a host-side bot. On the client, the BTR gunner is an `ObservedPlayer` puppet. A "skip runaway physics moves" patch therefore affects a puppet on the client. That is safe for the client, but it only fixes the root cause when installed on the host/headless.
- Batching OverlapSphere queries for character controllers is safe on the client: it affects the local player and observed players' trigger-collider searches (`UpdateTriggerColliderSearcher`), not authority. Changing hit or collision results for the **local** player would change what that client reports, because the shooter is authoritative.
- Disabling far shadow-only renderers, zero-intensity lights and small visual-only objects only changes visuals. One caveat: an object must not be removed if it has colliders that matter for ballistics. Bullets are simulated locally, and the shooter decides hits, so removing a collider client-side changes that client's hit results against bots and players.

### Gaps
- No documentation states how doors and loot containers are arbitrated beyond the code structure (WorldInteractiveObject sync packets exist). I did not read the door packet handlers.
- I did not trace BTR movement source code in detail (host-driven via the BTR controller patches).

---

## 2. Observed players: classes, snapshot application, interpolation, and what runs locally

### Takeaway
Remote humans and bots on a client are `ObservedPlayer` objects. They have Observed* controllers, and their state comes from a zero-allocation ring-buffer `PlayerSnapshotter`. That buffer has an adaptive jitter buffer (50–250 ms delay), EMA clock sync, and up to 100 ms of extrapolation. Each observed player still runs a full third-person animator, procedural weapon animation, FBBIK/limb IK, a hands controller (`ObservedFirearmController` etc.) and local shot simulation. That is why `Player.ArmsUpdate`/FirearmController costs remain on the client. Culling (EFT's `BotPlayerCulling` plus `FollowerCullingObject`) skips the visual pass for **bots** that are not visible. **Human players are never culled by botPlayerCulling.**

### Cited Findings
- **Class set.** Under `Main/ObservedClasses` and `Main/Players`:
  - `ObservedPlayer`
  - `ObservedMovementContext`
  - `ObservedInventoryController`
  - `ObservedHealthController`
  - `ObservedQuestController`
  - `ObservedStatisticsManager`
  - `ObservedCorpseCulling`
  - Hands controllers: `ObservedFirearmController`, `ObservedGrenadeController`, `ObservedKnifeController`, `ObservedMedsController`, `ObservedQuickGrenadeController`, `ObservedQuickKnifeController`, `ObservedQuickUseItemController`, `ObservedEmptyHandsController`
  - Movement states: `ObservedRunState`, `ObservedSprintState`, `ObservedJumpState`, `ObservedMountedState`, …
  - Bridges: `ObservedClientBridge` and `ObservedHostBridge`

  The local player is `FikaPlayer`, and host bots are `FikaBot`. — [Fika-Plugin tree](https://github.com/project-fika/Fika-Plugin/tree/main/Fika.Core/Main/ObservedClasses)
- **Snapshot buffer.** `PlayerSnapshotter<T>` defaults to capacity 32 (a power-of-two ring), drops out-of-order or duplicate packets, keeps an EMA time offset, and binary-searches by `renderTime = localTime + SmoothOffset − CurrentDelay`. Extrapolation is hard-limited to 0.1 s, and beyond that the state is `Stale`. — [PlayerSnapshotter.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/Snapshotting/PlayerSnapshotter.cs)
- **Adaptive jitter buffer.** `DefaultBaseDelay = 0.05` (50 ms) and `DefaultMaxDelay = 0.25` (250 ms). — [AdaptiveJitterBuffer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/Snapshotting/AdaptiveJitterBuffer.cs)
- The wiki describes the Snapshotter as "zero-allocation", with an "Adaptive Jitter Buffer… asymmetric EMA" and "Clock Synchronization". It benchmarks insertion at ~5.8 ns and sampling at ~4.2 ns, O(log N), 0 bytes allocated. It recommends sending at "a tickrate of 20" via `ThrottledMono`. — [Wiki: Modding Fika → Snapshotting](https://github.com/project-fika/gitbook-wiki/blob/main/Modding-Fika.md)
- `ObservedPlayer.ManualStateUpdate(localTime)` handles the three buffer states:
  - **Interpolating:** lerps rotation, head rotation, position, movement direction, tilt, speed, pose, weapon overlap and velocity, with dead zones.
  - **Extrapolating:** uses `pos + vel*t`.
  - **Stale:** zeroes velocity.

  If `!botPlayerCulling.IsVisible`, it only sets Position/Rotation/velocity and returns. On the host it also emits jump/step noise for AI hearing. — [ObservedPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L1225–1340)
- **Culling setup** in `CreateObservedPlayer`:
  - `botPlayerCulling = new(); Initialize(...)`, but `if (FikaBackendUtils.IsHeadless || profile.IsPlayerProfile()) botPlayerCulling.Disable();`. So **human players are never culled; only bots are.**
  - On non-headless machines, a `FollowerCullingObject` is added using `EFTHardSettings.CULLING_PLAYER_SPHERE_RADIUS/SHIFT/DISTANCE`.
  - On headless, `EnabledAnimators = Thirdperson | Arms`.

  The same logic is present in the v2.3.9 tag. — [ObservedPlayer.cs main](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L379–420); [ObservedPlayer.cs v2.3.9](https://github.com/project-fika/Fika-Plugin/blob/v2.3.9/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L259–303)
- **Per-frame update path.** `ManualUpdate` runs `MovementUpdate`, `UpdateTriggerColliderSearcher` (only every Nth frame, staggered by `_frameSkip`), `botPlayerCulling.ManualUpdate`, and state ticks. `LateUpdate` runs `Physical.LateUpdate()`, `ObservedVisualPass(...)`, `PropUpdate()` and corpse culling. — [ObservedPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L2043–2195)
- **What `ObservedVisualPass` does:**
  - It returns early if the bot is culled (`!botPlayerCulling.IsVisible`) or dead.
  - It then computes `_lastDistance` to the camera and `isVisibleOrClose = IsVisible && _lastDistance <= EFTHardSettings.CULL_GROUNDER`.
  - If arms were updated and the player is visible or close: `ProceduralWeaponAnimation.ProcessEffectors`, `ObservedFBBIKUpdate`, `MouseLook`, `ShiftWeaponRoot`, `RotateHead`, hand posers and limb IK (`IkProcess/AdjustElbows/IkApply`), plus `Animator.SetLayerWeight`.
  - Beyond `AnimatorCullDistance`, the animators switch to `CullUpdateTransforms`, except that the arms animator stays `AlwaysAnimate` unless the hands are empty, knife or usable item (`_shouldCullController`).
  - Whenever `_armsupdated` is set, `ProceduralWeaponAnimation.LateTransformations` and `HandsController.ManualLateUpdate` run, **even when the player is far away**.

  — [ObservedPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L2474–2560)
- FBBIK solver iterations scale with distance (`Clamp(15/distance, 0, 2)`). In "Quick" mode it updates only every `ikUpdateInterval` frames, staggered per player. — [ObservedPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L2876–2890)
- **v2.3.9 vs v2.4.4.** In v2.3.9, `ObservedVisualPass(Time.deltaTime, 3)` and the trigger search use `Time.frameCount % 2`. v2.4.4's notes say "Improved IK performance slightly by staggering updates over 3 frames" and "Added (hidden) setting to change the IK culling distance". — [ObservedPlayer.cs v2.3.9](https://github.com/project-fika/Fika-Plugin/blob/v2.3.9/Fika.Core/Main/Players/ObservedPlayer.cs) (≈L1602–1710); [v2.4.4 release](https://github.com/project-fika/Fika-Plugin/releases/tag/v2.4.4)
- **"IK Cull Distance"** (Advanced, default 40, range 20–200, v2.4.4+) sets `EFTHardSettings.Instance.CULL_GROUNDER`. Its description: "Distance when IK should cull (not update)… If another player is beyond this distance, their arms won't update (floating gun)." — [FikaConfig.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/FikaConfig.cs) (L241–250), [FikaPlugin.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/FikaPlugin.cs) (L275–283)
- Observed players are created with `armsUpdateMode = Manual` and `bodyUpdateMode = Auto`. — [CoopHandler.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Components/CoopHandler.cs)
- The headless sets `CULL_GROUNDER = 2000` and `AnimatorCullDistance = 2000`, so it always animates and IKs everything, because bot aiming and hitboxes need accurate bones there. — [Fika-Headless SettingsPatch.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/Patches/SettingsPatch.cs)

### Inferences
- **Why ArmsUpdate still costs ~0.8 ms for ~19 observed players.** Each observed player is a real EFT `Player` with an Animator-driven arms rig and a real hands controller. The hands controller is needed locally to:
  - play shot and reload animations;
  - spawn local bullets via `InitiateShot`, because clients re-simulate every observed shot;
  - drive `ProceduralWeaponAnimation`.

  `HandsController.ManualLateUpdate` and `LateTransformations` run whenever `_armsupdated` is set, with no distance check. Only the IK/effector block is gated by `CULL_GROUNDER`.
- **Biggest lever in 2.3.9:** the `CULL_GROUNDER` value. EFT's default applies, since the 2.4.4 Fika setting is absent. A client mod can set `EFTHardSettings.Instance.CULL_GROUNDER` lower, which is what 2.4.4 exposes. The visual cost is a "floating gun" on far players; there is no gameplay authority impact. Reducing `AnimatorCullDistance` is similar.
- **Half-rate animation for off-screen bots** is client-side and visual. Bot culling already skips `ObservedVisualPass` when not visible, but the Animator component and hands controller can still tick, so throttling those is plausible.
  - Risk 1: the client simulates hits **from its own player** against the observed bot's colliders. If off-screen bones go stale, that does not matter, because off-screen means you are not shooting at them.
  - Risk 2: bots in your peripheral or optic view must stay full-rate, because a stale pose changes **your** hit results. You are the authority for your own shots.
- **Never** apply half-rate animation to **human** observed players near the camera for the same reason. Fika itself never culls humans with botPlayerCulling.

### Gaps
- No Fika docs give per-frame CPU cost numbers for interpolation or networking, beyond the snapshotter micro-benchmarks above.
- EFT's default values for `CULL_GROUNDER` and `AnimatorCullDistance` in EFT 0.16.9.40087 are not documented by Fika. They would have to be read from the game's `EFTHardSettings` asset.

---

## 3. Fika Headless: how it runs, mods on it, tuning, same-PC vs separate-PC

### Takeaway
The headless is a normal EFT/SPT client process with Fika.Core plus Fika.Headless. It is launched by FikaHeadlessManager, and the user can press `G` for graphics mode. It is **not** a true Unity `-batchmode -nographics` server. Instead it "destroys" graphics: it removes post-processing from the camera, disables grass, decals, volumetrics, clouds, rain, water and lamps, mutes audio, sets minimum quality at 1024x768, and optionally destroys renderers and unloads materials. Its frame cap is the "Update Rate" setting, default 60, range 30–120. It must stay above 30 FPS. Separate-PC hosting is the officially supported setup.

### Cited Findings
- **Headless BepInEx config** (section "Headless"):
  - `Update Rate`, default 60, range 30–120: "How often the server should update (frame cap / tick rate)".
  - `RAM Clean Interval`, default 15 min: runs only outside raids.
  - `Bot sleeping`, default **false**: "Should the headless host allow bots to sleep? (BSG bot sleeping logic)". `BotStandBy_Update_Transpiler` disables `BotStandBy.Update`.
  - `Destroy Graphics`, default true.
  - `Destroy Renderers`, default true: "disable unnecessary renderers as well as unloading all materials".

  — [FikaHeadlessPlugin.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/FikaHeadlessPlugin.cs) (≈L276–292); [BotStandBy_Update_Transpiler.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/Patches/BotStandBy_Update_Transpiler.cs)
- **Frame limiter.** In menus it uses `Application.targetFrameRate = 1` with `vSyncCount = 0`. In raid it uses `targetFrameRate = UpdateRate`, and `JobScheduler.SetTargetFrameRate` is set the same way. — [FikaHeadlessPlugin.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/FikaHeadlessPlugin.cs) (≈L594–610)
- **GC policy on headless.** In raid, the GC is kept `Disabled`, and periodically it is briefly enabled to call `GarbageCollector.CollectIncremental(1000000)`. Out of raid it runs `Resources.UnloadUnusedAssets` plus a full `Collect(2, Forced)`. It also disables SPT's `MemoryCollectionPatch`. — [FikaHeadlessPlugin.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/FikaHeadlessPlugin.cs) (≈L147–170)
- **Forced settings on headless.**
  - Graphics and audio: volume 0, VSync off, shadows 0, textures 0, SuperSampling DownX05, AA/SSAO/SSR/DLSS/FSR off, `LodBias 2`, `OverallVisibility 400`, `SdTarkovStreets true`, 1024x768 windowed, `GameFramerate = UpdateRate`.
  - Hard settings: `CULL_GROUNDER = 2000`, `AnimatorCullDistance = 2000`; decals, hit effects, shot effects and heat emitters disabled.
  - Server-side masks: `TriggersCastLayerMask = ServerTriggersCastLayerMask`, `WEAPON_OCCLUSION_LAYERS = WEAPON_OCCLUSION_SERVER_LAYERS`, `LootVolumeForHighQuallityPhysicsClient = ...Server`.

  — [SettingsPatch.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/Patches/SettingsPatch.cs)
- **Graphics destruction patches** include: CameraNuker (destroys HBAO, Bloom, AO, DoF, MotionBlur, color-correction components and so on from the camera), GPUInstancer detail/HiZ managers, grass init, ImpostorsRenderer, StaticDeferredDecals, VolumetricLightRenderer, WaterRendererv3, Rain, Clouds, DistantShadow, PerfectCulling cross-scene update, LoadScenePatch, many audio transpilers, Lamp system, DLSS/Reflex/VRAM stubs, and `Firearms_IsPatronVisible/IsShellVisible`. — [Fika-Headless Patches tree](https://github.com/project-fika/Fika-Headless/tree/main/Fika.Headless/Patches)
- `Shot_CheckCollision_Transpiler` inserts `Physics.SyncTransforms()` before every `Shot.CheckCollision` on headless ("syncs all transforms before a bullet checks if it hits"). — [Shot_CheckCollision_Transpiler.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/Patches/PhysicsPatches/Shot_CheckCollision_Transpiler.cs)
- A `Player_VisualPass_Patch` (skip visual work on headless) exists but is **commented out**. — [Player_VisualPass_Patch.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/Patches/PlayerPatches/Player_VisualPass_Patch.cs)
- The wiki says graphics rendering is disabled, but the headless "is **NOT** a true headless server. It still requires all the game files and a pretty hefty amount of RAM… A graphic card is NOT required". — [Wiki: Headless Client](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/README.md)
- **Performance claims:** on a separate PC, "FPS gains are usually around 25% to 50%". Same-PC is "not the intended use case… Some users see performance degradation". — [Wiki: Headless Client](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/README.md)
- **Hardware:**
  - Remote headless: a dedicated machine, "at least 4 cores @ 4GHz+", 32 GB RAM, SSD/NVMe; a VPS generally will not work.
  - Local (same-PC) headless: "must have at least 64 GB of RAM", a CPU holding 4 GHz+ all-core, and default pagefile settings.

  — [Wiki: Remote Headless](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/remote-headless-client.md), [Wiki: Local Headless](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/local-headless-client.md)
- The wiki's explanation of why it helps: AI work "has to be calculated on the same CPU core/thread before the CPU pushes data to your video card… offloading the AI calculations onto a separate game client… [frees] your game client's main logic thread". — [Wiki: Local Headless](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/local-headless-client.md)
- **Headless FPS threshold:** it "must stay **above 30 FPS**". It is checked with `debug t` in the client console, which shows "Server FPS". In the 20s or teens, expect "AI acting strange and physics objects (such as grenades) teleporting or moving in slow-motion". FPS stuck at 59 is expected. — [Wiki: Headless FAQ](https://github.com/project-fika/gitbook-wiki/blob/main/faqandguides/headless-client-faq-and-common-issues/README.md)
- **Mods on the headless.**
  - Can install: AI mods (SAIN), spawn mods (ABPS, MOAR, Donuts), mods that state headless support, and mods that change in-raid behavior or inventory.
  - Do **not** install: "RAM/VRAM cleaner mods", "Performance mods (DeClutter, etc.)", or anything else not in the allowed list.

  — [Wiki: Local Headless → Modding](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/local-headless-client.md)
- **Community guidance** (wiki page by Shynd):
  - Plugins that only change how information or graphics are displayed are "probably *not* necessary for the headless".
  - Anything that changes in-raid behavior is "probably necessary", because "the raid host is authoritative on inventory actions".
  - "more plugins does cause more processing overhead and thus lower in-raid FPS".

  — [Wiki: mod guidance for headless](https://github.com/project-fika/gitbook-wiki/blob/main/faqandguides/headless-client-faq-and-common-issues/guidance-on-which-mods-should-be-used-on-the-headless-client.md)
- **Server-side headless config** (`fika.jsonc` → `headless`): `profiles.amount`, `aliases`, `scripts.generate`, `scripts.forceIp`, `setLevelToAverageOfLobby`, and `restartAfterAmountOfRaids` (default 1). — [Wiki: Server config](https://github.com/project-fika/gitbook-wiki/blob/main/fika-configuration/server.md)
- `-title=` on the command line sets the console title. Graphics mode is started with `G` in FikaHeadlessManager. — [FikaHeadlessPlugin.cs](https://github.com/project-fika/Fika-Headless/blob/main/Fika.Headless/FikaHeadlessPlugin.cs); [Wiki: configure mods for headless](https://github.com/project-fika/gitbook-wiki/blob/main/faqandguides/headless-client-faq-and-common-issues/configure-mods-for-the-headless-client.md)

### Inferences
- The user's perf mod is mostly visual. Its GC policy is redundant on the headless, which has its own and disables SPT's memory patch. Following the wiki's "no performance mods" rule, the mod should **not** be installed on the headless, or at most only its physics fixes should be.
- **The BTR "runaway physics" fix is the exception.** It addresses host-side bot simulation and is the one feature that would matter on the headless. If it is deployed there, it should be a separately toggled feature that is tested against the 30 FPS / `debug t` guidance.
- Raising headless `Update Rate` above 60 increases the host's simulation rate, but bot state still goes out at the SendRate (20 Hz). Client smoothness depends mainly on SendRate and jitter, not on headless FPS, as long as the headless stays ≥30.
- No source documents CPU affinity or priority guidance. "Player Load Priority" (client, advanced) is a different thing: it controls asset-load priority.

### Gaps
- Exact launch arguments that FikaHeadlessManager passes (for example whether `-batchmode` or `-nographics` is used) are in the separate Fika-Headless-Manager repo, which I did not read. The wiki says a GPU is not required, and graphics mode is opt-in.
- No official CPU affinity, core-pinning or process-priority guidance was found.

---

## 4. Fika's own performance-related settings (exact names, 2.3.9 vs 2.4.x vs 3.x era)

### Takeaway
The SPT 4.0 Fika (2.3.x) client config has **no** "Dynamic AI", "Culling" toggle, "Max Bots", "Enforced Spawn Limits", "Native Sockets" or tick-rate setting. Those were SPT 3.x-era (Fika 0.9/1.x) BepInEx settings. What remains:

- Network → `Send Rate` (Low 10 / Medium 20 / High 30 Hz)
- Gameplay → `Disable Bot Metabolism`
- Gameplay → `Strict Inventory Sync`
- Advanced → `No AI`, `No Loot`, `Player Load Priority`, `Max Bundle Lock`
- Advanced → `IK Cull Distance` (2.4.4+ only)
- Server `fika.jsonc` → `useInertia`, `allowNamePlates`, etc.

Native sockets are now automatic (`UseNativeSockets = NativeSocket.IsSupported`).

### Cited Findings
- **v2.3.9 `FikaConfig.cs` setting keys:**
  - Advanced: Show Official Version, Developer Mode, No AI, No Loot, Player Load Priority, Max Bundle Lock.
  - Coop: Auto Use Headless, Show Feed, Auto Extract, …, Enable Online Players.
  - Name plates: Show Player Name Plates, …, Use Occlusion, ….
  - Quest sharing; Pinging; Revival (Give Up Key); Debug (Free Camera Button, AZERTY Mode, Drone Mode, Keybind Overlay).
  - Network: Force IP, Force Bind IP, UDP Port, Use Port Mapping, Use NAT Punching, Use Fika NAT Punch Server, Connection Timeout, Send Rate, Allow VOIP.
  - Gameplay: Disable Bot Metabolism, Strict Inventory Sync.

  — [FikaConfig.cs v2.3.9](https://github.com/project-fika/Fika-Plugin/blob/v2.3.9/Fika.Core/FikaConfig.cs)
- `main` (v2.4.6) adds **"IK Cull Distance"** (Advanced, default 40f, range 20–200). — [FikaConfig.cs main](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/FikaConfig.cs) (L241–250)
- `Send Rate` default `ESendRate.Medium`. `ToNumber()` maps Low=10, Medium=20, High=30 (identical in v2.3.9). The server logs "Starting server with SendRate: {_sendRate}", which matches the user's "SendRate: 20". — [FikaGlobals.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Utils/FikaGlobals.cs) (L559–568), [FikaServer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/FikaServer.cs) (L211–212)
- **Advanced settings descriptions:**
  - `Player Load Priority`: "What priority loading other players (and AI as a client) uses. Might not have a huge effect."
  - `Max Bundle Lock` (default 5, range 1–10): "Max amount of bundles loading in parallel… Default game value is 1".
  - `No AI`: "Stops AI from spawning".
  - `No Loot`: "Stops loot from spawning".

  — [FikaConfig.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/FikaConfig.cs) (L202–239)
- `Disable Bot Metabolism` (default false): "Disables metabolism on bots, preventing them from dying from loss of energy/hydration during long raids". It is a gameplay feature, not a performance one. — [Wiki: Client config](https://github.com/project-fika/gitbook-wiki/blob/main/fika-configuration/client.md)
- `Strict Inventory Sync` (default true). Per v2.3.6: "Made several improvements to the `Strict Inventory Sync`… Everyone should enabled it again". — [FikaConfig.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/FikaConfig.cs) (L844–852); [v2.3.6 release](https://github.com/project-fika/Fika-Plugin/releases/tag/v2.3.6)
- **Server `fika.jsonc` client section:**
  - `useInertia` (true): "if disabled it will act as if you are not wearing anything".
  - `allowNamePlates`, `useBtr`, `dynamicVExfils`, `instantLoad`, `fastLoad`.
  - `mods.required` / `mods.optional`.

  — [Wiki: Server config](https://github.com/project-fika/gitbook-wiki/blob/main/fika-configuration/server.md); [ClientConfigModel.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/Models/ClientConfigModel.cs)
- **LiteNetLib setup:** `UpdateTime = 50` on both server and client managers, and `UseNativeSockets = NativeSocket.IsSupported`, so native sockets are now automatic. `FikaServer.Update()` and `FikaClient.Update()` call `PollEvents()` each frame. — [FikaServer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/FikaServer.cs) (L163–170, L548–550), [FikaClient.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/FikaClient.cs) (L146, L317–319)
- **SPT 3.x-era settings (old docs):**
  - Performance: "Dynamic AI" ("disabling AI when they are outside of any player's range"), "Dynamic AI Range", "Dynamic AI Rate", "Culling System" ("When players are outside of the culling range, their animations will be simplified"), "Culling Range".
  - Performance | Max Bots: "Enforced Spawn Limits", "Max Bots <MAP>".
  - Network: "Native Sockets".

  — [Jabaay/Fika-Documentation README (3.x era, community)](https://github.com/Jabaay/Fika-Documentation/blob/main/README.md)
- **Dynamic AI history:** added 2024-05-19 ("Synchronizes disabling of AI to clients; Destroy self if Questing Bots is loaded"), a SniperScav filter added 2024-07, last "Dynamic AI improvements" in Nov 2024 (PR #200). It is absent from the 2.3.9 and 2.4.6 configs. — [GitHub commit search: Fika-Plugin "dynamic AI"](https://github.com/project-fika/Fika-Plugin/commit/efa4307f), [PR #200 merge](https://github.com/project-fika/Fika-Plugin/commit/e15d8a6b)
- The wiki's client config page says "Not fully updated for 4.X". The General page still lists "Culling system to increase performance". — [Wiki: Client config](https://github.com/project-fika/gitbook-wiki/blob/main/fika-configuration/client.md); [Wiki: General](https://github.com/project-fika/gitbook-wiki/blob/main/General-information.md)

### Inferences
- In 4.0 Fika, "culling" means the built-in `botPlayerCulling`, `FollowerCullingObject` and `ObservedCorpseCulling`, which are always on and have no toggle. In 2.4.4+ the IK distance is also configurable.
- Dynamic AI (disabling far bots) is gone from Fika for SPT 4.0. Equivalent behavior would have to come from a host-side mod or from EFT's own `BotStandBy` sleeping. The headless disables `BotStandBy` by default via "Bot sleeping = false", so setting **"Bot sleeping" = true on the headless** is the closest built-in equivalent for reducing host AI load.
- SendRate High (30) would raise per-client packet-processing and interpolation-sample frequency only slightly. The interpolation cost per player per frame is the same regardless of send rate. Low (10) would raise the adaptive delay and extrapolation risk.

### Gaps
- I could not find the exact commit or release that removed Dynamic AI and the Performance section. Commit search shows nothing after Nov 2024, which suggests removal during the SPT 3.11 → 4.0 rewrite, but this is unconfirmed.
- Localized description strings for the Send Rate, Strict Inventory Sync and Disable Bot Metabolism tooltips are in the locale files, which I did not fetch.

---

## 5. Mod compatibility rules and validation

### Takeaway
Mod validation is GUID-based with a CRC32C hash of each BepInEx plugin DLL. The client posts `{GUID: crc32c}` to `/fika/client/check/mods`.

- If `fika.jsonc` `mods.required` and `mods.optional` are **both empty, all mods are allowed**.
- Otherwise, a missing required mod, a mod in neither list (Forbidden), or a hash different from the first client's (HashMismatch) produces "Your client doesn't meet server requirements" and the game quits.

Client-only visual mods are safe as long as they are permitted by those lists. Mods that change AI, spawns, inventory or in-raid behavior must be on the host/headless. Performance mods are explicitly not wanted on the headless.

### Cited Findings
- `FikaModHandler.VerifyMods`:
  - computes `CRC32C.Compute(fileBytes)` per plugin and logs `Loaded plugin: [Name] with GUID [..] and crc32 [..]`;
  - posts to `/fika/client/check/mods`;
  - on Forbidden, MissingRequired or HashMismatch, shows "INSTALLATION ERROR… Your client doesn't meet server requirements" and calls `Application.Quit` (auto after 15 s).

  It also detects QuestingBots, SAIN and UIFixes by GUID. — [FikaModHandler.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Utils/FikaModHandler.cs)
- **Server side**, `ClientService.GetCheckModsResponse`:
  - "if no configuration was made, allow all mods".
  - Otherwise it checks required mods first, then marks any GUID not in `_allowedMods` as Forbidden.
  - The first client's hash becomes the reference, and later clients with a different hash produce `HashMismatch`.
  - Optionally logs client mods (`LogClientModsInConsole`).

  — [Fika-Server-CSharp ClientService.cs](https://github.com/project-fika/Fika-Server-CSharp/blob/main/FikaServer/Services/ClientService.cs) (≈L66–128)
- The wiki gives a PowerShell snippet that scrapes `[Info :FikaModHandler]… GUID [..]` lines from Player.log into `mods.required`. Its guidance: "move some of the GUIDs down to the `optional`… Anyone who has a plugin loaded that is not in either of these lists or missing a plugin in the required list will get an error". — [Wiki: Set up required/optional mods](https://github.com/project-fika/gitbook-wiki/blob/main/faqandguides/set-up-required-optional-mods.md)
- **Modding rules:**
  - Inventory changes must go through `InventoryController.vmethod_1`; "If you circumvent the intended way of executing inventory operations, you will inevitably cause desync!"
  - Use `FikaBackendUtils.IsServer` to branch host vs client logic.
  - Do not send redundant data every `Update()`; use `ThrottledMono`.

  — [Wiki: Modding Fika](https://github.com/project-fika/gitbook-wiki/blob/main/Modding-Fika.md)
- Wiki limitation: "Certain SPT mods are incompatible with Fika… It is the responsibility of the mod's author to make their mod compatible". — [Wiki: General](https://github.com/project-fika/gitbook-wiki/blob/main/General-information.md)
- Headless don't-install list: "RAM/VRAM cleaner mods" and "Performance mods (DeClutter, etc.)". — [Wiki: Local Headless](https://github.com/project-fika/gitbook-wiki/blob/main/advanced-features/headless-client/local-headless-client.md)
- v2.4.1 notes that errors while disposing players after a raid are "99%… from mods". — [v2.4.1 release](https://github.com/project-fika/Fika-Plugin/releases/tag/v2.4.1)

### Inferences
- **Classification of the user's mod features:**
  - Client-safe (visual or local-only): shadow-only renderer culling, zero-intensity lights, GC policy, and declutter of objects **without gameplay colliders**.
  - Conditionally safe: OverlapSphere batching, as long as results are identical. Half-rate off-screen bot animation, as long as it stays strictly off-screen and is never applied to humans.
  - Host-relevant: the BTR runaway-physics skip.
- If the server uses required/optional lists, the perf mod's GUID must be added to `optional` so it can stay client-only. Because of the hash check, every client using it must have the **same DLL build** as the first client that connected.
- Declutter is risky in multiplayer if it disables colliders. Your client is authoritative for your own shots, so you could shoot through objects that are solid for others. Bots on the host would still see and shoot through their own geometry, which could make bots appear to "shoot through walls" from your point of view, or the reverse. Keep declutter strictly renderer-only, which is likely why the wiki bans DeClutter-type mods on the headless.
- Patching the timing of `BotOwner`, `BotsController` or AI layers on the client has no effect, because there are no BotOwners on clients. Such work must happen on the headless.

### Gaps
- No public list of known-incompatible performance mods for SPT 4.0 exists beyond the "DeClutter" and RAM/VRAM cleaner examples.

---

## 6. What could be offloaded further from client to host, and the limits

### Takeaway
The headless already takes all AI decision-making, bot navigation and pathing, spawning, grenade and loot physics, and bot sensing. The client still pays for:

- remote-player presentation: animators, IK, hands controllers and procedural weapon animation;
- local re-simulation of every bullet fired by any observed player;
- its own player and rendering.

Moving presentation to the host is not feasible, because it is per-camera visual work. Shot re-simulation stays local by design, because the victim resolves bot hits and the shooter resolves player hits.

### Cited Findings
- Clients re-simulate observed shots from `ShotInfoPacket` (`InitiateShot` with position and direction). — [ObservedFirearmController.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/ObservedClasses/HandsControllers/ObservedFirearmController.cs)
- Damage authority: the shooter is authoritative for player shots (DamagePacket), and the victim is authoritative for AI shots on the local player. — [ObservedClientBridge.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/ObservedClasses/PlayerBridge/ObservedClientBridge.cs), [FikaPlayer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Main/Players/FikaPlayer.cs)
- Bandwidth guidance and an example from the wiki: a Vector3 packet is 42 bytes on the wire, because headers increase it "almost 4×". Use throttled updates; "performance and bandwidth is not free". — [Wiki: Modding Fika → General Information About Data](https://github.com/project-fika/gitbook-wiki/blob/main/Modding-Fika.md)
- The wiki warns that Fika "will not run perfectly if you're playing with someone across the world on a 4G connection". — [Wiki: General](https://github.com/project-fika/gitbook-wiki/blob/main/General-information.md)

### Inferences
- **Plausible further offloads:**
  1. **Bot shot effects only.** For bot-vs-bot fights far from the player, the client could skip full ballistic simulation and only play impact effects. The risk is that the client can no longer detect bot bullets that hit **the local player**, and those are resolved locally. Any skip must keep the full simulation for shots whose trajectory passes near the local player.
  2. **Host-computed visibility or importance.** The host already knows which bots are asleep or far. A host→client "bot importance" packet could let clients reduce animation and hands-controller rates for unimportant bots. This needs a custom packet via the Fika API (both sides need the mod).
- **Limits:**
  - Latency: animation and IK must be computed at render rate on the viewer.
  - Authority: shooter-side hit registration needs accurate local bone poses.
  - Bandwidth: sending bone poses for ~20 players at 20 Hz is orders of magnitude more data than the current state snapshot.

### Gaps
- No Fika documentation discusses offloading presentation, or shot re-simulation costs on clients.

---

## 7. Networking library (LiteNetLib), packets, per-frame client cost

### Takeaway
Fika bundles a modified LiteNetLib source with native-socket support, a CRC32C/XOR layer, a NAT punch module and NTP. It polls events once per frame in `Update()`. The logic thread runs at `UpdateTime = 50` ms. Snapshot sampling is O(log N) and allocation-free. No official per-frame CPU cost figures exist beyond the micro-benchmarks.

### Cited Findings
- **Source tree:** `Fika.Core/Networking/LiteNetLib/` includes `NativeSocket.cs`, `NatPunchModule.cs`, `Layers/Crc32cLayer.cs`, `Layers/XorEncryptLayer.cs`, `ReliableChannel.cs`, `SequencedChannel.cs`, `Utils/NtpRequest.cs` and others. Packets are organized under `Networking/Packets/` (Player, FirearmController, World, Communication, Generic, Backend). LZ4 compression is under `Networking/LZ4/`. — [Fika-Plugin Networking tree](https://github.com/project-fika/Fika-Plugin/tree/main/Fika.Core/Networking)
- README credits: LiteNetLib (MIT) for P2P UDP, Open.NAT (MIT) for UPnP, "a custom interpolation system… inspired by the id Tech 3 networking model". — [Fika-Plugin README](https://github.com/project-fika/Fika-Plugin)
- Both managers use `UpdateTime = 50` and `UseNativeSockets = NativeSocket.IsSupported`, and call `PollEvents()` in `Update()`. — [FikaServer.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/FikaServer.cs), [FikaClient.cs](https://github.com/project-fika/Fika-Plugin/blob/main/Fika.Core/Networking/FikaClient.cs)
- Snapshot sampling costs ~4.2 ns and insertion ~5.8 ns, with 0 B allocations. — [Wiki: Modding Fika](https://github.com/project-fika/gitbook-wiki/blob/main/Modding-Fika.md)
- v2.4.4 notes: "Improved reading and writing of network data… Improved packet handling of common packets… Updated snapshotter to a newer version". — [v2.4.4 release](https://github.com/project-fika/Fika-Plugin/releases/tag/v2.4.4)

### Inferences
- Networking and interpolation are a tiny fraction of the client's per-frame cost. On the client, the ~19 observed players' *presentation* (animators, IK, hands controllers, bullet re-simulation) dominates, not packet handling. Optimizing packet code is not worthwhile for a client perf mod.
- Upgrading to SPT 4.1 with Fika ≥2.4.4 would give IK staggering over 3 frames and the configurable IK cull distance for free. This is directly relevant to the measured 0.8 ms ArmsUpdate.

### Gaps
- No measured per-frame cost of `PollEvents` or packet deserialization with ~20 players at 20 Hz was found. A profiler capture would be needed.
