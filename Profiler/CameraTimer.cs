using System.Collections.Generic;
using UnityEngine;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Splits rendering time per camera using Unity's global camera callbacks (built-in render pipeline):
    ///   onPreCull → onPreRender   = "Cam.&lt;name&gt;.Cull"   (culling, OnBecameVisible/OnWillRenderObject, other onPreCull subscribers)
    ///   onPreRender → onPostRender = "Cam.&lt;name&gt;.Render" (shadows, opaque/transparent passes, command buffers before post)
    /// Time after onPostRender (OnRenderImage image effects, present) falls back to whatever slot was active
    /// before the camera started, usually PostLateUpdate.FinishFrameRendering. Nested Camera.Render() calls
    /// (scopes, reflections) are handled with a stack.
    /// </summary>
    internal static class CameraTimer
    {
        private struct CamSlots
        {
            public int Cull;
            public int Render;
        }

        private const int MaxDepth = 32;
        private static readonly Dictionary<int, CamSlots> Slots = new Dictionary<int, CamSlots>();
        private static readonly int[] Stack = new int[MaxDepth];
        private static int _depth;
        private static int _renders;
        private static bool _hooked;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
        }

        public static int TakeRenderCount()
        {
            int n = _renders;
            _renders = 0;
            _depth = 0; // a camera that never reached onPostRender must not leak into the next frame
            return n;
        }

        private static CamSlots Get(Camera cam)
        {
            int id = cam.GetInstanceID();
            if (Slots.TryGetValue(id, out var s)) return s;

            // First time this camera instance is seen: name allocation is fine here. Cameras are keyed by name so
            // the same camera recreated in a new raid lands in the same columns (simultaneous same-name cameras merge).
            string name = string.IsNullOrEmpty(cam.name) ? "unnamed" : cam.name.Replace('.', '_').Replace(',', '_');
            s = new CamSlots
            {
                Cull = FrameRecorder.GetSlot("Cam." + name + ".Cull"),
                Render = FrameRecorder.GetSlot("Cam." + name + ".Render"),
            };
            Slots[id] = s;
            return s;
        }

        private static void OnPreCull(Camera cam)
        {
            if (!FrameRecorder.Recording || _depth >= MaxDepth) return;
            _renders++;
            Stack[_depth++] = FrameRecorder.CurrentSlot;
            FrameRecorder.Mark(Get(cam).Cull);
        }

        private static void OnPreRender(Camera cam)
        {
            if (!FrameRecorder.Recording || _depth == 0) return;
            FrameRecorder.Mark(Get(cam).Render);
        }

        private static void OnPostRender(Camera cam)
        {
            if (!FrameRecorder.Recording || _depth == 0) return;
            FrameRecorder.Mark(Stack[--_depth]);
        }
    }
}
