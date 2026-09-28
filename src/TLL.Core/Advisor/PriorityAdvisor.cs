namespace TLL.Core.Advisor
{
    /// <summary>
    /// Chooses the priority road of a junction without signals from its
    /// traffic. The game decides priority by the kind of road: a side road
    /// gives way to bigger roads, and between roads of one kind the one from
    /// the right goes first. Where one pair of approaches carries most of the
    /// traffic, that pair gets the priority road and the others give way,
    /// even where the main flow turns or runs along smaller roads. Elsewhere
    /// the game's rule stays.
    /// </summary>
    public static class PriorityAdvisor
    {
        /// <summary>Vehicles per hour between the two approaches below which the game's rule stays.</summary>
        public const float MinFlow = 120f;

        /// <summary>Share of all traffic through the junction the pair must carry to become the priority road.</summary>
        public const float StartShare = 0.5f;

        /// <summary>How many times the next pair's traffic the pair must carry to become the priority road.</summary>
        public const float StartLead = 2f;

        /// <summary>
        /// Lower bars for a priority road already set, so a junction does not
        /// switch back and forth as the traffic shifts a little.
        /// </summary>
        public const float KeepShare = 0.4f;
        public const float KeepLead = 1.5f;
        public const float KeepFlow = 90f;

        /// <summary>
        /// The two approaches that should form the priority road, as a mask,
        /// or 0 to leave the game's rule.
        /// </summary>
        /// <param name="flow">Vehicles per hour from approach [s] into approach [t].</param>
        /// <param name="current">The approaches that form the priority road now, 0 for none.</param>
        public static ulong Choose(float[,] flow, ulong current)
        {
            int n = flow.GetLength(0);
            float total = 0f;
            for (int s = 0; s < n; s++)
            {
                for (int t = 0; t < n; t++)
                    total += flow[s, t];
            }
            if (total <= 0f)
                return 0UL;

            ulong best = 0UL;
            float bestFlow = 0f;
            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    float f = Pair(flow, a, b);
                    if (f > bestFlow)
                    {
                        bestFlow = f;
                        best = (1UL << a) | (1UL << b);
                    }
                }
            }
            if (Qualifies(flow, best, total, MinFlow, StartShare, StartLead))
                return best;
            if (current != 0UL && Qualifies(flow, current, total, KeepFlow, KeepShare, KeepLead))
                return current;
            return 0UL;
        }

        /// <summary>
        /// Whether the pair of approaches in <paramref name="pair"/> carries
        /// enough of the junction's traffic, and enough more than any other
        /// pair, to be its priority road.
        /// </summary>
        private static bool Qualifies(float[,] flow, ulong pair, float total, float minFlow, float share, float lead)
        {
            int n = flow.GetLength(0);
            int a = -1;
            int b = -1;
            for (int i = 0; i < n; i++)
            {
                if ((pair & (1UL << i)) == 0)
                    continue;
                if (a < 0)
                    a = i;
                else
                    b = i;
            }
            if (a < 0 || b < 0)
                return false;
            float main = Pair(flow, a, b);
            float next = 0f;
            for (int x = 0; x < n; x++)
            {
                for (int y = x + 1; y < n; y++)
                {
                    if (x == a && y == b)
                        continue;
                    next = System.Math.Max(next, Pair(flow, x, y));
                }
            }
            return main >= minFlow && main >= share * total && main >= lead * next;
        }

        private static float Pair(float[,] flow, int a, int b)
        {
            return flow[a, b] + flow[b, a];
        }
    }
}
