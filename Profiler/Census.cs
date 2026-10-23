using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// One-off snapshot of everything that drives render and animation cost: quality settings, cameras
    /// (with command buffers and image-effect components), lights, reflection probes, renderers, skinned
    /// meshes, animators, LOD groups, particles, terrain and behaviour counts. Uses FindObjectsOfType,
    /// so it causes a hitch; only run on demand (F7).
    /// </summary>
    internal static class Census
    {
        public static string Take()
        {
            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            sb.AppendLine($"Scene census {DateTime.Now:yyyy-MM-dd HH:mm:ss}  frame {Time.frameCount}");
            Section(sb, "Quality settings", Quality);
            Section(sb, "Cameras", Cameras);
            Section(sb, "Lights", Lights);
            Section(sb, "Reflection probes", Probes);
            Section(sb, "Renderers", Renderers);
            Section(sb, "Skinned meshes", Skinned);
            Section(sb, "Players (animation / IK state)", Players);
            Section(sb, "Animators", Animators);
            Section(sb, "LOD groups", Lods);
            Section(sb, "Particle systems", Particles);
            Section(sb, "Terrain", Terrains);
            Section(sb, "Behaviours (active and enabled, by type)", Behaviours);
            sb.AppendLine().AppendLine($"Census took {sw.ElapsedMilliseconds} ms");
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, Action<StringBuilder> body)
        {
            sb.AppendLine().AppendLine("== " + title + " ==");
            try
            {
                body(sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine("  failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Quality(StringBuilder sb)
        {
            sb.AppendLine($"  level {QualitySettings.names[QualitySettings.GetQualityLevel()]}  pixelLightCount {QualitySettings.pixelLightCount}  antiAliasing {QualitySettings.antiAliasing}  anisotropic {QualitySettings.anisotropicFiltering}");
            sb.AppendLine($"  shadows {QualitySettings.shadows}  resolution {QualitySettings.shadowResolution}  projection {QualitySettings.shadowProjection}  cascades {QualitySettings.shadowCascades}  distance {QualitySettings.shadowDistance}  nearPlaneOffset {QualitySettings.shadowNearPlaneOffset}  shadowmask {QualitySettings.shadowmaskMode}");
            sb.AppendLine($"  lodBias {QualitySettings.lodBias}  maximumLODLevel {QualitySettings.maximumLODLevel}  skinWeights {QualitySettings.skinWeights}  realtimeReflectionProbes {QualitySettings.realtimeReflectionProbes}  softParticles {QualitySettings.softParticles}  particleRaycastBudget {QualitySettings.particleRaycastBudget}");
            sb.AppendLine($"  asyncUpload timeSlice {QualitySettings.asyncUploadTimeSlice} ms buffer {QualitySettings.asyncUploadBufferSize} MB  streamingMipmaps {QualitySettings.streamingMipmapsActive} budget {QualitySettings.streamingMipmapsMemoryBudget} MB  textureLimit {QualitySettings.globalTextureMipmapLimit}");
            sb.AppendLine($"  vSync {QualitySettings.vSyncCount}  targetFrameRate {Application.targetFrameRate}  maxQueuedFrames {QualitySettings.maxQueuedFrames}  billboardsFaceCamera {QualitySettings.billboardsFaceCameraPosition}  terrainQualityOverrides {QualitySettings.terrainQualityOverrides}");
            sb.AppendLine($"  graphics jobs {SystemInfo.graphicsMultiThreaded}  renderingThreadingMode n/a  colorSpace {QualitySettings.activeColorSpace}  screen {Screen.width}x{Screen.height}");
            sb.AppendLine($"  physics: autoSyncTransforms {Physics.autoSyncTransforms}  simulationMode {Physics.simulationMode}  reuseCollisionCallbacks {Physics.reuseCollisionCallbacks}  defaultSolverIterations {Physics.defaultSolverIterations}");
        }

        private static void Cameras(StringBuilder sb)
        {
            var cams = new Camera[Camera.allCamerasCount];
            Camera.GetAllCameras(cams);
            sb.AppendLine($"  {cams.Length} enabled cameras");
            foreach (var c in cams.OrderBy(c => c.depth))
            {
                var t = c.targetTexture;
                sb.AppendLine($"  [{c.depth,5}] {Path(c.transform)}");
                sb.AppendLine($"        target {(t == null ? "screen" : $"{t.width}x{t.height} {t.format} aa{t.antiAliasing}")}  path {c.renderingPath}->{c.actualRenderingPath}  hdr {c.allowHDR}  msaa {c.allowMSAA}  occlusion {c.useOcclusionCulling}  clear {c.clearFlags}  far {c.farClipPlane}  depthTex {c.depthTextureMode}");
                sb.AppendLine($"        cullingMask {LayerNames(c.cullingMask)}");
                var lcd = c.layerCullDistances;
                if (lcd != null && lcd.Any(d => d > 0))
                    sb.AppendLine("        layerCullDistances " + string.Join(", ", Enumerable.Range(0, 32).Where(i => lcd[i] > 0).Select(i => $"{LayerMask.LayerToName(i)}={lcd[i]:0}")));
                var comps = c.GetComponents<MonoBehaviour>().Where(m => m != null).Select(m => m.GetType().Name + (m.enabled ? "" : "(off)"));
                sb.AppendLine("        components " + string.Join(", ", comps));
                foreach (CameraEvent ev in Enum.GetValues(typeof(CameraEvent)))
                {
                    var bufs = c.GetCommandBuffers(ev);
                    if (bufs.Length > 0) sb.AppendLine($"        cmd {ev}: " + string.Join(", ", bufs.Select(b => $"{b.name}({b.sizeInBytes / 1024}KB)")));
                }
            }
        }

        private static void Lights(StringBuilder sb)
        {
            var lights = UnityEngine.Object.FindObjectsOfType<Light>();
            sb.AppendLine($"  {lights.Length} active lights; enabled {lights.Count(l => l.enabled)}");
            foreach (var g in lights.Where(l => l.enabled).GroupBy(l => $"{l.type} shadows={l.shadows} mode={l.renderMode} bake={(l.bakingOutput.isBaked ? l.bakingOutput.lightmapBakeType.ToString() : "Realtime")}").OrderByDescending(g => g.Count()))
                sb.AppendLine($"    {g.Count(),5}  {g.Key}");
            sb.AppendLine("  Enabled realtime shadow casters (largest range first):");
            foreach (var l in lights.Where(l => l.enabled && l.shadows != LightShadows.None && !(l.bakingOutput.isBaked && l.bakingOutput.lightmapBakeType == LightmapBakeType.Baked)).OrderByDescending(l => l.range).Take(40))
                sb.AppendLine($"    {l.type,-11} range {l.range,6:0.0} intensity {l.intensity,5:0.00} res {l.shadowResolution} custom {l.shadowCustomResolution} strength {l.shadowStrength:0.00} mask {LayerNames(l.cullingMask)}  {Path(l.transform)}");
        }

        private static void Probes(StringBuilder sb)
        {
            var probes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>();
            sb.AppendLine($"  {probes.Length} active probes");
            foreach (var g in probes.Where(p => p.enabled).GroupBy(p => $"{p.mode} refresh={p.refreshMode} slicing={p.timeSlicingMode} res={p.resolution} hdr={p.hdr}").OrderByDescending(g => g.Count()))
                sb.AppendLine($"    {g.Count(),5}  {g.Key}");
            foreach (var p in probes.Where(p => p.enabled && p.mode == ReflectionProbeMode.Realtime).Take(20))
                sb.AppendLine($"    realtime: {Path(p.transform)} refresh={p.refreshMode} res={p.resolution} far={p.farClipPlane} mask {LayerNames(p.cullingMask)}");
        }

        private static void Renderers(StringBuilder sb)
        {
            var rs = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var enabled = rs.Where(r => r.enabled).ToArray();
            var visible = enabled.Where(r => r.isVisible).ToArray();
            sb.AppendLine($"  {rs.Length} active renderers, {enabled.Length} enabled, {visible.Length} visible to some camera last frame");
            foreach (var g in enabled.GroupBy(r => r.GetType().Name).OrderByDescending(g => g.Count()))
                sb.AppendLine($"    {g.Key,-28} {g.Count(),7} enabled  {g.Count(r => r.isVisible),7} visible  {g.Count(r => r.shadowCastingMode != ShadowCastingMode.Off),7} cast shadows");
            sb.AppendLine("  Shadow casting modes (enabled): " + string.Join(", ", enabled.GroupBy(r => r.shadowCastingMode).Select(g => $"{g.Key}={g.Count()}")));
            int mats = visible.Sum(r => r.sharedMaterials.Length);
            var shaders = visible.SelectMany(r => r.sharedMaterials).Where(m => m != null && m.shader != null).GroupBy(m => m.shader.name).OrderByDescending(g => g.Count()).ToList();
            var distinctMats = visible.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().Count();
            sb.AppendLine($"  Visible: {mats} material slots, {distinctMats} distinct materials, {shaders.Count} shaders");
            sb.AppendLine("  Top shaders on visible renderers (material slots):");
            foreach (var g in shaders.Take(25)) sb.AppendLine($"    {g.Count(),6}  {g.Key}");
            // Distance bands from the main camera: culling cost scales with every enabled renderer, visible or not.
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 cp = cam.transform.position;
                float[] bands = { 25, 50, 100, 200, 400, 800, float.MaxValue };
                string BandName(int i) => i == bands.Length - 1 ? $">{bands[i - 1]:0}m" : $"<{bands[i]:0}m";
                int Band(Renderer r)
                {
                    float d = Vector3.Distance(cp, r.bounds.center);
                    for (int i = 0; i < bands.Length; i++) if (d < bands[i]) return i;
                    return bands.Length - 1;
                }
                var byBand = enabled.GroupBy(r => (Band(r), r.shadowCastingMode)).ToDictionary(g => g.Key, g => g.Count());
                var modes = new[] { ShadowCastingMode.On, ShadowCastingMode.Off, ShadowCastingMode.ShadowsOnly, ShadowCastingMode.TwoSided };
                sb.AppendLine($"  Enabled renderers by distance from camera ({cp.x:0},{cp.y:0},{cp.z:0}) and shadow mode:");
                sb.AppendLine($"    {"band",-8} " + string.Join(" ", modes.Select(m => $"{m,12}")) + $" {"visible",9}");
                for (int i = 0; i < bands.Length; i++)
                {
                    int vis = visible.Count(r => Band(r) == i);
                    sb.AppendLine($"    {BandName(i),-8} " + string.Join(" ", modes.Select(m => $"{(byBand.TryGetValue((i, m), out int n) ? n : 0),12}")) + $" {vis,9}");
                }
            }
            sb.AppendLine("  Most common ShadowsOnly renderer names (enabled):");
            foreach (var g in enabled.Where(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly).GroupBy(r => r.name).OrderByDescending(g => g.Count()).Take(15))
                sb.AppendLine($"    {g.Count(),7}  {g.Key}  layer {LayerMask.LayerToName(g.First().gameObject.layer)}  e.g. {Path(g.First().transform)}");
            sb.AppendLine("  ShadowsOnly by layer: " + string.Join(", ", enabled.Where(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly).GroupBy(r => LayerMask.LayerToName(r.gameObject.layer)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}={g.Count()}")));
            // Can far shadow proxies be merged into fewer meshes? Mesh.CombineMeshes needs readable meshes; static
            // batching would already have merged them.
            var proxies = rs.Where(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly).ToArray();
            var proxyMeshes = proxies.Select(r => r.GetComponent<MeshFilter>()?.sharedMesh).Where(m => m != null).ToArray();
            var distinctProxyMeshes = proxyMeshes.Distinct().ToArray();
            sb.AppendLine($"  ShadowsOnly renderers (active, enabled or not): {proxies.Length}; with a mesh {proxyMeshes.Length}, {distinctProxyMeshes.Length} distinct meshes, {distinctProxyMeshes.Count(m => m.isReadable)} of them readable; part of a static batch {proxies.Count(r => r.isPartOfStaticBatch)}; vertices (all instances) {proxyMeshes.Sum(m => (long)m.vertexCount)}");
            sb.AppendLine("  Renderers with a LODGroup parent vs without: " + enabled.Count(r => r.GetComponentInParent<LODGroup>() != null) + " / " + enabled.Count(r => r.GetComponentInParent<LODGroup>() == null));
            sb.AppendLine("  Visible renderers by layer:");
            foreach (var g in visible.GroupBy(r => r.gameObject.layer).OrderByDescending(g => g.Count()).Take(15))
                sb.AppendLine($"    {g.Count(),6}  {LayerMask.LayerToName(g.Key)}");
        }

        private static void Skinned(StringBuilder sb)
        {
            var sk = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>().Where(s => s.enabled).ToArray();
            sb.AppendLine($"  {sk.Length} enabled; visible {sk.Count(s => s.isVisible)}; updateWhenOffscreen {sk.Count(s => s.updateWhenOffscreen)}; skinnedMotionVectors {sk.Count(s => s.skinnedMotionVectors)}");
            sb.AppendLine("  quality: " + string.Join(", ", sk.GroupBy(s => s.quality).Select(g => $"{g.Key}={g.Count()}")));
            if (sk.Length > 0)
            {
                sb.AppendLine($"  bones per renderer avg {sk.Average(s => (double)(s.bones?.Length ?? 0)):0.0}, max {sk.Max(s => s.bones?.Length ?? 0)}; vertices avg {sk.Average(s => (double)(s.sharedMesh != null ? s.sharedMesh.vertexCount : 0)):0}, total visible {sk.Where(s => s.isVisible).Sum(s => (long)(s.sharedMesh != null ? s.sharedMesh.vertexCount : 0))}");
                sb.AppendLine("  updateWhenOffscreen=true (first 20): " + string.Join(", ", sk.Where(s => s.updateWhenOffscreen).Take(20).Select(s => Path(s.transform))));
            }
        }

        private static void Players(StringBuilder sb)
        {
            if (!Comfort.Common.Singleton<EFT.GameWorld>.Instantiated) { sb.AppendLine("  no game world"); return; }
            var gw = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            var hs = EFTHardSettings.Instance;
            sb.AppendLine($"  CULL_GROUNDER (IK distance) {hs.CULL_GROUNDER} m, AnimatorCullDistance {hs.AnimatorCullDistance} m, CULLING_PLAYER_DISTANCE {hs.CULLING_PLAYER_DISTANCE} m, UseBodyFastAnimator {AppEnvironment.Config?.UseBodyFastAnimator}");
            sb.AppendLine($"  {"who",-22} {"dist",6} {"onScr",5} {"forcedOff",9} {"body anim",-34} {"arms anim",-44} {"hands",-24} mask");
            var renderers = new List<Renderer>();
            foreach (var p in gw.AllAlivePlayersList.OrderBy(p => p == gw.MainPlayer ? -1f : p.Distance))
            {
                if (p == null) continue;
                string who = p == gw.MainPlayer ? "(you)" : (p.IsAI ? p.Profile?.Info?.Settings?.Role.ToString() ?? "AI" : "player");
                var body = p.BodyAnimatorCommon;
                var arms = p.ArmsAnimatorCommon;
                string bodyInfo = body == null ? "-" : $"{(body.enabled ? "on" : "OFF")} {body.cullingMode} L{body.layerCount} {Trunc(body.GetType().Name, 12)}";
                string armsInfo = arms == null ? "-" : $"{(arms.enabled ? "on" : "OFF")} {arms.cullingMode} L{arms.layerCount} {Trunc(arms.name, 18)}";
                renderers.Clear();
                int forced = 0;
                try
                {
                    p.PlayerBody.GetRenderersNonAlloc(renderers);
                    forced = renderers.Count(r => r != null && r.forceRenderingOff);
                }
                catch
                {
                    // body not built yet
                }
                sb.AppendLine($"  {Trunc(who, 22),-22} {(p == gw.MainPlayer ? 0f : p.Distance),6:0} {(p.OnScreen ? "yes" : "no"),5} {forced + "/" + renderers.Count,9} {bodyInfo,-34} {armsInfo,-44} {Trunc(p.HandsController?.GetType().Name ?? "-", 24),-24} {p.EnabledAnimators}");
            }
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

        private static void Animators(StringBuilder sb)
        {
            var an = UnityEngine.Object.FindObjectsOfType<Animator>();
            var en = an.Where(a => a.isActiveAndEnabled).ToArray();
            sb.AppendLine($"  {an.Length} active, {en.Length} enabled");
            foreach (var g in en.GroupBy(a => $"culling={a.cullingMode} update={a.updateMode} rootMotion={a.applyRootMotion}").OrderByDescending(g => g.Count()))
                sb.AppendLine($"    {g.Count(),5}  {g.Key}");
            sb.AppendLine("  By controller (enabled):");
            foreach (var g in en.GroupBy(a => a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "(none)").OrderByDescending(g => g.Count()).Take(25))
                sb.AppendLine($"    {g.Count(),5}  {g.Key}  layers {g.First().layerCount}  culling {string.Join("/", g.Select(a => a.cullingMode).Distinct())}");
        }

        private static void Lods(StringBuilder sb)
        {
            var lods = UnityEngine.Object.FindObjectsOfType<LODGroup>().Where(l => l.enabled).ToArray();
            sb.AppendLine($"  {lods.Length} enabled LOD groups; fade modes: " + string.Join(", ", lods.GroupBy(l => l.fadeMode).Select(g => $"{g.Key}={g.Count()}")));
            if (lods.Length > 0) sb.AppendLine($"  LOD levels avg {lods.Average(l => (double)l.lodCount):0.0}; animateCrossFading {lods.Count(l => l.animateCrossFading)}");
            // Unity evaluates every enabled LOD group per camera, whether or not any of its renderers is enabled.
            int noRenderers = 0, allOff = 0, onlyShadowsOnly = 0, onlyShadowsOnlyAllOff = 0, mixed = 0;
            foreach (var l in lods)
            {
                var rend = l.GetLODs().SelectMany(x => x.renderers).Where(r => r != null).Distinct().ToArray();
                if (rend.Length == 0) { noRenderers++; continue; }
                bool off = rend.All(r => !r.enabled);
                bool so = rend.All(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly);
                if (off) allOff++;
                if (so) { onlyShadowsOnly++; if (off) onlyShadowsOnlyAllOff++; }
                else if (rend.Any(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly)) mixed++;
            }
            sb.AppendLine($"  enabled groups with no renderers {noRenderers}; with every renderer disabled {allOff}; shadow-proxy-only {onlyShadowsOnly} (every renderer disabled: {onlyShadowsOnlyAllOff}); mixing proxies with normal renderers {mixed}");
        }

        private static void Particles(StringBuilder sb)
        {
            var ps = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            sb.AppendLine($"  {ps.Length} active systems, {ps.Count(p => p.isPlaying)} playing, {ps.Sum(p => (long)p.particleCount)} particles alive");
            foreach (var g in ps.Where(p => p.isPlaying).GroupBy(p => p.name).OrderByDescending(g => g.Count()).Take(15))
                sb.AppendLine($"    {g.Count(),5}  {g.Key}  particles {g.Sum(p => p.particleCount)}");
        }

        private static void Terrains(StringBuilder sb)
        {
            foreach (var t in Terrain.activeTerrains)
                sb.AppendLine($"  {Path(t.transform)} pixelError {t.heightmapPixelError} basemapDist {t.basemapDistance} trees {t.treeDistance}/{t.treeBillboardDistance} details {t.detailObjectDistance}@{t.detailObjectDensity} instanced {t.drawInstanced} shadows {t.shadowCastingMode} drawTrees {t.drawTreesAndFoliage}");
            if (Terrain.activeTerrains.Length == 0) sb.AppendLine("  none");
        }

        private static void Behaviours(StringBuilder sb)
        {
            var mbs = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>().Where(m => m.isActiveAndEnabled).ToArray();
            sb.AppendLine($"  {mbs.Length} active and enabled MonoBehaviours");
            foreach (var g in mbs.GroupBy(m => m.GetType().FullName).OrderByDescending(g => g.Count()).Take(50))
                sb.AppendLine($"    {g.Count(),6}  {g.Key}");
        }

        private static string LayerNames(int mask)
        {
            if (mask == -1) return "Everything";
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? i.ToString() : LayerMask.LayerToName(i));
            return string.Join("|", names);
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
