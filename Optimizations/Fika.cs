using System;
using EFT;
using HarmonyLib;

namespace SPTOptimizer.Optimizations
{
    /// <summary>
    /// What Fika (if installed) says about a player, by reflection so the plugin runs with or without it.
    /// On a Fika client every bot and every other human is an ObservedPlayer: a puppet whose position comes from the
    /// host's snapshots (ObservedPlayer.ManualStateUpdate), with no AI. On the host, bots are local players (IsAI).
    /// </summary>
    internal static class Fika
    {
        public static readonly Type ObservedPlayerType = AccessTools.TypeByName("Fika.Core.Main.Players.ObservedPlayer");

        private static readonly AccessTools.FieldRef<object, bool> IsObservedAIRef = ObservedPlayerType == null ? null
            : AccessTools.FieldRefAccess<bool>(AccessTools.TypeByName("Fika.Core.Main.Players.FikaPlayer"), "IsObservedAI");

        private static readonly Func<bool> IsHeadlessGetter = MakeGetter("Fika.Core.Main.Utils.FikaBackendUtils", "IsHeadless");

        public static bool Present => ObservedPlayerType != null;

        /// <summary>This process is the Fika headless host.</summary>
        public static bool IsHeadless => IsHeadlessGetter != null && IsHeadlessGetter();

        public static bool IsObserved(Player p) => ObservedPlayerType != null && ObservedPlayerType.IsInstanceOfType(p);

        public static bool IsObservedBot(Player p) => IsObserved(p) && IsObservedAIRef(p);

        private static Func<bool> MakeGetter(string type, string property)
        {
            var t = AccessTools.TypeByName(type);
            var getter = t == null ? null : AccessTools.PropertyGetter(t, property);
            return getter == null ? null : (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
        }
    }
}
