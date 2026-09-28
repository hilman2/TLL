using System;
using System.Collections.Generic;
using System.Text;
using TLL.Components;
using TLL.Core.Advisor;
using TLL.Core.Planning;
using Unity.Entities;

namespace TLL.Systems
{
    /// <summary>
    /// The autopilot's review of a junction's lane arrows (LaneArrows): per
    /// approach, which lane goes where, from the traffic of its movements.
    /// The result is kept as rules (LaneConnectionRule with Auto set): the
    /// changes to the lanes the game builds by itself. Approaches with a rule
    /// of the player's stay as they are; so do U-turns, which the turn
    /// review looks after.
    /// </summary>
    internal static class LaneArrowReview
    {
        /// <summary>
        /// Whether the autopilot may give a lane a direction it does not
        /// have, which takes a junction lane TLL builds itself
        /// (LaneRuleSystem). Off until that is confirmed in the game: until
        /// then it only takes directions away, which only deletes lanes the
        /// game built.
        /// </summary>
        private const bool kAddConnections = false;

        /// <summary>
        /// For junctions with signals: the junction as the signals run it, and
        /// the traffic per movement, by which each choice of lanes is judged
        /// through the delay of its best phase layout.
        /// </summary>
        public sealed class SignalModel
        {
            public JunctionLayout Layout;
            public float[] Volumes;
            public DelayParameters Parameters;
        }

        /// <summary>
        /// The junction's lane rules with the autopilot's part renewed, or null
        /// to keep them.
        /// </summary>
        /// <param name="flow">Vehicles per hour from approach [s] into approach [t], as the junction's roads are listed.</param>
        /// <param name="signals">
        /// For a junction with signals, its model: lanes are then judged by
        /// the junction's delay, and a lane never mixes straight traffic with
        /// a turn across it. Null for a junction without, judged by the load
        /// of its busiest lane.
        /// </param>
        /// <param name="summary">What changed, for the log.</param>
        public static List<LaneConnectionRule> Decide(EntityManager em, Entity node, float[,] flow, SignalModel signals, bool leftHandTraffic, out string summary)
        {
            summary = null;
            List<Entity> edges = NetGeometry.ConnectedEdges(em, node);
            if (edges.Count < 3 || flow.GetLength(0) != edges.Count)
                return null;
            List<LaneEnd> ends = LaneEnds.Collect(em, node, edges);
            var rules = new List<LaneConnectionRule>();
            if (em.HasBuffer<LaneConnectionRule>(node))
            {
                DynamicBuffer<LaneConnectionRule> stored = em.GetBuffer<LaneConnectionRule>(node, true);
                for (int i = 0; i < stored.Length; i++)
                    rules.Add(stored[i]);
            }
            HashSet<ApproachLanes.Link> current = ApproachLanes.Current(em, node, ends);

            var result = new List<LaneConnectionRule>();
            foreach (LaneConnectionRule rule in rules)
            {
                if (!rule.Auto)
                    result.Add(rule);
            }
            var text = new StringBuilder();
            bool changed = false;
            for (int e = 0; e < edges.Count; e++)
            {
                List<LaneConnectionRule> own = rules.FindAll(r => r.Auto && r.FromEdge == edges[e]);
                if (rules.Exists(r => !r.Auto && r.FromEdge == edges[e]))
                    continue;
                List<LaneConnectionRule> renewed = Review(edges, e, ends, current, own, flow, signals, leftHandTraffic, text);
                if (renewed == null)
                {
                    result.AddRange(own);
                    continue;
                }
                result.AddRange(renewed);
                changed = true;
            }
            // Rules for roads that are gone are dropped by LaneRuleSystem.
            if (!changed)
                return null;
            summary = text.ToString().TrimEnd(';', ' ');
            return result;
        }

        /// <summary>
        /// The autopilot's rules for one approach, or null to keep the ones it
        /// has (<paramref name="own"/>).
        /// </summary>
        private static List<LaneConnectionRule> Review(List<Entity> edges, int approach, List<LaneEnd> ends, HashSet<ApproachLanes.Link> current,
            List<LaneConnectionRule> own, float[,] flow, SignalModel signals, bool leftHandTraffic, StringBuilder text)
        {
            bool signalled = signals != null;
            ApproachLanes lanes = ApproachLanes.ReadOne(edges, approach, ends, current, leftHandTraffic);
            // With one road to lead into, every lane already goes there.
            if (lanes == null || lanes.Targets.Count < 2)
                return null;
            List<int> targets = lanes.Targets;
            LaneUse[] uses = lanes.Uses;
            int n = targets.Count;
            var volumes = new float[n];
            for (int m = 0; m < n; m++)
                volumes[m] = flow[approach, targets[m]];

            Predicate<LaneUse[]> allowed = kAddConnections ? (Predicate<LaneUse[]>)null : lanesNow => LaneArrows.OnlyRemoves(lanesNow, uses);
            LaneUse[] chosen;
            float before;
            float after;
            string unit;
            if (signalled)
            {
                // The movements of the model the lanes carry, in the same order.
                JunctionModel model = signals.Layout.Model;
                var movements = new int[n];
                for (int m = 0; m < n; m++)
                {
                    movements[m] = -1;
                    for (int i = 0; i < model.Movements.Count; i++)
                    {
                        Movement mv = model.Movements[i];
                        if (mv.Source == approach && mv.Target == targets[m] && mv.Kind != MovementKind.Track && !mv.IsPedestrian)
                            movements[m] = i;
                    }
                    if (movements[m] < 0)
                        return null;
                }
                float people = 0f;
                float Cost(LaneUse[] lanesNow)
                {
                    PlanEstimate[] estimates = JunctionAdvisor.EvaluateAll(model.WithLanes(movements, lanesNow), signals.Volumes, signals.Parameters, signals.Volumes);
                    PlanEstimate best = estimates[JunctionAdvisor.Best(estimates)];
                    people = Math.Max(people, best.People);
                    return best.TotalDelay;
                }
                Cost(uses);
                chosen = LaneArrows.ChooseBy(uses, volumes, lanes.Far, lanes.Straight, lanes.Receiving, true, Cost, TurnAdvisor.MinSaving * people,
                    out before, out after, allowed);
                before /= Math.Max(1f, people);
                after /= Math.Max(1f, people);
                unit = "s mean delay";
            }
            else
            {
                chosen = LaneArrows.Choose(uses, volumes, lanes.Far, lanes.Straight, lanes.Receiving, false, out before, out after, allowed);
                unit = "/h on the busiest lane";
            }
            if (ReferenceEquals(chosen, uses) || Same(chosen, uses))
                return null;

            // The rules replace the autopilot's own for this approach, so they
            // are measured against the lanes the game builds without them.
            List<LaneConnectionRule> result = lanes.Rules(ends, current, chosen, own);
            for (int i = 0; i < result.Count; i++)
            {
                LaneConnectionRule rule = result[i];
                rule.Auto = true;
                result[i] = rule;
            }
            // New lanes beyond the rules the approach had already.
            if (!kAddConnections && result.Exists(r => r.Change == LaneConnectionChange.Added && !own.Exists(o => o.Change == r.Change
                && o.FromEdge == r.FromEdge && o.FromLane == r.FromLane && o.ToEdge == r.ToEdge && o.ToLane == r.ToLane)))
                return null;
            text.Append($"approach {approach}: lanes {string.Join(" ", Array.ConvertAll(uses, u => u.ToString()))} -> {string.Join(" ", Array.ConvertAll(chosen, u => u.ToString()))}"
                + $" for roads {string.Join(",", targets)}, {before:0.#} -> {after:0.#} {unit}; ");
            return result;
        }

        private static bool Same(LaneUse[] a, LaneUse[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }
    }
}
