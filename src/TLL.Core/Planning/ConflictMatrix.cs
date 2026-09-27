using System;

namespace TLL.Core.Planning
{
    /// <summary>How movement A relates to movement B when both have green.</summary>
    public enum Relation : byte
    {
        /// <summary>The paths do not meet.</summary>
        Compatible = 0,

        /// <summary>The paths meet and A gives way to B. Both may have green together.</summary>
        Yields = 1,

        /// <summary>The paths meet and B gives way to A. Both may have green together.</summary>
        HasPriority = 2,

        /// <summary>The paths meet and neither can give way safely. Never green together.</summary>
        Hard = 3,
    }

    /// <summary>How two lanes of a junction meet, as far as the planner cares.</summary>
    public enum PathContact : byte
    {
        None,

        /// <summary>The paths start from the same lane and split. No conflict.</summary>
        Diverge,

        /// <summary>The paths end in the same lane.</summary>
        Merge,

        /// <summary>The paths cross each other.</summary>
        Cross,
    }

    /// <summary>
    /// Pairwise relations between the movements of one junction.
    /// Setting one direction of a pair also sets the mirrored one, so the
    /// matrix is consistent by construction.
    /// </summary>
    public sealed class ConflictMatrix
    {
        private readonly Relation[] m_Cells;

        public int Count { get; }

        public ConflictMatrix(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            Count = count;
            m_Cells = new Relation[count * count];
        }

        public Relation Get(int a, int b)
        {
            return m_Cells[a * Count + b];
        }

        public void Set(int a, int b, Relation relation)
        {
            if (a == b)
                return;
            m_Cells[a * Count + b] = relation;
            m_Cells[b * Count + a] = Mirror(relation);
        }

        /// <summary>
        /// Records a relation unless a stronger one is already there.
        /// Several lane pairs of the same two movements can meet in different
        /// ways; the movement pair takes the most restrictive of them.
        /// </summary>
        public void Merge(int a, int b, Relation relation)
        {
            if (a == b)
                return;
            Relation current = Get(a, b);
            if (current == Relation.Hard || relation == Relation.Compatible)
                return;
            if (relation == Relation.Hard || current == Relation.Compatible)
            {
                Set(a, b, relation);
                return;
            }
            // One pair of lanes says A yields, another says B yields. Neither
            // can rely on the other, so the movements must not run together.
            if (current != relation)
                Set(a, b, Relation.Hard);
        }

        /// <summary>
        /// Takes over every relation of <paramref name="other"/> that is
        /// stricter than the one here, pair by pair (see <see cref="Merge"/>).
        /// </summary>
        public void Tighten(ConflictMatrix other)
        {
            if (other.Count != Count)
                throw new ArgumentException("Matrices differ in size.", nameof(other));
            for (int a = 0; a < Count; a++)
            {
                for (int b = a + 1; b < Count; b++)
                    Merge(a, b, other.Get(a, b));
            }
        }

        /// <summary>Whether A and B may have green at the same time.</summary>
        public bool CanShare(int a, int b)
        {
            return Get(a, b) != Relation.Hard;
        }

        /// <summary>Whether A must give way to some other movement in <paramref name="phase"/>.</summary>
        public bool YieldsWithin(int a, ulong phase)
        {
            for (int b = 0; b < Count; b++)
            {
                if ((phase & (1UL << b)) != 0 && Get(a, b) == Relation.Yields)
                    return true;
            }
            return false;
        }

        public ConflictMatrix Clone()
        {
            var copy = new ConflictMatrix(Count);
            Array.Copy(m_Cells, copy.m_Cells, m_Cells.Length);
            return copy;
        }

        private static Relation Mirror(Relation relation)
        {
            switch (relation)
            {
                case Relation.Yields:
                    return Relation.HasPriority;
                case Relation.HasPriority:
                    return Relation.Yields;
                default:
                    return relation;
            }
        }
    }
}
