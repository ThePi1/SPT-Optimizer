# Porting to a new client (SPT 4.1.6 / Fika 2.4.6)

**Status (2026-10-20): ported.** Both plugins build against SPT 4.1.6 and are deployed to `I:\Games\SPT-4-1-6-testing`, but haven't been tested in game yet. The mapping is in [SPT 4.1.6 mapping](#spt-416-mapping-done) below.

SPT 4.1.6 runs **EFT 0.16.9.5 build 40743**, still a 0.16.9 client on Unity 2022.3.43f1 (4.0.13 ran build 40087). It is *not* a 1.0-series client, so BSG's 1.0.4 player culling and 1.0.5 animation work are not in it. What changed most is naming: SPT 4.1's patched `Assembly-CSharp` uses readable names almost everywhere, with no top-level `GClassNNNN` types. Many SPT 4.0 remap names (`...Class`, `...AbstractClass`) are gone too. A decompile is in `I:\Games\SPT-4-1-6-testing\eft-decomp\`.

The general rule still holds: re-find every hook that used an obfuscated or remapped name, and re-check the behaviour that patches against named methods rely on.

## SPT 4.1.6 mapping (done)

| Used by | SPT 4.0.13 (0.16.9.40087) | SPT 4.1.6 (0.16.9.40743) |
| --- | --- | --- |
| Batched overlaps | `SimpleCharacterController.method_7`, nested `GClass755` | `UpdateNeighboursColliders`, nested `CollidersArray` (same body; still called once per `SingleMove` sub-step) |
| Batched overlaps | `MovementContext.ScheduleDirectApplyMotion`, `ScheduledMotion` | public fields `_scheduleDirectApplyMotion`, `_scheduledMotion` |
| Half-rate animation | `GClass1446` | `AnimationSystem.UnityAnimatorWrapper` (`.Animator`) |
| Faded light culling | `ControlledLampGroup.method_8` | `SetLightState` (same body). `LampController.bool_0` and `ControlledLampGroup._lights` are unchanged |
| LOD bias override | `GameGraphicsClass.smethod_9` | `EFT.Settings.Graphics.GraphicsSettingsController.ChangeLodBias` (same `bias` parameter) |
| LOD bias, profiler graphics steps | `SharedGameSettingsClass`, `GraphicsSettingsClass` | `EFT.Settings.SettingsManager`, `EFT.Settings.Graphics.GraphicsSettingsGroup` (all 11 setting field names unchanged) |
| Physics diagnostics | `EFTPhysicsClass.SyncTransformsClass.smethod_0/1`, `.Boolean_0`, `.UpdateEnabled` | `PhysicsExtensions.Simulation.Simulate/SmoothSimulate`, `.CanRunSimulate`, `.UpdateEnabled` |
| Physics diagnostics | `EFTPhysicsClass.GClass745.List_0` | `PhysicsExtensions.UpdateController._rigidbodies` (same `rigidbody` field) |
| Raid state | `CurrentScreenSingletonClass` | `EFT.UI.Screens.EftScreenManager` |
| Fast animator, census | `BackendConfigAbstractClass.Config` | `AppEnvironment.Config` |
| Fast animator | `ResourceKeyManagerAbstractClass.BUNDLES_TO_PRELOAD` | `EFT.InGameBundles.BUNDLES_TO_PRELOAD` |
| Profiler, AreaLight dispatch | `AreaLight.method_2`, `method_0` | `AreaLight.PreCull`, `Init` |
| Profiler, slow-work logger | `Class1031.Execute`, `Struct263` | `Diz.Jobs.ExecuteConditionGeneral.Execute`, `Diz.Jobs.JobContinuation` |
| Profiler targets | `BotsClass`, `AICoreControllerClass` | `BotsList`, `AICoreController` |

Both `.csproj` files now default `SptDir` to `I:\Games\SPT-4-1-6-testing`. To build for the archived 4.0.13 install, check out commit `112ef6d` or earlier.

## Before porting: baseline the new client

1. Set up a separate test install. Check `BepInEx\LogOutput.log` and `Player.log` for the EFT and Unity versions. The Unity version decides whether the csproj references and Unity API notes still apply.
2. Repeat the environment from [TESTING.md](TESTING.md): sunny weather, borderless, same graphics settings.
3. Run the **self-hosted vs headless** protocol with only the profiler installed, once with Fika's *IK Cull Distance* at its default (40) and once at a low value. EFT 1.0.4 added a new player-culling system and 1.0.5 optimized animation CPU cost. Fika 2.4.4 added 3-frame IK staggering. All three may already cover what [the research report](reports/Tarkov%20Unity%20optimization%20research.md) ranks highest.
4. Decompile the new `Assembly-CSharp.dll` and diff it against the 0.16.9 decompile in `data\spt-4.0.13\reference\` to find the renamed members.

## Hooks to re-target

### Optimizations plugin

| File | Obfuscated targets (0.16.9) | Named targets |
| --- | --- | --- |
| `BatchedOverlaps.cs` | `SimpleCharacterController.method_7` (the OverlapSphereNonAlloc call per 0.3 m sub-step), `GClass755` | `SimpleCharacterController.Move`; fields `_colliders`, `_collisionMask`, `_movementTransform`, `_castHalo` |
| `RunawayMoveGuard.cs` | — | `SimpleCharacterController.Move`. First check whether the BTR gunner still parks at (1000, −498, 1001) and falls without limit |
| `FadedLightCulling.cs` | `LampController.bool_0` (current on/off state), `ControlledLampGroup.method_8` | `CullingLightObject.UpdateCullingObject`, `LampController.TurnLights`, field `ControlledLampGroup._lights` |
| `LodBiasOverride.cs` | `GameGraphicsClass.smethod_9` (sets `QualitySettings.lodBias`) | — |
| `PhysicsDiag.cs` | `GClass745` (supported-rigidbody registry), `EFTPhysicsClass.SyncTransformsClass.smethod_0/1` | `Corpse.CheckCorpseIsStill` |
| `BotAnimLod.cs` (parked) | `GClass1446` (`IAnimator` wrapper around the Unity Animator) | `MovementContext.ManualUpdate`, `MovementContext.AnimatorStatesLateUpdate`, `PlayerStateContainer.OnStateMove(Animator, AnimatorStateInfo, int)`, field `MovementContext._player` |
| `FastAnimator.cs` (experimental) | `GClass1340` | `TarkovApplication.ConfigureApplication`, `ResourceKeyManagerAbstractClass.BUNDLES_TO_PRELOAD` |
| `Declutter.cs` | none hooked (the comment cites `StaticDeferredDecalRenderer.method_9`) | `LODGroup` scan; `StaticDeferredDecal` must stay untouched |
| `ShadowProxyCulling.cs`, `GcPolicy.cs`, `Raid.cs` | — | Unity APIs, `GameWorld`, `HideoutGameWorld`, `CurrentScreenSingletonClass`, `EEftScreenType` |
| `Plugin.cs` | — | `EFTHardSettings.CULL_GROUNDER` (bot IK distance). Fika 2.4.4+ also sets this through its *IK Cull Distance* setting. **Resolved in 0.3.0:** our option was removed and Fika owns the value |

### Profiler plugin

| File | Obfuscated targets |
| --- | --- |
| `AreaLightDispatcher.cs` | `AreaLight.method_2` (camera callback), `method_0` |
| `SlowWork.cs` | `Class1031` |
| `Experiments.cs` | `GraphicsSettingsClass` field names in `GfxChanges` (named but may change), and `EFTHardSettings` |
| `default-targets.txt` | Method lists by name. Re-check for missing types; the instrumenter logs how many patches failed |

## After porting

1. Run `AllOptimizationsOff` (F5) on Streets, self-hosted and on headless, to see what each existing optimization is still worth on the new client.
2. Re-run the individual steps that come out near zero (`BatchedOverlapsOff`, `FadedLightCullingOff`, `ShadowProxyCullingOff`, `RunawayGuardOff`) and switch off by default whatever no longer pays.
3. Re-rank the research report's techniques with the new numbers before building any of them.
4. Fika: put the plugin GUIDs (`com.sptoptimizer.optimizations`, `com.sptoptimizer.profiler`) in the server's `optional` mod list if the required/optional lists are in use, and give every machine the identical DLL.
