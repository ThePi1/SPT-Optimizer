using Comfort.Common;
using EFT;
using EFT.UI.Screens;

namespace SPTOptimizer.Optimizations
{
    /// <summary>Raid and UI state shared by the optimizations. Uses only named (non-obfuscated) game types.</summary>
    internal static class Raid
    {
        /// <summary>A raid world with a live local player (hideout excluded).</summary>
        public static bool InRaid(out GameWorld world)
        {
            world = null;
            if (!Singleton<GameWorld>.Instantiated) return false;
            world = Singleton<GameWorld>.Instance;
            return world != null && !(world is HideoutGameWorld) && world.MainPlayer != null;
        }

        /// <summary>The screen on top of the UI stack; None/BattleUI mean the player is looking at the game.</summary>
        public static EEftScreenType CurrentScreen()
        {
            var controller = CurrentScreenSingletonClass.Instance.CurrentScreenController;
            return controller != null ? controller.ScreenType : EEftScreenType.None;
        }
    }
}
