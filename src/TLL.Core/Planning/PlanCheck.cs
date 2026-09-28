using System.Collections.Generic;

namespace TLL.Core.Planning
{
    public enum FindingKind : byte
    {
        /// <summary>A movement or crosswalk has green in no phase. Its traffic would wait forever.</summary>
        NoGreen,

        /// <summary>Two movements whose paths cross have green in the same phase.</summary>
        Crossing,

        /// <summary>Two movements from one approach lane have green in different phases.</summary>
        SplitLane,

        /// <summary>More phases than the game has signal groups (<see cref="PhasePlanner.MaxPhases"/>).</summary>
        TooManyPhases,

        /// <summary>A turn gives way to heavy traffic in its phase and will hardly find a gap.</summary>
        HeavyYield,

        /// <summary>The cycle is so long that everyone waits long at red.</summary>
        LongCycle,

        /// <summary>A phase gives green to nothing.</summary>
        EmptyPhase,

        /// <summary>A phase gives green to exactly what an earlier one does.</summary>
        DuplicatePhase,
    }

    public enum Severity : byte
    {
        /// <summary>The plan cannot run like this.</summary>
        Error,

        /// <summary>The plan runs, but traffic will suffer.</summary>
        Warning,

        /// <summary>The plan runs; something is superfluous.</summary>
        Info,
    }

    /// <summary>One thing the check found about a plan.</summary>
    public struct Finding
    {
        public FindingKind Kind;
        public Severity Severity;

        /// <summary>The phase concerned, or -1.</summary>
        public int Phase;

        /// <summary>The movement concerned, or -1.</summary>
        public int Movement;

        /// <summary>The second movement of a pair (Crossing, SplitLane), the earlier phase of a DuplicatePhase, or -1.</summary>
        public int Other;

        /// <summary>For NoGreen: the phases the movement fits into, as a bit mask (PlanEditing.FitsIn).</summary>
        public uint FitsIn;

        /// <summary>For HeavyYield: vehicles per hour the turn gives way to. For LongCycle: the cycle in seconds.</summary>
        public float Value;
    }

    /// <summary>
    /// Checks a phase plan before it runs: the checks of the planner's check
    /// bar. A plan with an error must not run; the planner keeps it from
    /// being applied, and the junction set-up runs the same checks on every
    /// plan it loads.
    /// </summary>
    public static class PlanCheck
    {
        /// <summary>
        /// Vehicles per hour that a turn giving way meets in its phase above
        /// which it gets few gaps. With about 600 oncoming vehicles an hour a
        /// permitted turn across them can take only a few hundred an hour,
        /// in rush-hour platoons close to none.
        /// </summary>
        public const float HeavyYieldVolume = 600f;

        /// <summary>A turn carrying less than this per hour is not worth a warning.</summary>
        public const float MinTurnVolume = 60f;

        /// <summary>Cycle in seconds above which waiting at red gets long.</summary>
        public const float LongCycleSeconds = 120f;

        /// <summary>Runs every check.</summary>
        /// <param name="volumes">Vehicles per hour per movement, or null where not measured: skips HeavyYield.</param>
        /// <param name="cycleSeconds">The plan's cycle, or 0 where it has none: skips LongCycle.</param>
        public static List<Finding> Run(JunctionModel model, IList<ulong> phases, float[] volumes = null, float cycleSeconds = 0f)
        {
            var findings = new List<Finding>();
            int n = model.Movements.Count;

            if (phases.Count > PhasePlanner.MaxPhases)
                findings.Add(new Finding { Kind = FindingKind.TooManyPhases, Severity = Severity.Error, Phase = -1, Movement = -1, Other = -1, Value = phases.Count });

            ulong covered = 0UL;
            foreach (ulong p in phases)
                covered |= p;
            for (int m = 0; m < n; m++)
            {
                if ((covered & (1UL << m)) == 0UL)
                {
                    findings.Add(new Finding
                    {
                        Kind = FindingKind.NoGreen, Severity = Severity.Error, Phase = -1, Movement = m, Other = -1,
                        FitsIn = PlanEditing.FitsIn(model, phases, m),
                    });
                }
            }

            for (int p = 0; p < phases.Count; p++)
            {
                for (int a = 0; a < n; a++)
                {
                    for (int b = a + 1; b < n; b++)
                    {
                        if ((phases[p] & (1UL << a)) != 0UL && (phases[p] & (1UL << b)) != 0UL && !model.Conflicts.CanShare(a, b))
                            findings.Add(new Finding { Kind = FindingKind.Crossing, Severity = Severity.Error, Phase = p, Movement = a, Other = b });
                    }
                }
            }

            for (int a = 0; a < n; a++)
            {
                ulong partners = model.SharesLaneWith(a);
                for (int b = a + 1; b < n; b++)
                {
                    if ((partners & (1UL << b)) != 0UL && GreenIn(phases, a) != GreenIn(phases, b))
                        findings.Add(new Finding { Kind = FindingKind.SplitLane, Severity = Severity.Error, Phase = -1, Movement = a, Other = b });
                }
            }

            if (volumes != null && volumes.Length == n)
                HeavyYields(model, phases, volumes, findings);

            if (cycleSeconds > LongCycleSeconds)
                findings.Add(new Finding { Kind = FindingKind.LongCycle, Severity = Severity.Warning, Phase = -1, Movement = -1, Other = -1, Value = cycleSeconds });

            for (int p = 0; p < phases.Count; p++)
            {
                if (phases[p] == 0UL)
                {
                    findings.Add(new Finding { Kind = FindingKind.EmptyPhase, Severity = Severity.Info, Phase = p, Movement = -1, Other = -1 });
                    continue;
                }
                for (int q = 0; q < p; q++)
                {
                    if (phases[q] == phases[p])
                    {
                        findings.Add(new Finding { Kind = FindingKind.DuplicatePhase, Severity = Severity.Info, Phase = p, Movement = -1, Other = q });
                        break;
                    }
                }
            }
            return findings;
        }

        /// <summary>Whether any finding keeps the plan from running.</summary>
        public static bool HasErrors(List<Finding> findings)
        {
            foreach (Finding f in findings)
            {
                if (f.Severity == Severity.Error)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A turn with traffic of its own that, in one of its phases, gives
        /// way to more than <see cref="HeavyYieldVolume"/>. One finding per
        /// turn, for the phase where it meets the least, since that is the
        /// phase that has to carry it: a turn that also has a phase of its
        /// own gets through there.
        /// </summary>
        private static void HeavyYields(JunctionModel model, IList<ulong> phases, float[] volumes, List<Finding> findings)
        {
            int n = model.Movements.Count;
            for (int m = 0; m < n; m++)
            {
                if (model.Movements[m].IsPedestrian || volumes[m] < MinTurnVolume)
                    continue;
                float least = float.MaxValue;
                int where = -1;
                for (int p = 0; p < phases.Count; p++)
                {
                    if ((phases[p] & (1UL << m)) == 0UL)
                        continue;
                    float against = 0f;
                    for (int o = 0; o < n; o++)
                    {
                        if ((phases[p] & (1UL << o)) != 0UL && model.Conflicts.Get(m, o) == Relation.Yields && !model.Movements[o].IsPedestrian)
                            against += volumes[o];
                    }
                    if (against < least)
                    {
                        least = against;
                        where = p;
                    }
                }
                if (where >= 0 && least > HeavyYieldVolume)
                    findings.Add(new Finding { Kind = FindingKind.HeavyYield, Severity = Severity.Warning, Phase = where, Movement = m, Other = -1, Value = least });
            }
        }

        /// <summary>The phases giving <paramref name="movement"/> green, as a bit mask.</summary>
        private static uint GreenIn(IList<ulong> phases, int movement)
        {
            uint result = 0U;
            for (int p = 0; p < phases.Count && p < 32; p++)
            {
                if ((phases[p] & (1UL << movement)) != 0UL)
                    result |= 1U << p;
            }
            return result;
        }
    }
}
