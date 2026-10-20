# Porting to a new client (SPT 4.1.6 / Fika 2.4.6)

SPT 4.1.x runs a newer EFT client (1.0 series) than 0.16.9. BSG's obfuscated names (`GClassNNNN`, `method_N`, `smethod_N`, `bool_N`) are regenerated with every client build, so every hook that uses one must be re-found. Patches against named methods usually survive, but their bodies may have changed, so re-check the behaviour they rely on.

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
| `Plugin.cs` | — | `EFTHardSettings.CULL_GROUNDER` (bot IK distance). Fika 2.4.4+ also sets this through its *IK Cull Distance* setting, so the two settings now conflict. Pick one owner |

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
