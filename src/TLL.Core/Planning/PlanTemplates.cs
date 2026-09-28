using System.Collections.Generic;
using System.Linq;

namespace TLL.Core.Planning
{
    /// <summary>The ready-made phase plans the planner offers. Numbers are shared with the panel.</summary>
    public enum TemplateKind : byte
    {
        /// <summary>Oncoming traffic runs together, turns across it give way. Fewest phases.</summary>
        TurnsGiveWay = 0,

        /// <summary>Turns across oncoming traffic get a phase of their own, before the straight traffic of their road.</summary>
        ProtectedFirst = 1,

        /// <summary>As <see cref="ProtectedFirst"/>, with the turn phase after the straight traffic.</summary>
        ProtectedLast = 2,

        /// <summary>Only the main road's turns across oncoming traffic get a phase of their own; the side road's give way.</summary>
        ProtectedMainRoad = 3,

        /// <summary>Each road runs alone.</summary>
        EachRoadAlone = 4,
    }

    /// <summary>One template as built for a junction.</summary>
    public sealed class TemplatePlan
    {
        public TemplateKind Kind;

        /// <summary>The phases, as masks over the junction's movements.</summary>
        public readonly List<ulong> Phases = new List<ulong>();
    }

    /// <summary>
    /// Builds the planner's templates for one junction. A template is written
    /// in roles, not in movement numbers: main road, side road, turn across
    /// the oncoming traffic. So each fits any junction it makes sense for,
    /// and is drawn on the player's own junction.
    /// </summary>
    public static class PlanTemplates
    {
        /// <summary>
        /// Every template that makes sense at the junction, in the order the
        /// planner shows them. One that comes out the same as an earlier one
        /// is left out: at a junction without turns across oncoming traffic,
        /// protecting them changes nothing.
        /// </summary>
        /// <param name="mainApproach">An approach of the main road, or -1 if the junction has none.</param>
        /// <param name="weights">Per movement, the traffic it carries, or null; see PhasePlanner.Build.</param>
        /// <param name="scramble">Pedestrians get a phase of their own in every template.</param>
        public static List<TemplatePlan> All(JunctionModel model, int mainApproach, float[] weights, bool scramble)
        {
            var result = new List<TemplatePlan>();
            foreach (TemplateKind kind in new[] { TemplateKind.TurnsGiveWay, TemplateKind.ProtectedFirst, TemplateKind.ProtectedLast, TemplateKind.ProtectedMainRoad, TemplateKind.EachRoadAlone })
            {
                TemplatePlan plan = Build(model, kind, mainApproach, weights, scramble);
                // Compared in order: turns last differs from turns first
                // only in the order of the same phases.
                if (plan == null || plan.Phases.Count == 0 || result.Exists(other => other.Phases.SequenceEqual(plan.Phases)))
                    continue;
                result.Add(plan);
            }
            return result;
        }

        /// <summary>One template for the junction, or null where it does not apply.</summary>
        public static TemplatePlan Build(JunctionModel model, TemplateKind kind, int mainApproach, float[] weights, bool scramble)
        {
            PhasePlan plan;
            switch (kind)
            {
                case TemplateKind.TurnsGiveWay:
                    plan = PhasePlanner.Build(model, PlanStrategy.Permissive, weights, scramble);
                    break;
                case TemplateKind.ProtectedFirst:
                case TemplateKind.ProtectedLast:
                    plan = PhasePlanner.Build(model, PlanStrategy.ProtectedTurns, weights, scramble);
                    break;
                case TemplateKind.ProtectedMainRoad:
                    if (mainApproach < 0 || mainApproach >= model.ApproachCount)
                        return null;
                    plan = PhasePlanner.Build(ProtectOnMainRoad(model, mainApproach), PlanStrategy.Permissive, weights, scramble);
                    break;
                case TemplateKind.EachRoadAlone:
                    if (model.ApproachCount < 3)
                        return null;
                    plan = PhasePlanner.Build(model, PlanStrategy.Split, weights, scramble);
                    break;
                default:
                    return null;
            }
            var result = new TemplatePlan { Kind = kind };
            foreach (Phase p in plan.Phases)
                result.Phases.Add(p.Green);
            if (kind == TemplateKind.ProtectedFirst || kind == TemplateKind.ProtectedMainRoad)
                OrderTurns(model, result.Phases, false);
            else if (kind == TemplateKind.ProtectedLast && !OrderTurns(model, result.Phases, true))
                return null;
            return result;
        }

        /// <summary>
        /// The junction with the main road's turns across oncoming traffic
        /// kept apart from everything they would give way to: planned
        /// permissively, it gives them a phase of their own and lets the side
        /// road's turns give way. Crosswalks are left as they are, as the
        /// protected turns strategy does.
        /// </summary>
        private static JunctionModel ProtectOnMainRoad(JunctionModel model, int mainApproach)
        {
            int opposite = model.OppositeOf != null && mainApproach < model.OppositeOf.Length ? model.OppositeOf[mainApproach] : -1;
            var result = new JunctionModel
            {
                ApproachCount = model.ApproachCount,
                OppositeOf = model.OppositeOf,
                LeftHandTraffic = model.LeftHandTraffic,
                Conflicts = model.Conflicts.Clone(),
                SharedLane = model.SharedLane == null ? null : (ulong[])model.SharedLane.Clone(),
            };
            result.Movements.AddRange(model.Movements);
            int n = model.Movements.Count;
            for (int a = 0; a < n; a++)
            {
                Movement m = model.Movements[a];
                if (!IsLongTurn(m, model.LeftHandTraffic) || (m.Source != mainApproach && m.Source != opposite))
                    continue;
                for (int b = 0; b < n; b++)
                {
                    if (b != a && !model.Movements[b].IsPedestrian && model.Conflicts.Get(a, b) == Relation.Yields)
                        result.Conflicts.Set(a, b, Relation.Hard);
                }
            }
            return result;
        }

        /// <summary>
        /// Puts each phase of protected turns right before (or with
        /// <paramref name="last"/>, right after) the phase of straight
        /// traffic on its road, the order planners call leading and lagging
        /// turns: turns of the main road, main road, turns of the side road,
        /// side road. Phases of other kinds keep their place.
        /// </summary>
        /// <returns>Whether the order changed.</returns>
        private static bool OrderTurns(JunctionModel model, List<ulong> phases, bool last)
        {
            var straightAxes = new HashSet<int>();
            foreach (ulong p in phases)
            {
                if (Has(model, p, IsStraight))
                    straightAxes.Add(Axis(model, p));
            }
            var turnsByAxis = new Dictionary<int, List<ulong>>();
            foreach (ulong p in phases)
            {
                int axis = Axis(model, p);
                if (Has(model, p, IsStraight) || !Has(model, p, m => IsLongTurn(m, model.LeftHandTraffic)) || !straightAxes.Contains(axis))
                    continue;
                if (!turnsByAxis.TryGetValue(axis, out List<ulong> list))
                    turnsByAxis[axis] = list = new List<ulong>();
                list.Add(p);
            }
            var ordered = new List<ulong>();
            var placed = new HashSet<int>();
            foreach (ulong p in phases)
            {
                int axis = Axis(model, p);
                bool turn = turnsByAxis.TryGetValue(axis, out List<ulong> turns) && turns.Contains(p);
                if (turn)
                    continue;
                bool anchor = turns != null && Has(model, p, IsStraight) && placed.Add(axis);
                if (anchor && !last)
                    ordered.AddRange(turns);
                ordered.Add(p);
                if (anchor && last)
                    ordered.AddRange(turns);
            }
            bool changed = !ordered.SequenceEqual(phases);
            phases.Clear();
            phases.AddRange(ordered);
            return changed;
        }

        private static bool Has(JunctionModel model, ulong phase, System.Predicate<Movement> test)
        {
            for (int m = 0; m < model.Movements.Count; m++)
            {
                if ((phase & (1UL << m)) != 0UL && test(model.Movements[m]))
                    return true;
            }
            return false;
        }

        private static bool IsStraight(Movement m)
        {
            return m.Kind == MovementKind.Straight || m.Kind == MovementKind.Track;
        }

        /// <summary>
        /// The road a phase serves, as the lower of an approach and the one
        /// opposite: that of its straight traffic, else of its turns across
        /// oncoming traffic, else of any vehicle. Turns that merely run free
        /// alongside, such as a right turn into its own lane, do not count:
        /// they turn up in the phases of every road. -1 for crosswalks alone.
        /// </summary>
        private static int Axis(JunctionModel model, ulong phase)
        {
            int axis = AxisOf(model, phase, IsStraight);
            if (axis < 0)
                axis = AxisOf(model, phase, m => IsLongTurn(m, model.LeftHandTraffic));
            if (axis < 0)
                axis = AxisOf(model, phase, m => !m.IsPedestrian);
            return axis;
        }

        private static int AxisOf(JunctionModel model, ulong phase, System.Predicate<Movement> test)
        {
            int axis = int.MaxValue;
            for (int m = 0; m < model.Movements.Count; m++)
            {
                if ((phase & (1UL << m)) == 0UL || !test(model.Movements[m]))
                    continue;
                int a = model.Movements[m].Source;
                int opposite = model.OppositeOf != null && a < model.OppositeOf.Length ? model.OppositeOf[a] : -1;
                if (opposite >= 0 && opposite < a)
                    a = opposite;
                if (a < axis)
                    axis = a;
            }
            return axis == int.MaxValue ? -1 : axis;
        }

        private static bool IsLongTurn(Movement m, bool leftHandTraffic)
        {
            return m.Kind == MovementKind.UTurn || m.Kind == (leftHandTraffic ? MovementKind.Right : MovementKind.Left);
        }
    }
}
