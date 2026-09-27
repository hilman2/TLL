using System.Collections.Generic;

namespace TLL.Core.Planning
{
    /// <summary>One signal phase: the movements that have green together.</summary>
    public struct Phase
    {
        /// <summary>Bit i set: movement i has green in this phase.</summary>
        public ulong Green;

        /// <summary>
        /// Subset of <see cref="Green"/>: movements that have green but must
        /// give way to another movement of the same phase. The mod shows them
        /// the game's yield signal instead of a plain go.
        /// </summary>
        public ulong Permitted;

        public bool Has(int movement)
        {
            return (Green & (1UL << movement)) != 0;
        }

        public int Count
        {
            get
            {
                int count = 0;
                ulong bits = Green;
                while (bits != 0)
                {
                    bits &= bits - 1;
                    count++;
                }
                return count;
            }
        }
    }

    public sealed class PhasePlan
    {
        public readonly List<Phase> Phases = new List<Phase>();

        /// <summary>Movements that no phase gives green. Empty for every valid plan.</summary>
        public ulong Uncovered(int movementCount)
        {
            ulong all = movementCount >= 64 ? ulong.MaxValue : (1UL << movementCount) - 1;
            ulong covered = 0;
            foreach (Phase p in Phases)
                covered |= p.Green;
            return all & ~covered;
        }
    }
}
