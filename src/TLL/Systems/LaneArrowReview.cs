using System;
using System.Collections.Generic;
using System.Text;
using Game.Net;
using Game.Pathfind;
using TLL.Components;
using TLL.Core.Advisor;
using TLL.Core.Planning;
using Unity.Entities;
using Unity.Mathematics;

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

        /// <summary>A pair of lane ends connected through the junction: incoming, outgoing.</summary>
        private struct Link : IEquatable<Link>
        {
            public int From;
            public int To;

            public bool Equals(Link other) => From == other.From && To == other.To;

            public override int GetHashCode() => From * 397 ^ To;
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
            HashSet<Link> current = Current(em, node, ends);

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

        /// <summary>The connections the junction's lanes make now, as pairs of lane ends.</summary>
        private static HashSet<Link> Current(EntityManager em, Entity node, List<LaneEnd> ends)
        {
            var links = new HashSet<Link>();
            DynamicBuffer<SubLane> lanes = em.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!em.HasComponent<CarLane>(lane) || em.HasComponent<MasterLane>(lane)
                    || (em.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Forbidden) != 0)
                    continue;
                Lane path = em.GetComponentData<Lane>(lane);
                int from = LaneEnds.Find(ends, path.m_StartNode, incoming: true);
                int to = LaneEnds.Find(ends, path.m_EndNode, incoming: false);
                if (from >= 0 && to >= 0)
                    links.Add(new Link { From = from, To = to });
            }
            return links;
        }

        /// <summary>
        /// The autopilot's rules for one approach, or null to keep the ones it
        /// has (<paramref name="own"/>).
        /// </summary>
        private static List<LaneConnectionRule> Review(List<Entity> edges, int approach, List<LaneEnd> ends, HashSet<Link> current,
            List<LaneConnectionRule> own, float[,] flow, SignalModel signals, bool leftHandTraffic, StringBuilder text)
        {
            bool signalled = signals != null;
            Entity edge = edges[approach];
            List<int> lanes = Sorted(ends, i => ends[i].Incoming && ends[i].Edge == edge, leftHandTraffic);
            if (lanes.Count < 2 || lanes.Count > LaneArrows.MaxLanes)
                return null;
            float3 heading = Mean(ends, lanes);

            // The roads this approach leads into now, U-turns aside, from the
            // kerb side outwards.
            var targets = new List<int>();
            foreach (Link link in current)
            {
                int target = edges.IndexOf(ends[link.To].Edge);
                if (lanes.Contains(link.From) && target != approach && !targets.Contains(target))
                    targets.Add(target);
            }
            if (targets.Count < 2 || targets.Count > LaneArrows.MaxMovements)
                return null;
            var angles = new Dictionary<int, float>();
            var exits = new Dictionary<int, List<int>>();
            foreach (int t in targets)
            {
                List<int> outgoing = Sorted(ends, i => !ends[i].Incoming && ends[i].Edge == edges[t], leftHandTraffic);
                exits[t] = outgoing;
                angles[t] = Angle(heading, Mean(ends, outgoing));
            }
            // A kerb-side turn has the lowest angle in right-hand traffic
            // (to the right), the highest in left-hand traffic.
            targets.Sort((a, b) => leftHandTraffic ? angles[b].CompareTo(angles[a]) : angles[a].CompareTo(angles[b]));

            int n = targets.Count;
            var volumes = new float[n];
            var far = new bool[n];
            var straight = new bool[n];
            var receiving = new int[n];
            for (int m = 0; m < n; m++)
            {
                volumes[m] = flow[approach, targets[m]];
                straight[m] = math.abs(angles[targets[m]]) < 30f;
                far[m] = !straight[m] && (leftHandTraffic ? angles[targets[m]] < 0f : angles[targets[m]] > 0f);
                receiving[m] = exits[targets[m]].Count;
            }
            var uses = new LaneUse[lanes.Count];
            for (int j = 0; j < lanes.Count; j++)
            {
                int first = int.MaxValue;
                int last = -1;
                for (int m = 0; m < n; m++)
                {
                    if (!Serves(current, lanes[j], exits[targets[m]]))
                        continue;
                    first = Math.Min(first, m);
                    last = Math.Max(last, m);
                }
                // A lane that only U-turns, or none at all: this approach is
                // not one the autopilot understands.
                if (last < 0)
                    return null;
                uses[j] = new LaneUse(first, last);
            }

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
                chosen = LaneArrows.ChooseBy(uses, volumes, far, straight, receiving, true, Cost, TurnAdvisor.MinSaving * people,
                    out before, out after, allowed);
                before /= Math.Max(1f, people);
                after /= Math.Max(1f, people);
                unit = "s mean delay";
            }
            else
            {
                chosen = LaneArrows.Choose(uses, volumes, far, straight, receiving, false, out before, out after, allowed);
                unit = "/h on the busiest lane";
            }
            if (ReferenceEquals(chosen, uses) || Same(chosen, uses))
                return null;

            // The connections the lanes are to have: a lane that keeps a
            // movement keeps its connections for it, one that gains it gets
            // one, to the lane of the target on the same side.
            var wanted = new HashSet<Link>();
            foreach (Link link in current)
            {
                if (!lanes.Contains(link.From))
                    continue;
                int target = edges.IndexOf(ends[link.To].Edge);
                int m = targets.IndexOf(target);
                if (m < 0 || chosen[lanes.IndexOf(link.From)].Serves(m))
                    wanted.Add(link);
            }
            for (int m = 0; m < n; m++)
            {
                List<int> into = exits[targets[m]];
                var serving = new List<int>();
                for (int j = 0; j < lanes.Count; j++)
                {
                    if (chosen[j].Serves(m))
                        serving.Add(j);
                }
                for (int k = 0; k < serving.Count; k++)
                {
                    int lane = lanes[serving[k]];
                    if (Serves(current, lane, into))
                        continue;
                    wanted.Add(new Link { From = lane, To = into[FreeExit(wanted, into, ExitFor(k, serving.Count, into.Count, far[m]))] });
                }
            }

            // The rules say how the lanes differ from the game's own. The
            // game's own are what the junction has now, without what the
            // autopilot's rules for this approach did to it.
            var game = new HashSet<Link>(current);
            foreach (LaneConnectionRule rule in own)
            {
                int from = LaneEnds.Find(ends, rule.FromEdge, rule.FromLane, incoming: true);
                int to = LaneEnds.Find(ends, rule.ToEdge, rule.ToLane, incoming: false);
                if (from < 0 || to < 0)
                    continue;
                var link = new Link { From = from, To = to };
                if (rule.Change == LaneConnectionChange.Added)
                    game.Remove(link);
                else
                    game.Add(link);
            }
            var result = new List<LaneConnectionRule>();
            foreach (Link link in game)
            {
                if (lanes.Contains(link.From) && !wanted.Contains(link))
                    result.Add(Rule(ends, link, LaneConnectionChange.Removed));
            }
            foreach (Link link in wanted)
            {
                if (!game.Contains(link))
                    result.Add(Rule(ends, link, LaneConnectionChange.Added));
            }
            // New lanes beyond the rules the approach had already.
            if (!kAddConnections && result.Exists(r => r.Change == LaneConnectionChange.Added && !own.Exists(o => o.Change == r.Change
                && o.FromEdge == r.FromEdge && o.FromLane == r.FromLane && o.ToEdge == r.ToEdge && o.ToLane == r.ToLane)))
                return null;
            text.Append($"approach {approach}: lanes {string.Join(" ", Array.ConvertAll(uses, u => u.ToString()))} -> {string.Join(" ", Array.ConvertAll(chosen, u => u.ToString()))}"
                + $" for roads {string.Join(",", targets)}, {before:0.#} -> {after:0.#} {unit}; ");
            return result;
        }

        private static LaneConnectionRule Rule(List<LaneEnd> ends, Link link, LaneConnectionChange change)
        {
            return new LaneConnectionRule
            {
                FromEdge = ends[link.From].Edge,
                FromLane = ends[link.From].Index,
                ToEdge = ends[link.To].Edge,
                ToLane = ends[link.To].Index,
                Change = change,
                Auto = true,
            };
        }

        /// <summary>
        /// Which lane of the road it leads into the k-th of <paramref name="count"/>
        /// lanes of a movement takes, both counted from the kerb. A turn
        /// across the oncoming traffic keeps to the far side, the others to
        /// the kerb side, so the paths of one movement stay side by side.
        /// </summary>
        private static int ExitFor(int k, int count, int exits, bool far)
        {
            int index = far ? exits - count + k : k;
            return math.clamp(index, 0, exits - 1);
        }

        /// <summary>
        /// The exit lane nearest to <paramref name="preferred"/> that no
        /// wanted connection leads into yet, or the preferred one if all are
        /// taken: two lanes into one would have to merge inside the junction.
        /// </summary>
        private static int FreeExit(HashSet<Link> wanted, List<int> exits, int preferred)
        {
            for (int distance = 0; distance < exits.Count; distance++)
            {
                foreach (int index in new[] { preferred - distance, preferred + distance })
                {
                    if (index < 0 || index >= exits.Count)
                        continue;
                    bool taken = false;
                    foreach (Link link in wanted)
                        taken |= link.To == exits[index];
                    if (!taken)
                        return index;
                }
            }
            return preferred;
        }

        private static bool Serves(HashSet<Link> links, int lane, List<int> exits)
        {
            foreach (int exit in exits)
            {
                if (links.Contains(new Link { From = lane, To = exit }))
                    return true;
            }
            return false;
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

        /// <summary>
        /// The lane ends that match, from the kerb side outwards: ordered
        /// across their direction of travel, the kerb on the right in
        /// right-hand traffic.
        /// </summary>
        private static List<int> Sorted(List<LaneEnd> ends, Predicate<int> match, bool leftHandTraffic)
        {
            var result = new List<int>();
            for (int i = 0; i < ends.Count; i++)
            {
                if (match(i))
                    result.Add(i);
            }
            if (result.Count == 0)
                return result;
            float3 heading = Mean(ends, result);
            // To the right of the heading, seen from above (x east, z north).
            float2 right = new float2(heading.z, -heading.x);
            if (leftHandTraffic)
                right = -right;
            result.Sort((a, b) => math.dot(ends[b].Position.xz, right).CompareTo(math.dot(ends[a].Position.xz, right)));
            return result;
        }

        private static float3 Mean(List<LaneEnd> ends, List<int> which)
        {
            float3 sum = float3.zero;
            foreach (int i in which)
                sum += ends[i].Direction;
            return math.normalizesafe(sum);
        }

        /// <summary>Angle from one heading to another in degrees, positive to the left, seen from above.</summary>
        private static float Angle(float3 from, float3 to)
        {
            float2 a = math.normalizesafe(from.xz);
            float2 b = math.normalizesafe(to.xz);
            return math.degrees(math.atan2(a.x * b.y - a.y * b.x, math.dot(a, b)));
        }
    }
}
