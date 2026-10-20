using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace SPTOptimizer.Profiler
{
    /// <summary>
    /// Per-frame counts of the other players by the conditions that decide their animation and IK cost:
    ///  - visible: Player.OnScreen (body or feet skin renderer visible to any camera, shadow maps included)
    ///  - IK: visible and within EFTHardSettings.CULL_GROUNDER, the gate for FBBIK/limb IK in Player.VisualPass
    ///  - near: within EFTHardSettings.AnimatorCullDistance, where EFT forces AlwaysAnimate even off-screen
    ///  - always: body animator currently in AlwaysAnimate culling mode
    /// </summary>
    internal static class PlayerStats
    {
        public static void Fill(float[] row, int o)
        {
            if (!Singleton<GameWorld>.Instantiated) return;
            var gw = Singleton<GameWorld>.Instance;
            var main = gw.MainPlayer;
            List<Player> players = gw.AllAlivePlayersList;
            var hs = EFTHardSettings.Instance;
            float ikDist = hs != null ? hs.CULL_GROUNDER : 40f;
            float nearDist = hs != null ? hs.AnimatorCullDistance : 10f;

            int alive = 0, visible = 0, ik = 0, near = 0, always = 0;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || p == main) continue;
                alive++;
                float d = p.Distance;
                bool vis = p.OnScreen;
                if (vis) visible++;
                if (vis && d <= ikDist) ik++;
                if (d <= nearDist) near++;
                var body = p.BodyAnimatorCommon;
                if (body != null && body.cullingMode == AnimatorCullingMode.AlwaysAnimate) always++;
            }
            row[o + Col.BotsAlive] = alive;
            row[o + Col.BotsVisible] = visible;
            row[o + Col.BotsIk] = ik;
            row[o + Col.BotsNear] = near;
            row[o + Col.BotsAnimAlways] = always;
        }
    }
}
