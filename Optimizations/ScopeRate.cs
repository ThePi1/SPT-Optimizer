using EFT.CameraControl;
using UnityEngine;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// VISUAL TRADE-OFF (opt-in): the magnified scope's camera renders only every <see cref="Interval"/>th frame.
    ///
    /// EFT draws a magnified scope picture-in-picture: OpticCameraManager's camera renders the whole scene a second time
    /// into the lens texture, with its own culling and draw calls. On a main-thread-bound client that is the largest
    /// single cost while scoped: ~1.8 ms looking at a nearby wall, ~3.5 ms down a street (profiler step ScopeHalfRate:
    /// −0.89 / −1.77 ms at half rate, captures 20261022-102433 / 093533). On skipped frames the camera component is
    /// switched off, so the lens keeps showing the last image; the main view renders every frame.
    ///
    /// EFT shows and hides the optic camera with SetActive, which is left alone. Only a camera this class switched off
    /// is ever switched back on: PiP-Disabler turns the optic camera off with Camera.enabled (and clears its target
    /// texture), and turning it on again would draw it onto the screen. With PiP-Disabler the option therefore only acts
    /// on the scopes it leaves to the game (variable, thermal, night vision). Aim, recoil and hits don't use this image.
    /// While the view turns faster than <see cref="TurnSpeed"/> every frame is rendered, which hides the judder where
    /// it would show most.
    /// </summary>
    internal static class ScopeRate
    {
        public static bool Enabled;
        public static int Interval = 2;
        public static float TurnSpeed = 15f; // degrees per second of the main camera; 0 = never full rate

        private static Camera _offCamera; // the optic camera we switched off for this frame
        private static int _skipped;      // frames skipped since the last render
        private static Vector3 _lastForward;
        private static bool _hasForward;
        private static long _skippedRenders, _turningRenders;
        private static float _nextFlush;

        /// <summary>Update phase, before rendering.</summary>
        public static void Tick()
        {
            Flush();
            // Undo last frame's switch-off first; it is only kept for the frames we decide to skip again below.
            if (_offCamera != null)
            {
                _offCamera.enabled = true;
                _offCamera = null;
            }
            if (!Enabled || !CameraManager.Exist) { _hasForward = false; return; }

            var cm = CameraManager.Instance;
            var ocm = cm.OpticCameraManager;
            var cam = ocm?.Camera;
            if (cam == null || !ocm.IsAnyOpticCameraRendering || !cam.enabled || !cam.gameObject.activeInHierarchy)
            {
                _skipped = 0;
                _hasForward = false;
                return;
            }

            bool turning = false;
            var main = cm.Camera;
            if (main != null && TurnSpeed > 0f)
            {
                var fwd = main.transform.forward;
                if (_hasForward && Time.unscaledDeltaTime > 0f)
                    turning = Vector3.Angle(_lastForward, fwd) / Time.unscaledDeltaTime > TurnSpeed;
                _lastForward = fwd;
                _hasForward = true;
            }

            if (turning || _skipped >= Interval - 1)
            {
                if (turning && _skipped < Interval - 1) _turningRenders++;
                _skipped = 0; // render this frame
                return;
            }
            cam.enabled = false;
            _offCamera = cam;
            _skipped++;
            _skippedRenders++;
        }

        /// <summary>Switched off, or leaving a raid: render again normally.</summary>
        public static void Release()
        {
            if (_offCamera != null) _offCamera.enabled = true;
            _offCamera = null;
            _skipped = 0;
            _hasForward = false;
        }

        private static void Flush()
        {
            if (Time.realtimeSinceStartup < _nextFlush) return;
            _nextFlush = Time.realtimeSinceStartup + 1f;
            if (_skippedRenders > 0) Stats.Add("scopeRate.skippedRenders", _skippedRenders);
            if (_turningRenders > 0) Stats.Add("scopeRate.turningRenders", _turningRenders);
            _skippedRenders = _turningRenders = 0;
        }
    }
}
