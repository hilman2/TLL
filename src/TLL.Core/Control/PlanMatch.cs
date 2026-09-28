using System;

namespace TLL.Core.Control
{
    /// <summary>
    /// Recognises a junction's plan after the junction was set up again. The
    /// game may list a node's roads in another order after it rebuilt the
    /// node, which reorders the movements and with them the phases, though
    /// nothing about the plan changed.
    /// </summary>
    public static class PlanMatch
    {
        /// <summary>
        /// Maps each old phase to the new phase giving green to the same
        /// movements, the movements matched by key. Returns null if the plans
        /// differ: another movement, another phase, or another number of
        /// either.
        /// </summary>
        /// <param name="before">The old movements' keys, in the old order.</param>
        /// <param name="after">The new movements' keys, in the new order.</param>
        /// <param name="phasesBefore">Per old phase, its movements as a bit mask over <paramref name="before"/>.</param>
        /// <param name="phasesAfter">Per new phase, its movements as a bit mask over <paramref name="after"/>.</param>
        public static int[] PhaseMap<TKey>(TKey[] before, TKey[] after, ulong[] phasesBefore, ulong[] phasesAfter)
            where TKey : IEquatable<TKey>
        {
            if (before.Length != after.Length || phasesBefore.Length != phasesAfter.Length || before.Length > 64)
                return null;
            var toNew = new int[before.Length];
            ulong taken = 0;
            for (int m = 0; m < before.Length; m++)
            {
                toNew[m] = -1;
                for (int n = 0; n < after.Length; n++)
                {
                    if ((taken & (1UL << n)) == 0 && before[m].Equals(after[n]))
                    {
                        toNew[m] = n;
                        taken |= 1UL << n;
                        break;
                    }
                }
                if (toNew[m] < 0)
                    return null;
            }

            var map = new int[phasesBefore.Length];
            ulong used = 0;
            for (int p = 0; p < phasesBefore.Length; p++)
            {
                ulong moved = 0;
                for (int m = 0; m < before.Length; m++)
                {
                    if ((phasesBefore[p] & (1UL << m)) != 0)
                        moved |= 1UL << toNew[m];
                }
                map[p] = -1;
                for (int q = 0; q < phasesAfter.Length; q++)
                {
                    if ((used & (1UL << q)) == 0 && phasesAfter[q] == moved)
                    {
                        map[p] = q;
                        used |= 1UL << q;
                        break;
                    }
                }
                if (map[p] < 0)
                    return null;
            }
            return map;
        }
    }
}
