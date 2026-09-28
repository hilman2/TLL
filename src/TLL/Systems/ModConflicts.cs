using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// Mods that drive traffic lights themselves. Running one of them next to
    /// TLL means two controllers switching the same signals, so TLL stands
    /// back while one is loaded.
    /// </summary>
    internal static class ModConflicts
    {
        private struct Known
        {
            public string Namespace;
            public string Name;
        }

        // Traffic Lights Enhancement switches the game's traffic light systems
        // off and runs its own copies over every junction, TLL's included.
        private static readonly Known[] s_Known =
        {
            new Known { Namespace = "C2VM.TrafficLightsEnhancement", Name = "Traffic Lights Enhancement" },
        };

        private static bool s_TrafficResolved;
        private static System.Type s_TrafficConnections;

        /// <summary>
        /// The tag the Traffic mod (krzychu124) puts on junctions whose lane
        /// connections the player has changed with it, or null without that
        /// mod. The autopilot leaves the turns of such junctions to the
        /// player. Looked up once, by name, since TLL does not reference the
        /// mod.
        /// </summary>
        public static System.Type TrafficConnections
        {
            get
            {
                if (!s_TrafficResolved)
                {
                    s_TrafficResolved = true;
                    foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        s_TrafficConnections = assembly.GetType("Traffic.Components.ModifiedConnections", false);
                        if (s_TrafficConnections != null)
                            break;
                    }
                }
                return s_TrafficConnections;
            }
        }

        /// <returns>The name of a loaded mod that also drives traffic lights, or null.</returns>
        public static string Find(World world)
        {
            foreach (ComponentSystemBase system in world.Systems)
            {
                string ns = system.GetType().Namespace;
                if (ns == null)
                    continue;
                foreach (Known known in s_Known)
                {
                    if (ns.StartsWith(known.Namespace, System.StringComparison.Ordinal))
                        return known.Name;
                }
            }
            return null;
        }
    }
}
