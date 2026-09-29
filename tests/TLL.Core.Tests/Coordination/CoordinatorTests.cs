using System;
using System.Collections.Generic;
using System.Linq;
using TLL.Core.Control;
using TLL.Core.Coordination;
using TLL.Core.Optimization;
using Xunit;

namespace TLL.Core.Tests.Coordination
{
    public class CoordinatorTests
    {
        private const int ThroughA = 0;
        private const int ThroughB = 1;
        private const int CrossStreet = 2;

        /// <summary>
        /// A corridor of simple junctions: phase 0 carries the corridor in
        /// both directions, phase 1 the cross street.
        /// </summary>
        private static (CorridorPath path, List<CorridorMember> members) Corridor(float[] spacings, int[] desiredCycles, int intergreen)
        {
            var path = new CorridorPath();
            var members = new List<CorridorMember>();
            for (int i = 0; i <= spacings.Length; i++)
            {
                path.Junctions.Add(i);
                path.ApproachBack.Add(i == 0 ? -1 : 2);
                path.ApproachAhead.Add(i == spacings.Length ? -1 : 0);
                if (i < spacings.Length)
                    path.Links.Add(new SignalLink { A = i, ApproachA = 0, B = i + 1, ApproachB = 2, Length = spacings[i], Speed = 13.9f });
                members.Add(new CorridorMember
                {
                    Phases = new[]
                    {
                        new PhaseData { MinGreen = 20, MaxGreen = 200, Green = 80 },
                        new PhaseData { MinGreen = 20, MaxGreen = 200, Green = 40 },
                    },
                    PhaseMovements = new[] { (1UL << ThroughA) | (1UL << ThroughB), 1UL << CrossStreet },
                    Intergreen = intergreen,
                    MovementA = ThroughA,
                    MovementB = ThroughB,
                    Ratios = new[] { 0.5f, 0.2f + 0.05f * i },
                    DesiredCycle = desiredCycles[i],
                });
            }
            return (path, members);
        }

        [Fact]
        public void EveryMemberRunsTheCommonCycle()
        {
            var (path, members) = Corridor(new[] { 250f, 400f, 180f }, new[] { 200, 320, 260, 240 }, 16);
            CoordinationPlan plan = Coordinator.Plan(path, members, OptimizerLimits.Default);

            Assert.Equal(320, plan.Cycle);
            for (int i = 0; i < members.Count; i++)
            {
                Assert.Equal(plan.Cycle, plan.Greens[i].Sum(g => g) + members[i].Phases.Length * members[i].Intergreen);
                Assert.Equal(new[] { true, false }, plan.Coordinated[i]);
            }
        }

        [Theory]
        [InlineData(250f, 400f, 180f)]
        [InlineData(500f, 500f, 500f)]
        [InlineData(120f, 700f, 330f)]
        public void ControllersRunningThePlanLetTheBandThrough(float s1, float s2, float s3)
        {
            var (path, members) = Corridor(new[] { s1, s2, s3 }, new[] { 300, 300, 300, 300 }, 16);
            CoordinationPlan plan = Coordinator.Plan(path, members, OptimizerLimits.Default);
            Assert.True(plan.BandwidthA > 0, "no band in direction A");

            // Run one controller per junction with the plan, from a cold start.
            int n = members.Count;
            int steps = plan.Cycle * 8;
            var greenA = new bool[n][];
            for (int i = 0; i < n; i++)
            {
                var phases = members[i].Phases.Select((p, k) => new PhaseData
                {
                    MinGreen = p.MinGreen,
                    MaxGreen = p.MaxGreen,
                    Green = plan.Greens[i][k],
                    Flags = plan.Coordinated[i][k] ? PhaseFlags.Coordinated : PhaseFlags.None,
                    Demand = 1f,
                }).ToArray();
                var config = ControllerConfig.Default(ControlMode.Coordinated);
                config.Yellow = 10;
                config.AllRed = 5;
                config.Prepare = 1;
                config.Offset = plan.Offsets[i];
                var state = new ControllerState();
                var access = new PhaseArray(phases);
                greenA[i] = new bool[steps];
                for (int step = 0; step < steps; step++)
                {
                    SignalController.Step(ref state, in config, ref access, step);
                    greenA[i][step] = HasGreen(state, members[i], ThroughA);
                }
            }

            // Count departures from the first junction, within one settled
            // cycle, that meet green everywhere at the corridor speed.
            var arrival = new int[n];
            for (int i = 1; i < n; i++)
                arrival[i] = arrival[i - 1] + SimTime.ToSteps(path.Links[i - 1].Length / (path.Links[i - 1].Speed * Coordinator.SpeedFactor));
            int from = plan.Cycle * 4;
            int passing = 0;
            for (int g = from; g < from + plan.Cycle; g++)
            {
                bool allGreen = true;
                for (int i = 0; i < n; i++)
                    allGreen &= greenA[i][g + arrival[i]];
                passing += allGreen ? 1 : 0;
            }
            Assert.True(Math.Abs(passing - plan.BandwidthA) <= 1, $"{passing} departures pass, the plan promised {plan.BandwidthA}");
        }

        private const int SideAhead = 2;
        private const int SideBack = 3;

        /// <summary>
        /// A main road of T-junctions whose side roads alternate between its
        /// sides, <paramref name="spacing"/> metres apart. Phase 0 carries the
        /// main road both ways, phase 1 the side road, whose traffic turns
        /// both ways onto the main road: one turn into the road ahead, the
        /// other into the road back.
        /// </summary>
        private static (CorridorPath path, List<CorridorMember> members) Tees(int count, float spacing, bool tight)
        {
            var path = new CorridorPath();
            var members = new List<CorridorMember>();
            for (int i = 0; i < count; i++)
            {
                path.Junctions.Add(i);
                path.ApproachBack.Add(i == 0 ? -1 : 1);
                path.ApproachAhead.Add(i == count - 1 ? -1 : 0);
                if (i < count - 1)
                    path.Links.Add(new SignalLink { A = i, ApproachA = 0, B = i + 1, ApproachB = 1, Length = spacing, Speed = 13.9f, LanesToA = 2, LanesToB = 2, Tight = tight });
                members.Add(new CorridorMember
                {
                    Phases = new[]
                    {
                        new PhaseData { MinGreen = 20, MaxGreen = 400, Green = 100 },
                        new PhaseData { MinGreen = 20, MaxGreen = 400, Green = 40 },
                    },
                    PhaseMovements = new[] { (1UL << ThroughA) | (1UL << ThroughB), (1UL << SideAhead) | (1UL << SideBack) },
                    Intergreen = 16,
                    MovementA = ThroughA,
                    MovementB = ThroughB,
                    Ratios = new[] { 0.6f, 0.2f },
                    DesiredCycle = SimTime.ToSteps(60f),
                    FeedsAhead = i < count - 1 ? new[] { ThroughA, SideAhead } : null,
                    FeedsBack = i > 0 ? new[] { ThroughB, SideBack } : null,
                    Volumes = new[] { 600f, 600f, 150f, 150f },
                });
            }
            return (path, members);
        }

        /// <summary>
        /// Runs one coordinated controller per junction with the plan, every
        /// phase always asked for, and records per junction and step which
        /// movements have green.
        /// </summary>
        private static bool[][][] Run(CoordinationPlan plan, List<CorridorMember> members, int steps)
        {
            var green = new bool[members.Count][][];
            for (int i = 0; i < members.Count; i++)
            {
                var phases = members[i].Phases.Select((p, k) => new PhaseData
                {
                    MinGreen = p.MinGreen,
                    MaxGreen = p.MaxGreen,
                    Green = plan.Greens[i][k],
                    Flags = plan.Coordinated[i][k] ? PhaseFlags.Coordinated : PhaseFlags.None,
                    Demand = 1f,
                }).ToArray();
                var config = ControllerConfig.Default(ControlMode.Coordinated);
                config.Yellow = 10;
                config.AllRed = 5;
                config.Prepare = 1;
                config.Offset = plan.Offsets[i];
                var state = new ControllerState();
                var access = new PhaseArray(phases);
                green[i] = new bool[4][];
                for (int m = 0; m < 4; m++)
                    green[i][m] = new bool[steps];
                for (int step = 0; step < steps; step++)
                {
                    SignalController.Step(ref state, in config, ref access, step);
                    for (int m = 0; m < 4; m++)
                        green[i][m][step] = HasGreen(state, members[i], m);
                }
            }
            return green;
        }

        /// <summary>
        /// Of the steps a side road has green, over one settled cycle, the
        /// share whose traffic reaches the next junction on the main road
        /// with its through green on and running for the lead.
        /// </summary>
        private static float SideIntoGreen(CoordinationPlan plan, List<CorridorMember> members, float spacing)
        {
            int travel = SimTime.ToSteps(spacing / (13.9f * Coordinator.SpeedFactor));
            int lead = Clusters.Lead(spacing);
            bool[][][] green = Run(plan, members, plan.Cycle * 8);
            int from = plan.Cycle * 5;
            int fed = 0;
            int intoGreen = 0;
            for (int i = 0; i < members.Count; i++)
            {
                for (int g = from; g < from + plan.Cycle; g++)
                {
                    // The turn into the road ahead meets the through green
                    // of direction A at the next junction; the turn into the
                    // road back that of direction B at the previous one.
                    if (i + 1 < members.Count && green[i][SideAhead][g])
                    {
                        fed++;
                        intoGreen += Enumerable.Range(g + travel - lead, lead + 1).All(t => green[i + 1][ThroughA][t]) ? 1 : 0;
                    }
                    if (i > 0 && green[i][SideBack][g])
                    {
                        fed++;
                        intoGreen += Enumerable.Range(g + travel - lead, lead + 1).All(t => green[i - 1][ThroughB][t]) ? 1 : 0;
                    }
                }
            }
            return fed > 0 ? intoGreen / (float)fed : 0f;
        }

        [Theory]
        [InlineData(60f)]
        [InlineData(90f)]
        public void InAClusterTheSideRoadsTurnIntoAGreenMainRoad(float spacing)
        {
            var (path, members) = Tees(4, spacing, tight: true);
            CoordinationPlan plan = Coordinator.Plan(path, members, OptimizerLimits.Default);

            Assert.True(plan.Cluster);
            float share = SideIntoGreen(plan, members, spacing);
            Assert.True(share >= 0.9f, $"{share:P0} of the side roads' green meets green ahead; cycle {plan.Cycle}, offsets {string.Join(",", plan.Offsets)}, greens {string.Join(" ", plan.Greens.Select(g => string.Join("/", g)))}, fed into red {plan.FedIntoRed}, band {plan.BandwidthA}/{plan.BandwidthB}, travel {SimTime.ToSteps(spacing / (13.9f * Coordinator.SpeedFactor))}, lead {Clusters.Lead(spacing)}");
        }

        [Fact]
        public void AGreenWaveAloneSendsTheSideRoadsIntoTheRed()
        {
            // The same road as a plain green wave: its offsets widen the band
            // and leave the side roads to chance. This is what the feeds of
            // a cluster change.
            var (path, members) = Tees(4, 60f, tight: false);
            CoordinationPlan plan = Coordinator.Plan(path, members, OptimizerLimits.Default);

            Assert.False(plan.Cluster);
            float share = SideIntoGreen(plan, members, 60f);
            Assert.True(share < 0.5f, $"{share:P0} of the side roads' green meets green ahead");
        }

        /// <summary>
        /// Whether the controller shows green to a movement. During a
        /// transition a movement keeps green if both the ending and the next
        /// phase contain it, as the game's signal logic does.
        /// </summary>
        private static bool HasGreen(ControllerState s, CorridorMember m, int movement)
        {
            ulong bit = 1UL << movement;
            bool inCurrent = (m.PhaseMovements[s.Phase] & bit) != 0;
            if (s.Stage == Stage.Green)
                return inCurrent;
            bool inNext = (m.PhaseMovements[s.Next] & bit) != 0;
            return inCurrent && inNext;
        }
    }
}
