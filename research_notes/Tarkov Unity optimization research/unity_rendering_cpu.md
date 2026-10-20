# Unity 2022.3 BiRP CPU-side rendering optimizations: feasibility from a runtime mod (EFT 0.16.9, Mono, DX11, BepInEx/HarmonyX)

Scope note: research used ~17 web searches/fetches. Unity does not publicly document the internals of built-in culling (CullScene / SceneCulling job structure) or give per-renderer cost numbers. Where these notes reason from the project's own measurements, they say so. The measurements come from the coordinator's brief, not from a web source: culling 1.2-1.6 ms, render submission 1.4-2.1 ms, 3-5k draws, forceRenderingOff gave no culling saving, enabled=false did, shadow proxies 0.5-1.0 ms, 4.6k zero-intensity lights 0.24 ms, LOD bias 2->1 saved 0.11 ms on the main thread and 0.37 ms on the render thread.

## Q1. How BiRP culling works, the per-renderer cost model, and which state changes take a renderer out of culling

### Takeaway
Unity documents only the outer behaviour. Built-in culling is frustum culling plus optional baked Umbra occlusion, run as jobs under Camera.Render > Culling > SceneCulling, which the main thread waits on. The cost scales with the number of renderers that are registered and enabled in the scene. `forceRenderingOff` keeps a renderer registered and only drops it after culling. `enabled=false` and an inactive GameObject remove it. Per-layer cull distances and cullingMask are tested inside culling, so they shrink the output (draws, shadow casters) more than the per-renderer test cost.

### Cited Findings
- `Renderer.forceRenderingOff` is meant for "manual control" or "custom visibility culling". It leaves the component's enabled state unchanged and only excludes the renderer from rendering. — [Unity ScriptRef 2022.3 Renderer.forceRenderingOff](https://docs.unity.cn/ja/ScriptReference/Renderer-forceRenderingOff.html)
- A forum profiler capture shows the hierarchy `Camera.Render > Culling > SceneCulling > WaitingForJob`, with spikes up to 330 ms. This shows the main thread blocks on culling jobs. — [Unity forum "culling kills performance"](https://forum.unity.com/threads/culling-kills-performance.340777/)
- CullingGroup visibility "based on frustum culling and static occlusion culling only", and new visibility data is computed only during the camera's culling pass. — [Unity Manual 2019.3 CullingGroup API](https://docs.unity3d.com/2019.3/Documentation/Manual/CullingGroupAPI.html)
- In SRP, `CullingOptions.DisablePerObjectCulling` skips per-object light and reflection-probe culling "to avoid wasted CPU operations". So per-object light/probe assignment is a known per-renderer culling-stage cost. BiRP has no equivalent public switch. — [Unity ScriptRef 2022.3 CullingOptions.DisablePerObjectCulling](https://docs.unity.cn/ScriptReference/Rendering.CullingOptions.DisablePerObjectCulling.html)
- LODGroup: `ForceLOD` gives "slightly better CPU performance" than toggling the LODGroup and renderers manually. For forcing a level across many LODGroups over many frames, disabling the LODGroup and renderers "might provide better CPU performance". — [Unity ScriptRef LODGroup.ForceLOD](https://docs.unity3d.com/ScriptReference/LODGroup.ForceLOD.html)

### Inferences
- The project measured that forceRenderingOff gave no culling saving while enabled=false did. That fits forceRenderingOff renderers still being bounds-tested and LOD-evaluated in SceneCulling and only dropped afterwards. Mods should use `enabled=false` or `SetActive(false)` for bulk removal. `enabled=false` is cheaper to flip because it avoids OnDisable/OnEnable cascades on other components and transform hierarchy changes.
- The per-renderer culling cost looks roughly linear in registered, enabled renderers plus LODGroups:
  - 4.6k disabled lights saved 0.24 ms, about 50 ns per light. This is a project-derived figure, not a documented one.
  - 173k shadow-only proxies dominate shadow-caster culling, which matches the 0.5-1.0 ms saving.
- Cutting the far clip had ~0 effect. That suggests the cost is spent on the per-renderer test itself, which happens whatever the result, rather than on downstream work for visible objects. Distance-based approaches that still leave renderers registered (layerCullDistances, far clip) are therefore expected to save little culling time. They mainly save draw submission and shadow-caster draws.
- Disabling a renderer inside a LODGroup can conflict with LODGroup state. Disabling whole LODGroups plus their renderers is the documented "better CPU" route.

### Gaps
- No public Unity source documents the BiRP SceneCulling job breakdown: the per-renderer test, LOD evaluation, per-cascade shadow-caster culling, and per-object light culling. Profiling the game with the Profiler's markers (CullScene, CullSendEvents, ShadowCulling, PrepareSceneCullingParameters) would be needed.
- No published ns-per-renderer culling cost for 2022.3 was found.

## Q2. Camera.layerCullDistances, layerCullSpherical, Light.layerShadowCullDistances

### Takeaway
All three are runtime-settable script APIs, so they are fully mod-feasible. They cull small objects early by layer, and the camera's layer distances also cap shadow casting. They are evaluated per renderer during culling, so the main expected win is fewer draws and shadow casters, not a big cut in culling time. This fits the project's finding that the far clip had ~0 effect.

### Cited Findings
- `layerCullDistances` takes a 32-entry float array, and 0 means "use far plane". It is described as "very useful to cull small objects early on, if you put them into appropriate layers". — [Unity ScriptRef Camera.layerCullDistances](https://docs.unity3d.com/ScriptReference/Camera-layerCullDistances)
- `layerCullSpherical` uses a spherical distance, so rotating in place does not change visibility. It is listed as "only available with the Built-in renderer". — [Unity ScriptRef Camera.layerCullSpherical](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Camera-layerCullSpherical.html)
- Camera.layerCullDistances also restricts shadow casting to those distances. When both it and `Light.layerShadowCullDistances` are set for a layer, the effective shadow distance is the smaller of the two. — [Unity ScriptRef Light.layerShadowCullDistances](https://docs.unity3d.com/kr/6000.0/ScriptReference/Light-layerShadowCullDistances.html); [2019.1 version](https://docs.unity.cn/ru/2019.1/ScriptReference/Light-layerShadowCullDistances.html)

### Inferences
- **Mod approach:** in a camera OnPreCull or a Harmony postfix after EFT sets up its cameras, write layerCullDistances. Set small distances for clutter or prop layers if EFT's layers separate them. Set the directional (sun) light's layerShadowCullDistances so far small props stop casting shadows. This works without touching renderers.
- **Expected benefit:**
  - Draw submission and render thread: moderate where props are numerous.
  - Shadow culling and shadow draws: likely meaningful.
  - Culling time itself: small.
- **Risk:** visible pop-in, and the effect is only as fine as EFT's layer assignment. A mod could move renderers to new layers, but cameras' cullingMask and physics/raycast layer masks also use layers, so re-layering renderers is risky for gameplay raycasts. Only re-layer renderer-only child objects that have no colliders.

### Gaps
- No benchmark quantifying layerCullDistances culling-time savings was found.

## Q3. CullingGroup API to drive enable/disable

### Takeaway
Feasible from a mod. CullingGroup takes a referenced BoundingSphere array and distance bands, computes them during the camera's culling pass, and fires onStateChanged only when a sphere's state changes. It is a cheap way to toggle `Renderer.enabled` (or whole LODGroups) by distance band, which removes them from SceneCulling, the thing the project measured as effective.

### Cited Findings
- CullingGroup works as follows:
  - It takes an array of BoundingSpheres and supports one camera and one reference point per group.
  - Distance bands are quantised thresholds, and spheres beyond the last band count as invisible.
  - onStateChanged fires only for spheres whose visibility or band changed.
  - The array is referenced, not copied, and resizing is expensive.
  - Results are computed during the camera's culling pass, so they arrive asynchronously.
  
  — [Unity Manual CullingGroup API](https://docs.unity3d.com/2019.3/Documentation/Manual/CullingGroupAPI.html)
- Intended uses include deactivating very distant objects and reducing animation or AI quality. — [same](https://docs.unity3d.com/2019.3/Documentation/Manual/CullingGroupAPI.html)

### Inferences
- **Pattern:** one CullingGroup on the main FPS camera with distance bands only. Do not use its visibility to disable renderers, because disabled renderers would also stop casting shadows into view. Spheres would be per cluster, for example grid cells of shadow proxies or LODGroups, rather than per renderer, to keep the sphere count and the toggle cost manageable.
- **Benefit:** extends the already-measured 0.5-1.0 ms shadow-proxy win and the light win to all far static content. The incremental cost is the CullingGroup test, which is also per sphere. Clustering amortises it.
- **Risks:**
  - It is a single camera, so optics/scope cameras see disabled objects as missing. EFT renders scope views with a second camera, so the scope camera must be handled or band thresholds kept conservative.
  - Toggle bursts when crossing bands cause hitches, so spread them across frames.
  - Must coexist with Perfect Culling, which also toggles renderer enable state (see Q6). Harmony-patching its toggles or AND-ing visibility is needed.

### Gaps
- No published numbers on CullingGroup cost per sphere.

## Q4. Reducing draw-submission CPU without shader changes: static batching, dynamic batching, mesh combining, GPU instancing, BRG, SRP Batcher

### Takeaway
In BiRP 2022.3 the realistic levers are listed below. BRG and the SRP Batcher are not available in BiRP.

| Lever | What it is | Main limits |
|---|---|---|
| Runtime `StaticBatchingUtility.Combine` | Fewer state changes, keeps per-object culling | Same-material only; does not reduce draw count or culling cost |
| `Mesh.CombineMeshes` | True merge, fewer draws and fewer renderers to cull | Loses per-object culling and LOD |
| Enabling `material.enableInstancing` | Instancing for repeated mesh+material pairs | Only works if EFT's shaders compiled instancing variants |
| GPU Instancer, which EFT already ships | Indirect instancing with compute culling | Its own shader conversion requirement |

### Cited Findings
- BatchRendererGroup in 2022.3 has the following compatibility and requirements:
  - Compatibility: "Built-in Render Pipeline: No". URP, HDRP and custom SRP are supported.
  - It requires the SRP Batcher, DOTS-instancing shader variants, and unsafe code.
  
  — [Unity Manual 2022.3 BRG](https://docs.unity3d.com/Manual/batch-renderer-group.html); [getting started 2022.3](https://docs.unity3d.com/cn/2022.3/Manual/batch-renderer-group-getting-started.html)
- The SRP Batcher applies only with an SRP. — [Unity Manual 2022.1 Optimizing draw calls](https://docs.unity3d.com/2022.1/Documentation/Manual/optimizing-draw-calls.html)
- Unity applies one batching method per object, in this priority order:
  1. Static batching (alongside the SRP Batcher)
  2. GPU instancing
  3. Dynamic batching
  
  If static batching succeeds, GPU instancing is disabled for that object. — [Unity Manual 2022.1 Optimizing draw calls](https://docs.unity3d.com/2022.1/Documentation/Manual/optimizing-draw-calls.html)
- Static batching: "Unity sends the combined data to the GPU, but renders each mesh in the combination individually". Culling still happens per mesh. — [same](https://docs.unity3d.com/2022.1/Documentation/Manual/optimizing-draw-calls.html)
- `StaticBatchingUtility.Combine` keeps source GameObjects individually culled, and only objects with the same material can be batched. Objects marked Static in the editor are already combined at build time. — [Unity ScriptRef StaticBatchingUtility](https://docs.unity3d.com/ja/2023.1/ScriptReference/StaticBatchingUtility.html)
- Dynamic batching transforms vertices on the CPU. `Mesh.CombineMeshes` merges meshes so Unity renders them in a single draw call. — [Unity Manual Optimizing draw calls](https://docs.unity3d.com/2022.1/Documentation/Manual/optimizing-draw-calls.html)
- Built-in GPU instancing on Direct3D issues a draw call every 500 instances (vendor statement). `DrawMeshInstanced` allows up to 1023 instances per call. — [GPU Instancer FAQ](https://wiki.gurbu.com/index.php?title=GPU_Instancer:FAQ)
- An unverified secondary claim says static batching cut draws from 298 to 12 and CPU render time from 9 ms to 1.8 ms. No primary source was found. — [Outlook Respawn guide](https://respawn.outlookindia.com/gaming/gaming-guides/optimizing-your-unity-game-12-techniques-for-better-performance)

### Inferences
- **SetPass/draw ratio ~0.68:** most draws change pass state, meaning material or shader variant changes. Static batching's main CPU benefit is fewer state changes between same-material draws. It only helps if many visible objects share materials.
  - **Runtime Combine:** feasible, since it is a pure C# API call on scene objects. It needs meshes to be read/write-enabled, and shipped asset meshes may not be readable. Mesh.isReadable must be checked; this is unconfirmed for EFT assets.
  - **Costs:** memory, because combined vertex buffers duplicate geometry, and a load-time hitch.
  - **Already batched:** EFT maps may already be statically batched at build time. If so, a mod adds nothing for those objects. Check `Renderer.isPartOfStaticBatch`.
- **Mesh.CombineMeshes** of far static clusters (for example, combining a block of shadow-only proxies into one shadow-only renderer per cell):
  - **What it saves:** this is the only option that cuts *both* renderer count in culling and draws.
  - **What it costs:** per-object culling within the cell, and LOD.
  - **Fit:** well suited to the 173k shadows-only proxies, which have no visual LOD needs and are already measured as costly.
  - **Requirements:** readable meshes, or a GPU copy via `Mesh.GetVertexBuffer` / `Graphics.CopyBuffer` in 2021.2+. The second route is unverified for this use.
- **Enabling instancing on materials (`material.enableInstancing = true`)**: this only works if the shader has the `multi_compile_instancing` variant compiled into the build. Stripped variants mean no effect or the wrong shader.
  - Shaders could be loaded from an AssetBundle built with the same Unity version (2022.3.43f1) in a separate editor project. That is the "AssetBundle shader" route; it is feasible but labour-intensive.
- **GPU Instancer already ships in EFT:** its runtime API (`AddPrefabInstance`, `RegisterPrefabInstanceList`, `InitializeWithMatrix4x4Array`) could in principle be driven by a mod to instance more prototypes. This only works where GPUI-converted shaders exist in the build (see Q6).

### Gaps
- Whether EFT map meshes are readable, and whether static batching is already applied, needs in-game inspection.
- No primary BiRP benchmark of instancing vs static batching CPU savings was found.

## Q5. Graphics jobs on DX11 in 2022.3

### Takeaway
On DX11, Unity supports only "Legacy jobified" graphics jobs. Native graphics jobs and split graphics jobs are DX12-only, and split jobs were not backported to 2022.3. The `gfx-enable-native-gfx-jobs=1` flag in boot.config most likely has no native effect under D3D11; the likely outcome is legacy jobs or a fallback. Switching EFT to DX12 (`-force-d3d12`) is a possible experiment, but whether EFT ships DX12 shaders is unknown.

### Cited Findings
- Render threading mode support by API:

  | Mode | DX11 | DX12 |
  |---|---|---|
  | Legacy jobified | Supported | Supported |
  | Native graphics jobs | Unsupported | Supported |
  | Split graphics jobs | Unsupported | Supported |
  
  — [Unity Manual 6000.0 DX11/DX12 features](https://docs.unity.com/en-us/engine/6000.0/manual/platform-specific/cross-platform-features/graphics-apis/using-dx11gl3features). This is the Unity 6 table; 2022.3 is assumed similar.
- How the modes divide the work:
  - LegacyJobified: worker threads generate intermediate graphics commands, and a single render thread converts them to API calls.
  - NativeGraphicsJobs: the main thread generates intermediate commands, and the render thread dispatches worker jobs.
  
  — [Unity ScriptRef RenderingThreadingMode](https://docs.unity3d.com/kr/6000.0/ScriptReference/Rendering.RenderingThreadingMode.html)
- Split graphics jobs, from Unity's 2023.1 announcement:
  - Introduced in 2023.1a21 to cut main/render-thread sync at frame boundaries.
  - Unity ruled out backporting to 2022.3 LTS.
  - Unity says DX12 already beats DX11 on CPU, while DX11 may still win in GPU-heavy scenes.
  
  — [Unity Discussions: DX12 and Graphics Jobs improvements in 2023.1](https://discussions.unity.com/t/directx12-and-graphics-jobs-improvements-in-2023-1-split-graphics-jobs-and-editor-support/903434)
- Unity's guidance when DX12 left experimental in 2022.2 was to enable Native Graphics Jobs for players to use DX12 multithreading. — [search summary of Unity DX12 announcements; see same thread](https://discussions.unity.com/t/directx12-and-graphics-jobs-improvements-in-2023-1-split-graphics-jobs-and-editor-support/903434)

### Inferences
- With legacy jobs on DX11, draw submission is already spread across worker threads. The main-thread "camera render" cost (1.4-2.1 ms) is then mostly scene traversal, sorting and job scheduling/waiting, which mods cannot change except by reducing draw/renderer counts.
- Mod-level option: none via Harmony; this is a boot.config/command-line choice. Testing `-force-d3d12` (plus native jobs) is a user-side config experiment, not a code patch. It risks crashes or visual bugs if EFT's DX12 path is untested.

### Gaps
- No 2022.3-specific DX11 graphics jobs benchmark was found.
- Whether 2022.3 silently downgrades a native request to legacy, or to no jobs, on DX11 is unverified. Check Player.log for a "graphics jobs" line.

## Q6. Occlusion culling alternatives; Perfect Culling (Koenigz) and GPU Instancer (GurBu) as shipped in EFT

### Takeaway
Umbra data cannot be rebaked at runtime. **Perfect Culling** works as follows:
- It bakes per-cell visible-renderer sets in the editor.
- At runtime it does an O(1) spatial-hash lookup and toggles renderer enabled state only when the camera changes cells, so its per-frame cost is near zero.
- Its quality depends entirely on baked coverage.

**GPU Instancer** does instancing plus GPU frustum, distance and Hi-Z occlusion culling via compute shaders and DrawMeshInstancedIndirect. It requires GPUI-converted shaders.

Both are already in EFT. The mod-relevant levers are tuning them, or extending their coverage, and making sure mod toggles do not fight them.

### Cited Findings
- Perfect Culling at bake time:
  - It renders the scene with a unique colour per renderer from sample points and reads back colours to get visibility.
  - Baking is editor/ahead-of-time; runtime baking is not officially supported.
  - Unbaked objects cannot be culled.
  
  — [Asset Store page](https://assetstore-fallback.unity.com/packages/tools/utilities/perfect-culling-occlusion-culling-system-193611); [developer forum thread](https://discussions.unity.com/t/released-perfect-culling-pixel-perfect-occlusion-culling/837404)
- Perfect Culling at runtime:
  - The lookup is O(1) via spatial hashing, and a camera outside the volume is clamped to the nearest cell.
  - The data stores renderer *indices* in a ScriptableObject, with up to 65,535 groups per bake volume, and LODGroups are grouped automatically.
  - Renderers are "enabled or disabled", and only when the camera enters a cell with new visibility. A stationary camera is "virtually free".
  - Neighbour-cell visibility can be computed at runtime (extra lookups) or baked with "Merge-Downsample".
  - The developer contrasts it with Umbra's per-frame CPU tree traversal and software rasterisation.
  
  — [developer forum thread](https://discussions.unity.com/t/released-perfect-culling-pixel-perfect-occlusion-culling/837404)
- GPU Instancer:
  - Sends mesh and material data to the GPU once, renders via DrawMeshInstancedIndirect, and does frustum, distance and occlusion (Hi-Z) culling in compute shaders.
  - Auto-generates converted shader copies.
  - Built-in-only "Shadow with Original Shader" option; the default lightweight shadow shader can be wrong with alpha cutoff or vertex animation.
  - No baked GI or light-probe support; reflection probes are not applied in forward rendering.
  - Recommended only for high-instance-count prototypes, since few instances can increase batches.
  - Runtime API: AddPrefabInstance/RemovePrefabInstance (needs "Add/Remove Instances At Runtime" and extra buffer size), RegisterPrefabInstanceList + InitializeGPUInstancer, InitializeWithMatrix4x4Array, SetCamera.
  
  — [GPU Instancer FAQ](https://wiki.gurbu.com/index.php?title=GPU_Instancer:FAQ)
- Generic Hi-Z technique:
  1. Render an occluder depth pre-pass.
  2. Build a max-downsampled depth pyramid.
  3. Project each instance's AABB and test it against the mip level where it spans about 2x2 texels.
  
  This is not confirmed as GPUI's exact implementation. — [daslang GPU-driven tutorial](https://daslang.io/doc/reference/tutorials/vulkan/12_gpu_driven.html)
- DrawMeshInstanced/Indirect geometry is not per-cascade culled by Unity, so full instance sets render into every shadow cascade. — [MPC GPU culling experiments](https://mpcvfx.com/en/news/unity-gpu-culling-experiments-2)
- Unity's baked occlusion culling is deprecated in Unity 6.x in favour of GPU occlusion culling (URP/HDRP only). — [Unity docs: remove occlusion setup](https://docs.unity.com/en-us/engine/6000.7/manual/cameras/occlusion-culling/remove-setup)

### Inferences
- Perfect Culling's disabled renderers leave SceneCulling entirely, consistent with the project's enabled=false result. Its main-thread cost is just cell-change toggle bursts.
  - **Mod angles:**
    - Harmony-hook its camera/cell-change method to observe or extend the visible sets, for example to also disable shadow-only proxies that are not in the set but are far away.
    - Avoid double-toggling conflicts with any mod CullingGroup system.
  - **Caution:** Perfect Culling probably also hides objects that should cast shadows into view, unless the bake accounted for that. A mod that disables more renderers should keep shadow casters near the player.
- A custom runtime occlusion system, such as a portal/cell system or a CPU software rasteriser, is technically possible in C# with Burst unavailable. Under Mono, jobs without Burst are slow, so the cost/benefit is poor compared to leveraging Perfect Culling's existing bake.
- GPU Hi-Z occlusion for arbitrary renderers would need converting them to GPUI or indirect draws. That runs into the shader-variant problem, so it is only feasible for prototypes that already have GPUI shaders in the build.

### Gaps
- Perfect Culling's exact toggle API (`Renderer.enabled` vs `forceRenderingOff`) and its EFT configuration (cell size, volumes per map) need decompile inspection of EFT's Assembly-CSharp / PerfectCulling assembly.
- The internal GPUI Hi-Z implementation was not documented publicly.

## Q7. LOD: LODGroup cost, lodBias, maximumLODLevel, crossfade, impostors

### Takeaway
LODGroups are evaluated during culling, and Unity notes that disabling LODGroups and renderers outright is cheaper than per-frame LOD control. `QualitySettings.lodBias` and `maximumLODLevel` are runtime-settable. The project measured lodBias 2->1 saving 0.11 ms on the main thread and 0.37 ms on the render thread, consistent with fewer or cheaper draws rather than less culling. Impostors need baked assets and are not runtime-feasible without AssetBundle content.

### Cited Findings
- `ForceLOD` gives slightly better CPU than manual toggling. For forcing across many groups over many frames, disabling the LODGroup and renderers may be better for CPU. — [Unity ScriptRef LODGroup.ForceLOD](https://docs.unity3d.com/ScriptReference/LODGroup.ForceLOD.html)
- LOD benefits depend on objects not all being near the camera at once. — [Unity Manual LOD](https://docs.unity3d.com/ru/2018.3/Manual/LevelOfDetail.html)
- No forum benchmark of per-LODGroup CPU overhead exists; the reply to the one forum question about it was "profile it". — [Unity forum "LODGroup performance"](https://forum.unity.com/threads/lodgroup-performance.1314414/)

### Inferences
- With 135k LODGroups, LOD selection itself is a likely large share of the culling cost.
  - **Candidate optimisation:** disable whole LODGroups plus their renderers beyond a distance (via a CullingGroup by cluster), or rely on the last LOD being "culled". The second only helps draws, not culling.
  - **`maximumLODLevel`:** skips high LODs, which reduces draws and triangles. It is a global, cheap knob but visually costly up close.
  - **LOD crossfade:** doubles draws during transitions and needs shader support. Avoid it.

### Gaps
- No data on the per-LODGroup CPU cost in 2022.3.

## Q8. Shadows: cascades, shadowDistance, caster culling, ShadowCastingMode

### Takeaway
Shadow-caster culling and shadow draws scale with casters times cascades. Runtime knobs include:
- `QualitySettings.shadowCascades` (1/2/4)
- `shadowDistance`
- `Light.layerShadowCullDistances`
- `Renderer.shadowCastingMode = Off` for small or distant objects
- disabling shadow-only proxy renderers, already measured at 0.5-1.0 ms

### Cited Findings
- Shadows with four cascades "easily triples the amount of draw calls" with trees (user report). BiRP offers no built-in per-cascade caster control. — [Unity Discussions: culling objects in specific shadow cascades](https://discussions.unity.com/t/culling-objects-in-specific-shadow-cascades/593695)
- Soft shadows mainly cost GPU, not CPU. Consider lower LODs for shadow casting of complex geometry. — [Unity Manual 2022.3 Shadow performance](https://docs.unity.cn/Components/ShadowPerformance.html)
- An HDRP bug culled all 4 cascades even when only one updated. It was not reproducible in 2022.3.17f1, and is HDRP-specific. — [Unity Issue Tracker](https://issuetracker.unity3d.com/issues/hdrp-performance-expensive-culling-for-non-rendered-shadow-cascades)
- Camera layer cull distances also cap shadow casting, and the min with the light's layer shadow distances applies. — [Unity ScriptRef Light.layerShadowCullDistances](https://docs.unity3d.com/kr/6000.0/ScriptReference/Light-layerShadowCullDistances.html)

### Inferences
- The best CPU levers, roughly in order:
  1. Remove distant shadow-only renderers via `enabled=false` (proven).
  2. Combine remaining proxies per cell with CombineMeshes (fewer casters and draws).
  3. Set layerShadowCullDistances on the sun.
  4. Reduce cascades from 4 to 2 (fewer shadow passes and draws; visual cost is lower shadow resolution near the player).
  5. Reduce shadowDistance. This reduces casters drawn, but culling may still test all casters.
- For GPUI-instanced content, shadows go to all cascades, so tuning GPUI shadow distance in the prototypes, if exposed, is the lever.

### Gaps
- No measured numbers for cascade count vs CPU in BiRP.

## Q9. Other CPU-affecting knobs: pixel lights, reflection probes, skinned meshes, Animator culling, extra cameras, command buffers

### Takeaway
Several runtime-settable knobs reduce CPU in forward BiRP:
- `QualitySettings.pixelLightCount`: fewer per-object additive passes, so fewer draws.
- Realtime reflection probes set to OnDemand or lower refresh, with a tight culling mask: each probe update re-renders the scene up to 6 faces.
- `Animator.cullingMode = CullCompletely` for distant characters.
- `SkinnedMeshRenderer.updateWhenOffscreen = false`.
- `QualitySettings.skinWeights` reduced.
- Fewer or cheaper extra cameras, such as scope/optic cameras, by lowering their cullingMask and far clip.

### Cited Findings
- Realtime reflection probes have more overhead than baked ones. Use lower resolution for small or distant reflectors, and use the probe's Culling Mask to exclude small props. — [Unity Manual Reflection Probe performance](https://docs.unity3d.com/Manual/RefProbePerformance.html)
- `AnimatorCullingMode` controls animation for invisible objects. Unity's example switches to CullCompletely by distance "to optimize performance". — [Unity ScriptRef AnimatorCullingMode](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/animatorcullingmode.md)
- Offscreen objects still incur script, physics and animation costs, and CullingGroup is suggested as a fix. — [Game Developer: Don't let offscreen objects ruin your Unity performance](https://www.gamedeveloper.com/game-platforms/don-t-let-offscreen-objects-ruin-your-unity-performance)

### Inferences
- **Extra cameras:** every enabled camera runs its own SceneCulling over all registered renderers. Each extra full-scene camera, such as an optic camera, likely costs about one full culling pass (~1.2-1.6 ms) plus submission. This is inferred from the per-camera culling model. Mod levers:
  - Restrict extra cameras' cullingMask and farClipPlane.
  - Lower their render rate.
  - Use layerCullDistances on them.
- **pixelLightCount:** in forward rendering, each extra per-pixel light adds a ForwardAdd pass per lit object. Lowering it reduces draws and SetPass calls at the cost of light quality.
- **Command buffers:** EFT's post-effects and decals may add command buffers. They are inspectable via `Camera.GetCommandBuffers`, but removing them changes visuals.

### Gaps
- Exact Unity documentation for SkinnedMeshRenderer.updateWhenOffscreen CPU cost and pixelLightCount pass costs was not fetched in this session.
- No EFT-specific data on how many cameras render per frame.
