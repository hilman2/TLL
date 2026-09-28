using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Rendering;
using Game.Tools;
using Game.UI;
using TLL.Components;
using TLL.Core;
using TLL.Core.Advisor;
using TLL.Core.Control;
using TLL.Core.Planning;
using TLL.Metrics;
using TLL.Systems;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.UI
{
    /// <summary>
    /// Connects the TLL panel (src/TLL.UI) with the simulation.
    ///
    /// Two values go to the panel: a city overview with the junctions that
    /// need attention, and the details of the selected junction. Both are
    /// collected from the ECS data at a limited rate, since reading the phase
    /// buffers waits for the running control job. The panel talks back
    /// through triggers, all in the binding group "tll".
    /// </summary>
    public partial class TllUISystem : UISystemBase
    {
        private const string kGroup = "tll";

        /// <summary>Junctions listed as needing attention.</summary>
        private const int kProblemCount = 12;

        private static readonly TimeSpan kSummaryInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan kDetailInterval = TimeSpan.FromMilliseconds(250);

        private EntityQuery m_ManagedQuery;
        private NameSystem m_NameSystem;
        private CameraUpdateSystem m_CameraSystem;
        private SignalControlSystem m_Control;
        private JunctionToolSystem m_Tool;
        private LaneToolSystem m_LaneTool;
        private CoordinationSystem m_Coordination;
        private Game.City.CityConfigurationSystem m_CityConfiguration;

        private Entity m_Selected;
        private Entity m_Hovered;
        private bool m_PanelOpen;
        private DateTime m_SummaryTime;
        private Game.Simulation.SimulationSystem m_Simulation;
        private Game.Input.ProxyAction m_ToggleAction;

        /// <summary>The junction shown in the panel, or Null.</summary>
        public Entity Selected => m_Selected;

        /// <summary>The junction whose row in a list the pointer is over, or Null; marked on the map.</summary>
        public Entity Hovered => m_Hovered;

        /// <summary>
        /// Whether the panel is open. Kept here, not in the panel, so the
        /// key binding can open and close it.
        /// </summary>
        public bool PanelOpen => m_PanelOpen;
        private DateTime m_DetailTime;
        private Summary m_Summary = new Summary();
        private Detail m_Detail;

        private struct ProblemRow
        {
            public Entity Node;
            public string Name;

            /// <summary>Rush-hour queue of the worst movement, vehicles (see JunctionHealth).</summary>
            public float Queue;
        }

        private sealed class Summary
        {
            public bool Available;
            public int Managed;
            public readonly int[] ByMode = new int[(int)ControlMode.Drain + 1];
            public readonly List<ProblemRow> Problems = new List<ProblemRow>();
        }

        private sealed class Detail
        {
            public Entity Node;
            public string Name;
            public bool Managed;
            public bool HasSignals;
            public bool Roundabout;
            public ManagedJunction Junction;
            public ControllerState State;
            public PedestrianConflicts Conflicts;

            /// <summary>Game minutes until the junction's next review (AutopilotSystem).</summary>
            public float ReviewMinutes;
            public int Cycle;
            public int Intergreen;

            /// <summary>Steps into the cycle in the timed modes, whose schedule is fixed; -1 otherwise.</summary>
            public int CyclePosition = -1;
            public readonly List<PhaseRow> Phases = new List<PhaseRow>();
            public readonly List<MovementRow> Movements = new List<MovementRow>();
            public readonly List<ApproachRow> Approaches = new List<ApproachRow>();
            public bool LeftHandTraffic;
            public float CameraYaw;
            public bool HasAutopilot;
            public AutopilotState Autopilot;

            /// <summary>The ways through the junction and their rules; empty where there is no junction.</summary>
            public readonly List<TurnRow> Turns = new List<TurnRow>();

            /// <summary>The sign on each approach; empty where signals decide.</summary>
            public readonly List<SignRow> Signs = new List<SignRow>();
        }

        private struct SignRow
        {
            public int Approach;

            /// <summary>The sign the player chose, or Game.</summary>
            public PrioritySign Sign;

            /// <summary>The autopilot chose the sign (PriorityAutopilotSystem).</summary>
            public bool Auto;

            /// <summary>The sign the approach's lanes carry now, the game's own or TLL's.</summary>
            public PrioritySign Showing;
        }

        /// <summary>Who allowed or forbade a turn. Numbers as in the panel's TurnState.</summary>
        private enum TurnState
        {
            Allowed = 0,
            ForbiddenByAutopilot = 1,
            ForbiddenByPlayer = 2,
            AllowedByPlayer = 3,

            /// <summary>Forbidden by a road upgrade of the game, which TLL does not change.</summary>
            ForbiddenByGame = 4,
        }

        private struct TurnRow
        {
            public int Source;
            public int Target;
            public MovementKind Kind;
            public TurnState State;

            /// <summary>Peak vehicles per hour: measured, or for a forbidden turn what it carried before; negative if not known.</summary>
            public float Volume;
        }

        private struct MovementRow
        {
            public MovementKind Kind;
            public int Source;
            public int Target;

            /// <summary>Recent vehicles (or people, on a crosswalk) per hour; negative before the first measurement.</summary>
            public float Volume;
        }

        private struct ApproachRow
        {
            /// <summary>Unit direction from the junction out along the road, in the ground plane (x, z).</summary>
            public float2 Direction;
            public string Name;
        }

        private struct PhaseRow
        {
            public PhaseData Data;
            public ulong Movements;
            public ulong Permitted;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_Control = World.GetOrCreateSystemManaged<SignalControlSystem>();
            m_Tool = World.GetOrCreateSystemManaged<JunctionToolSystem>();
            m_LaneTool = World.GetOrCreateSystemManaged<LaneToolSystem>();
            m_Coordination = World.GetOrCreateSystemManaged<CoordinationSystem>();
            m_CityConfiguration = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            m_Simulation = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
            if (Mod.Settings != null)
            {
                m_ToggleAction = Mod.Settings.GetAction(Setting.kTogglePanel);
                m_ToggleAction.shouldBeEnabled = true;
            }
            m_ManagedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ManagedJunction>(), ComponentType.ReadOnly<JunctionPhase>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            AddUpdateBinding(new RawValueBinding(kGroup, "summary", WriteSummary));
            AddUpdateBinding(new RawValueBinding(kGroup, "selected", WriteDetail));
            AddBinding(new TriggerBinding(kGroup, "toggleAutomation", OnToggleAutomation));
            AddBinding(new TriggerBinding<int, int>(kGroup, "select", (index, version) => Select(ToEntity(index, version), false)));
            AddBinding(new TriggerBinding<int, int>(kGroup, "goto", (index, version) => Select(ToEntity(index, version), true)));
            AddBinding(new TriggerBinding<int>(kGroup, "setMode", OnSetMode));
            AddBinding(new TriggerBinding<int>(kGroup, "setStrategy", OnSetStrategy));
            AddBinding(new TriggerBinding(kGroup, "toggleScramble", OnToggleScramble));
            AddBinding(new TriggerBinding(kGroup, "toggleTurnOnRed", OnToggleTurnOnRed));
            AddBinding(new TriggerBinding<int, int>(kGroup, "cycleTurn", OnCycleTurn));
            AddBinding(new TriggerBinding<int, int>(kGroup, "setSign", OnSetSign));
            AddBinding(new TriggerBinding(kGroup, "makeAutomatic", OnMakeAutomatic));
            AddBinding(new TriggerBinding(kGroup, "release", OnRelease));
            AddBinding(new TriggerBinding(kGroup, "manage", OnManage));
            AddBinding(new TriggerBinding<bool>(kGroup, "setPanelOpen", open => m_PanelOpen = open));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "panelOpen", () => m_PanelOpen));
            AddBinding(new TriggerBinding<int, int>(kGroup, "hover", (index, version) => m_Hovered = ToEntity(index, version)));
            AddBinding(new TriggerBinding(kGroup, "unhover", () => m_Hovered = Entity.Null));
            AddBinding(new TriggerBinding(kGroup, "rebuildGreenWaves", () => Requests.RebuildGreenWaves = true));
            AddBinding(new TriggerBinding(kGroup, "toggleTool", () => m_Tool.Toggle()));
            AddBinding(new TriggerBinding(kGroup, "diagnose", OnDiagnose));
            AddBinding(new TriggerBinding(kGroup, "resetAllToAutomatic", () =>
            {
                WriteUser("reset_all", null);
                Requests.ResetAllToAutomatic = true;
            }));
            AddBinding(new TriggerBinding(kGroup, "toggleShowProblems", () => ChangeSetting(s => s.ShowProblems = !s.ShowProblems)));
            AddBinding(new TriggerBinding(kGroup, "toggleShowCongestion", () => ChangeSetting(s => s.ShowCongestion = !s.ShowCongestion)));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "toolActive", () => m_Tool.IsActive));
            AddBinding(new TriggerBinding(kGroup, "toggleLaneTool", () => m_LaneTool.Toggle(m_Selected)));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "laneToolActive", () => m_LaneTool.IsActive));
        }

        protected override void OnUpdate()
        {
            try
            {
                if (m_ToggleAction != null && m_ToggleAction.WasPerformedThisFrame())
                {
                    m_PanelOpen = !m_PanelOpen;
                    if (!m_PanelOpen)
                        m_Hovered = Entity.Null;
                }
                base.OnUpdate();
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "The TLL panel failed and was switched off until the game is restarted.");
                Enabled = false;
            }
        }

        private static Entity ToEntity(int index, int version)
        {
            return new Entity { Index = index, Version = version };
        }

        // ---- Overview ----

        private void WriteSummary(IJsonWriter writer)
        {
            if (DateTime.UtcNow - m_SummaryTime >= kSummaryInterval)
            {
                m_SummaryTime = DateTime.UtcNow;
                m_Summary = CollectSummary();
            }
            Summary s = m_Summary;
            writer.TypeBegin("tll.Summary");
            writer.PropertyName("available");
            writer.Write(s.Available);
            writer.PropertyName("conflict");
            writer.Write(m_Control.Conflict ?? "");
            writer.PropertyName("automation");
            writer.Write(Mod.Settings != null && Mod.Settings.AutoManageAll);
            writer.PropertyName("showProblems");
            writer.Write(Mod.Settings != null && Mod.Settings.ShowProblems);
            writer.PropertyName("showCongestion");
            writer.Write(Mod.Settings != null && Mod.Settings.ShowCongestion);
            writer.PropertyName("managed");
            writer.Write(s.Managed);
            writer.PropertyName("greenWaves");
            writer.Write(m_Coordination.Corridors);
            writer.PropertyName("coordinated");
            writer.Write(m_Coordination.CoordinatedJunctions);
            writer.PropertyName("byMode");
            writer.ArrayBegin((uint)s.ByMode.Length);
            foreach (int count in s.ByMode)
                writer.Write(count);
            writer.ArrayEnd();
            writer.PropertyName("problems");
            writer.ArrayBegin((uint)s.Problems.Count);
            foreach (ProblemRow p in s.Problems)
            {
                writer.TypeBegin("tll.Problem");
                WriteEntity(writer, p.Node);
                writer.PropertyName("name");
                writer.Write(p.Name);
                writer.PropertyName("queue");
                writer.Write(p.Queue);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.TypeEnd();
        }

        private Summary CollectSummary()
        {
            var summary = new Summary { Available = m_Control.Available };
            var rows = new List<ProblemRow>();
            using (NativeArray<Entity> nodes = m_ManagedQuery.ToEntityArray(Allocator.Temp))
            {
                summary.Managed = nodes.Length;
                foreach (Entity node in nodes)
                {
                    ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(node);
                    int mode = (int)junction.Mode;
                    if (mode >= 0 && mode < summary.ByMode.Length)
                        summary.ByMode[mode]++;

                    // Long-term figures only, so the list changes over game
                    // hours and not with every cycle.
                    if (!EntityManager.HasComponent<JunctionHealth>(node))
                        continue;
                    float queue = EntityManager.GetComponentData<JunctionHealth>(node).WorstQueue;
                    if (queue >= MapOverlaySystem.ProblemQueue)
                        rows.Add(new ProblemRow { Node = node, Queue = queue });
                }
            }
            rows.Sort((a, b) => b.Queue.CompareTo(a.Queue));
            for (int i = 0; i < rows.Count && i < kProblemCount; i++)
            {
                ProblemRow row = rows[i];
                row.Name = JunctionName(row.Node);
                summary.Problems.Add(row);
            }
            return summary;
        }

        // ---- Selected junction ----

        private void WriteDetail(IJsonWriter writer)
        {
            if (DateTime.UtcNow - m_DetailTime >= kDetailInterval)
            {
                m_DetailTime = DateTime.UtcNow;
                m_Detail = CollectDetail();
            }
            Detail d = m_Detail;
            if (d == null)
            {
                writer.WriteNull();
                return;
            }
            writer.TypeBegin("tll.Junction");
            WriteEntity(writer, d.Node);
            writer.PropertyName("name");
            writer.Write(d.Name);
            writer.PropertyName("managed");
            writer.Write(d.Managed);
            writer.PropertyName("hasSignals");
            writer.Write(d.HasSignals);
            writer.PropertyName("roundabout");
            writer.Write(d.Roundabout);
            writer.PropertyName("mode");
            writer.Write((int)d.Junction.Mode);
            writer.PropertyName("strategy");
            writer.Write((int)d.Junction.Strategy);
            writer.PropertyName("group");
            writer.Write(d.Junction.Group);
            writer.PropertyName("manual");
            writer.Write(d.Junction.Origin == JunctionOrigin.Manual);
            writer.PropertyName("stage");
            writer.Write((int)d.State.Stage);
            writer.PropertyName("phase");
            writer.Write((int)d.State.Phase);
            writer.PropertyName("next");
            writer.Write((int)d.State.Next);
            writer.PropertyName("walk");
            writer.Write(d.State.Walk);
            writer.PropertyName("scramble");
            writer.Write((d.Junction.Options & JunctionOptions.Scramble) != 0);
            writer.PropertyName("turnOnRed");
            writer.Write((d.Junction.Options & JunctionOptions.TurnOnRed) != 0);
            writer.PropertyName("conflicts");
            writer.Write(d.Conflicts.Count);
            writer.PropertyName("reviewMinutes");
            writer.Write(d.ReviewMinutes);
            writer.PropertyName("stageSeconds");
            writer.Write(SimTime.ToSeconds(d.State.StageSteps));
            writer.PropertyName("cycleSeconds");
            writer.Write(SimTime.ToSeconds(d.Cycle));
            writer.PropertyName("intergreenSeconds");
            writer.Write(SimTime.ToSeconds(d.Intergreen));
            writer.PropertyName("cyclePosition");
            writer.Write(d.CyclePosition >= 0 ? SimTime.ToSeconds(d.CyclePosition) : -1f);
            writer.PropertyName("leftHandTraffic");
            writer.Write(d.LeftHandTraffic);
            writer.PropertyName("cameraYaw");
            writer.Write(d.CameraYaw);
            writer.PropertyName("approaches");
            writer.ArrayBegin((uint)d.Approaches.Count);
            foreach (ApproachRow a in d.Approaches)
            {
                writer.TypeBegin("tll.Approach");
                writer.PropertyName("x");
                writer.Write(a.Direction.x);
                writer.PropertyName("z");
                writer.Write(a.Direction.y);
                writer.PropertyName("name");
                writer.Write(a.Name);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.PropertyName("movements");
            writer.ArrayBegin((uint)d.Movements.Count);
            foreach (MovementRow m in d.Movements)
            {
                writer.TypeBegin("tll.Movement");
                writer.PropertyName("kind");
                writer.Write((int)m.Kind);
                writer.PropertyName("source");
                writer.Write(m.Source);
                writer.PropertyName("target");
                writer.Write(m.Target);
                writer.PropertyName("volume");
                writer.Write(m.Volume);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.PropertyName("turns");
            writer.ArrayBegin((uint)d.Turns.Count);
            foreach (TurnRow r in d.Turns)
            {
                writer.TypeBegin("tll.Turn");
                writer.PropertyName("source");
                writer.Write(r.Source);
                writer.PropertyName("target");
                writer.Write(r.Target);
                writer.PropertyName("kind");
                writer.Write((int)r.Kind);
                writer.PropertyName("state");
                writer.Write((int)r.State);
                writer.PropertyName("volume");
                writer.Write(r.Volume);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.PropertyName("signs");
            writer.ArrayBegin((uint)d.Signs.Count);
            foreach (SignRow s in d.Signs)
            {
                writer.TypeBegin("tll.Sign");
                writer.PropertyName("approach");
                writer.Write(s.Approach);
                writer.PropertyName("sign");
                writer.Write((int)s.Sign);
                writer.PropertyName("showing");
                writer.Write((int)s.Showing);
                writer.PropertyName("auto");
                writer.Write(s.Auto);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            WriteAutopilot(writer, d);
            writer.PropertyName("phases");
            writer.ArrayBegin((uint)d.Phases.Count);
            foreach (PhaseRow p in d.Phases)
            {
                writer.TypeBegin("tll.Phase");
                writer.PropertyName("minGreen");
                writer.Write(SimTime.ToSeconds(p.Data.MinGreen));
                writer.PropertyName("walkGreen");
                writer.Write(SimTime.ToSeconds(p.Data.WalkGreen));
                writer.PropertyName("maxGreen");
                writer.Write(SimTime.ToSeconds(p.Data.MaxGreen));
                writer.PropertyName("green");
                writer.Write(SimTime.ToSeconds(p.Data.Green));
                writer.PropertyName("demand");
                writer.Write(p.Data.Demand);
                writer.PropertyName("pressure");
                writer.Write(p.Data.Pressure);
                writer.PropertyName("wait");
                writer.Write(SimTime.ToSeconds(p.Data.WaitSteps));
                writer.PropertyName("busy");
                writer.Write(p.Data.Busy);
                writer.PropertyName("preempt");
                writer.Write(p.Data.Preempt);
                writer.PropertyName("movements");
                WriteBits(writer, p.Movements);
                writer.PropertyName("permitted");
                WriteBits(writer, p.Permitted);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.TypeEnd();
        }

        /// <summary>What the autopilot measured and decided at the junction, or null where it does not run.</summary>
        private static void WriteAutopilot(IJsonWriter writer, Detail d)
        {
            writer.PropertyName("autopilot");
            if (!d.HasAutopilot)
            {
                writer.WriteNull();
                return;
            }
            AutopilotState a = d.Autopilot;
            writer.TypeBegin("tll.Autopilot");
            writer.PropertyName("majorVolume");
            writer.Write(a.MajorVolume);
            writer.PropertyName("minorVolume");
            writer.Write(a.MinorVolume);
            writer.PropertyName("signalAdvice");
            writer.Write((int)a.SignalAdvice);
            writer.PropertyName("tooQuiet");
            writer.Write(a.TooQuiet);
            writer.PropertyName("pending");
            writer.Write(a.Layout.PendingReviews > 0 ? (int)a.Layout.Pending : -1);
            writer.PropertyName("pendingScramble");
            writer.Write(a.Layout.PendingReviews > 0 && a.Layout.PendingScramble);
            writer.PropertyName("measuredWait");
            writer.Write(a.MeasuredWait);
            writer.PropertyName("modelledWait");
            writer.Write(a.ModelledWait);
            writer.PropertyName("waveBanMinutes");
            writer.Write(a.WaveBan * AutopilotSystem.kRoundFrames * 1440f / Game.Simulation.TimeSystem.kTicksPerDay);
            bool wave = d.Junction.Mode == ControlMode.Coordinated;
            writer.PropertyName("estimates");
            int count = a.HasEstimate ? JunctionAdvisor.Strategies.Length : 0;
            writer.ArrayBegin((uint)count);
            for (int i = 0; i < count; i++)
            {
                writer.TypeBegin("tll.Estimate");
                writer.PropertyName("strategy");
                writer.Write((int)JunctionAdvisor.Strategies[i]);
                writer.PropertyName("delay");
                writer.Write(a.LayoutDelay[i]);
                writer.PropertyName("saturation");
                writer.Write(a.LayoutSaturation[i]);
                Calibration c = a.Memory.Get(i, wave);
                writer.PropertyName("measured");
                writer.Write(c.Measured);
                writer.PropertyName("jammed");
                writer.Write(c.Measured && c.Backlog >= LayoutMemory.BacklogShare);
                Calibration s = a.Memory.Get(i, wave, true);
                writer.PropertyName("scrambleDelay");
                writer.Write(a.ScrambleDelay[i]);
                writer.PropertyName("scrambleSaturation");
                writer.Write(a.ScrambleSaturation[i]);
                writer.PropertyName("scrambleMeasured");
                writer.Write(s.Measured);
                writer.PropertyName("scrambleJammed");
                writer.Write(s.Measured && s.Backlog >= LayoutMemory.BacklogShare);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.TypeEnd();
        }

        private Detail CollectDetail()
        {
            Entity node = m_Selected;
            if (node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
                return null;

            var detail = new Detail
            {
                Node = node,
                Name = JunctionName(node),
                HasSignals = EntityManager.HasComponent<TrafficLights>(node),
                Managed = EntityManager.HasComponent<ManagedJunction>(node) && EntityManager.HasBuffer<JunctionPhase>(node),
                Roundabout = IsRoundabout(node),
            };
            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            foreach (Entity edge in edges)
                detail.Approaches.Add(new ApproachRow { Direction = NetGeometry.Outward(EntityManager, node, edge), Name = RoadName(edge) });
            if (edges.Count >= 3 && !detail.Roundabout)
            {
                CollectTurns(node, edges, detail);
                if (!detail.HasSignals)
                    CollectSigns(node, edges, detail);
            }
            if (!detail.Managed)
                return detail;
            detail.Junction = EntityManager.GetComponentData<ManagedJunction>(node);
            if (EntityManager.HasComponent<JunctionRuntime>(node))
            {
                JunctionRuntime runtime = EntityManager.GetComponentData<JunctionRuntime>(node);
                detail.State = runtime.State;
                detail.Conflicts = runtime.Conflicts;
            }
            detail.ReviewMinutes = AutopilotSystem.FramesToReview(m_Simulation.frameIndex, node) * 1440f / Game.Simulation.TimeSystem.kTicksPerDay;

            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(node, true);
            int intergreen = detail.Junction.Yellow + detail.Junction.AllRed + detail.Junction.Prepare;
            for (int i = 0; i < phases.Length; i++)
            {
                detail.Phases.Add(new PhaseRow { Data = phases[i].Data, Movements = phases[i].Movements, Permitted = phases[i].Permitted });
                detail.Cycle += phases[i].Data.Green + intergreen;
            }
            detail.Intergreen = intergreen;
            // The same position the controller computes (SignalController),
            // for the cursor on the panel's cycle bar.
            ControlMode mode = detail.Junction.Mode;
            if ((mode == ControlMode.FixedTime || mode == ControlMode.Coordinated) && detail.Cycle > 0)
                detail.CyclePosition = SimTime.Mod(SimTime.StepOfFrame(m_Simulation.frameIndex) - detail.Junction.Offset, detail.Cycle);

            detail.LeftHandTraffic = m_CityConfiguration.leftHandTraffic;
            IGameCameraController camera = m_CameraSystem.activeCameraController;
            detail.CameraYaw = camera != null ? camera.rotation.y : 0f;

            if (EntityManager.HasBuffer<JunctionMovement>(node))
            {
                DynamicBuffer<JunctionMovement> movements = EntityManager.GetBuffer<JunctionMovement>(node, true);
                bool measured = EntityManager.HasBuffer<MovementStatistics>(node);
                DynamicBuffer<MovementStatistics> statistics = measured ? EntityManager.GetBuffer<MovementStatistics>(node, true) : default;
                measured = measured && statistics.Length == movements.Length;
                for (int i = 0; i < movements.Length; i++)
                {
                    MovementStatistics s = measured ? statistics[i] : default;
                    bool fresh = s.Recent == 0f && s.Daily == 0f && s.Peak == 0f;
                    detail.Movements.Add(new MovementRow
                    {
                        Kind = movements[i].Kind,
                        Source = edges.IndexOf(movements[i].Source),
                        Target = edges.IndexOf(movements[i].Target),
                        Volume = measured && !fresh ? s.Recent : -1f,
                    });
                }
            }
            detail.HasAutopilot = EntityManager.HasComponent<AutopilotState>(node);
            if (detail.HasAutopilot)
                detail.Autopilot = EntityManager.GetComponentData<AutopilotState>(node);
            return detail;
        }

        /// <summary>The junction's turns with their rules, for the panel's list.</summary>
        private void CollectTurns(Entity node, List<Entity> edges, Detail detail)
        {
            bool hasRules = EntityManager.HasBuffer<TurnRule>(node);
            DynamicBuffer<TurnRule> rules = hasRules ? EntityManager.GetBuffer<TurnRule>(node, true) : default;
            bool measured = EntityManager.HasBuffer<JunctionMovement>(node) && EntityManager.HasBuffer<MovementStatistics>(node);
            DynamicBuffer<JunctionMovement> movements = measured ? EntityManager.GetBuffer<JunctionMovement>(node, true) : default;
            DynamicBuffer<MovementStatistics> statistics = measured ? EntityManager.GetBuffer<MovementStatistics>(node, true) : default;
            foreach (NodeTurn turn in NodeTurns.Collect(EntityManager, node, edges))
            {
                var row = new TurnRow { Source = turn.Source, Target = turn.Target, Kind = turn.Kind, Volume = -1f };
                int rule = -1;
                for (int i = 0; hasRules && i < rules.Length; i++)
                {
                    if (rules[i].From == turn.From && rules[i].To == turn.To)
                        rule = i;
                }
                if (rule >= 0)
                {
                    TurnRule r = rules[rule];
                    row.State = r.ByPlayer
                        ? (r.Forbidden ? TurnState.ForbiddenByPlayer : TurnState.AllowedByPlayer)
                        : (r.Forbidden ? TurnState.ForbiddenByAutopilot : TurnState.Allowed);
                    if (r.Forbidden)
                        row.Volume = r.Volume;
                }
                else
                {
                    row.State = turn.Forbidden ? TurnState.ForbiddenByGame : TurnState.Allowed;
                }
                for (int m = 0; measured && m < movements.Length && m < statistics.Length && row.Volume < 0f; m++)
                {
                    if (movements[m].Source == turn.From && movements[m].Target == turn.To && movements[m].Kind == turn.Kind)
                        row.Volume = statistics[m].Peak;
                }
                detail.Turns.Add(row);
            }
        }

        /// <summary>The sign on each approach, as chosen and as the lanes carry it.</summary>
        private void CollectSigns(Entity node, List<Entity> edges, Detail detail)
        {
            bool hasRules = EntityManager.HasBuffer<PriorityRule>(node);
            DynamicBuffer<PriorityRule> rules = hasRules ? EntityManager.GetBuffer<PriorityRule>(node, true) : default;
            var showing = new CarLaneFlags[edges.Count];
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!EntityManager.HasComponent<CarLane>(lane))
                    continue;
                PathNode start = EntityManager.GetComponentData<Lane>(lane).m_StartNode;
                for (int e = 0; e < edges.Count; e++)
                {
                    if (start.OwnerEquals(new PathNode(edges[e], 0)))
                        showing[e] |= EntityManager.GetComponentData<CarLane>(lane).m_Flags;
                }
            }
            for (int e = 0; e < edges.Count; e++)
            {
                var row = new SignRow { Approach = e, Sign = PrioritySign.Game };
                for (int i = 0; hasRules && i < rules.Length; i++)
                {
                    if (rules[i].Edge != edges[e])
                        continue;
                    if (rules[i].ByPlayer)
                        row.Sign = rules[i].Sign;
                    else
                        row.Auto = true;
                }
                CarLaneFlags flags = showing[e];
                row.Showing = (flags & CarLaneFlags.Stop) != 0 ? PrioritySign.Stop
                    : (flags & CarLaneFlags.Yield) != 0 ? PrioritySign.Yield
                    : (flags & CarLaneFlags.RightOfWay) != 0 ? PrioritySign.Priority
                    : PrioritySign.Game;
                detail.Signs.Add(row);
            }
        }

        // ---- Triggers ----

        /// <summary>
        /// Puts a sign on an approach, or with Game hands it back to the
        /// game's rule. A sign of the player's takes the junction from the
        /// autopilot: its signs go. The junction and its roads are rebuilt:
        /// the roads' lanes carry the stop line and its marking.
        /// </summary>
        private void OnSetSign(int approach, int sign)
        {
            Entity node = m_Selected;
            if (node == Entity.Null || !EntityManager.Exists(node) || sign < 0 || sign > (int)PrioritySign.Stop)
                return;
            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            if (approach < 0 || approach >= edges.Count)
                return;
            DynamicBuffer<PriorityRule> rules = EntityManager.HasBuffer<PriorityRule>(node)
                ? EntityManager.GetBuffer<PriorityRule>(node)
                : EntityManager.AddBuffer<PriorityRule>(node);
            for (int i = rules.Length - 1; i >= 0; i--)
            {
                if (rules[i].Edge == edges[approach] || !rules[i].ByPlayer)
                    rules.RemoveAt(i);
            }
            if ((PrioritySign)sign != PrioritySign.Game)
                rules.Add(new PriorityRule { Edge = edges[approach], Sign = (PrioritySign)sign, Flags = PriorityRuleFlags.Player });
            EntityManager.AddComponent<RebuildRequest>(node);
            foreach (Entity edge in edges)
                EntityManager.AddComponent<RebuildRequest>(edge);
            m_DetailTime = DateTime.MinValue;
        }

        /// <summary>
        /// The next rule for a turn, as the panel's button goes round:
        /// the autopilot's choice, then forbidden by the player, then allowed
        /// by the player, then the autopilot's choice again. The junction is
        /// rebuilt, which applies it (LaneRuleSystem).
        /// </summary>
        private void OnCycleTurn(int source, int target)
        {
            Entity node = m_Selected;
            if (node == Entity.Null || !EntityManager.Exists(node))
                return;
            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            if (source < 0 || target < 0 || source >= edges.Count || target >= edges.Count)
                return;
            DynamicBuffer<TurnRule> rules = EntityManager.HasBuffer<TurnRule>(node)
                ? EntityManager.GetBuffer<TurnRule>(node)
                : EntityManager.AddBuffer<TurnRule>(node);
            int index = -1;
            for (int i = 0; i < rules.Length; i++)
            {
                if (rules[i].From == edges[source] && rules[i].To == edges[target])
                    index = i;
            }
            TurnRule rule = index >= 0 ? rules[index] : new TurnRule { From = edges[source], To = edges[target] };
            if (rule.ByPlayer && !rule.Forbidden)
            {
                rules.RemoveAt(index);
            }
            else
            {
                // Forbidden by the autopilot: the player allows it. Allowed,
                // by anyone: the player forbids it.
                rule.Flags = rule.Forbidden && !rule.ByPlayer ? TurnRuleFlags.Player : TurnRuleFlags.Player | TurnRuleFlags.Forbidden;
                if (index >= 0)
                    rules[index] = rule;
                else
                    rules.Add(rule);
            }
            EntityManager.AddComponent<RebuildRequest>(node);
            m_DetailTime = DateTime.MinValue;
        }

        private void OnToggleAutomation()
        {
            ChangeSetting(s => s.AutoManageAll = !s.AutoManageAll);
        }

        private void OnDiagnose()
        {
            if (m_Selected == Entity.Null || !EntityManager.Exists(m_Selected))
                return;
            Mod.Log.Info(JunctionDiagnostics.Describe(EntityManager, m_Selected, m_CityConfiguration.leftHandTraffic));
        }

        /// <summary>Changes a setting from the panel, saves it, and refreshes the overview at once.</summary>
        private void ChangeSetting(Action<Setting> change)
        {
            if (Mod.Settings == null)
                return;
            change(Mod.Settings);
            Mod.Settings.ApplyAndSave();
            m_SummaryTime = default;
        }

        /// <summary>Shows a junction in the panel, managed or not; optionally moves the camera there.</summary>
        public void Select(Entity node, bool moveCamera)
        {
            m_Selected = node;
            m_DetailTime = default;
            if (!moveCamera || node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
                return;
            IGameCameraController camera = m_CameraSystem.activeCameraController;
            if (camera != null)
                camera.pivot = EntityManager.GetComponentData<Node>(node).m_Position;
        }

        /// <summary>A record in the metrics log of what the player did, at the selected junction.</summary>
        private void WriteUser(string action, string value)
        {
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, m_Selected, "user")?.Add("action", action).Add("value", value));
        }

        private void OnSetMode(int mode)
        {
            if (!TryGetSelected(out ManagedJunction junction) || mode < 0 || mode > (int)ControlMode.Drain)
                return;
            WriteUser("mode", ((ControlMode)mode).ToString());
            TakeOverByPlayer(ref junction);
            junction.Mode = (ControlMode)mode;
            EntityManager.SetComponentData(m_Selected, junction);
            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(m_Selected);
            for (int p = 0; p < phases.Length; p++)
                phases.ElementAt(p).Data.Flags &= ~PhaseFlags.Coordinated;
            m_DetailTime = default;
        }

        /// <summary>
        /// The player's change makes the junction theirs. The automation
        /// leaves such junctions alone, green waves included, so one that ran
        /// in a wave leaves it and runs on its own; the wave is planned again
        /// without it.
        /// </summary>
        private static void TakeOverByPlayer(ref ManagedJunction junction)
        {
            junction.Origin = JunctionOrigin.Manual;
            if (junction.Mode == ControlMode.Coordinated || junction.Group != 0)
            {
                junction.Mode = Mod.Settings != null ? Mod.Settings.AutoControl() : ControlMode.Adaptive;
                junction.Group = 0;
                junction.Offset = 0;
                Requests.RebuildGreenWaves = true;
            }
        }

        /// <summary>
        /// Gives the selected junction back to the automation: automatic
        /// settings, a generated plan, and a place in green waves again. The
        /// traffic measured so far stays.
        /// </summary>
        private void OnMakeAutomatic()
        {
            if (!TryGetSelected(out _))
                return;
            WriteUser("make_automatic", null);
            Setting settings = Mod.Settings;
            ManagedJunction junction = ManagedJunction.Create(JunctionOrigin.Auto,
                settings != null ? settings.AutoControl() : ControlMode.Adaptive,
                settings != null ? settings.InitialStrategy() : PlanStrategy.Permissive,
                settings != null && settings.InitialScramble());
            EntityManager.SetComponentData(m_Selected, junction);
            EntityManager.GetBuffer<JunctionPhase>(m_Selected).Clear();
            EntityManager.RemoveComponent<AutopilotState>(m_Selected);
            EntityManager.AddComponent<RebuildRequest>(m_Selected);
            Requests.RebuildGreenWaves = true;
            m_DetailTime = default;
            m_SummaryTime = default;
        }

        private void OnSetStrategy(int strategy)
        {
            if (!TryGetSelected(out ManagedJunction junction) || strategy < 0 || strategy > (int)PlanStrategy.Split)
                return;
            WriteUser("layout", ((PlanStrategy)strategy).ToString());
            TakeOverByPlayer(ref junction);
            junction.Strategy = (PlanStrategy)strategy;
            EntityManager.SetComponentData(m_Selected, junction);
            // An empty plan makes JunctionInitSystem generate a new one. The
            // node is rebuilt as a whole, so the signal poles follow the new
            // phase count.
            EntityManager.GetBuffer<JunctionPhase>(m_Selected).Clear();
            EntityManager.AddComponent<RebuildRequest>(m_Selected);
            m_DetailTime = default;
        }

        /// <summary>Switches the scramble of the selected junction; the plan is generated anew.</summary>
        private void OnToggleScramble()
        {
            if (!TryGetSelected(out ManagedJunction junction))
                return;
            junction.Options ^= JunctionOptions.Scramble;
            WriteUser("scramble", ((junction.Options & JunctionOptions.Scramble) != 0).ToString());
            TakeOverByPlayer(ref junction);
            if (EntityManager.HasComponent<JunctionRuntime>(m_Selected))
            {
                JunctionRuntime runtime = EntityManager.GetComponentData<JunctionRuntime>(m_Selected);
                runtime.Conflicts = default;
                EntityManager.SetComponentData(m_Selected, runtime);
            }
            EntityManager.SetComponentData(m_Selected, junction);
            EntityManager.GetBuffer<JunctionPhase>(m_Selected).Clear();
            EntityManager.AddComponent<RebuildRequest>(m_Selected);
            m_DetailTime = default;
        }

        /// <summary>Switches turning on red at the selected junction. It takes effect at once, without a new plan.</summary>
        private void OnToggleTurnOnRed()
        {
            if (!TryGetSelected(out ManagedJunction junction))
                return;
            junction.Options ^= JunctionOptions.TurnOnRed;
            WriteUser("turn_on_red", ((junction.Options & JunctionOptions.TurnOnRed) != 0).ToString());
            TakeOverByPlayer(ref junction);
            EntityManager.SetComponentData(m_Selected, junction);
            m_DetailTime = default;
        }

        /// <summary>
        /// Takes over the selected signalled junction: as an automatic one
        /// while the city-wide automation runs, else as one the player
        /// configures.
        /// </summary>
        private void OnManage()
        {
            Entity node = m_Selected;
            if (node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<TrafficLights>(node)
                || EntityManager.HasComponent<ManagedJunction>(node))
                return;
            Setting settings = Mod.Settings;
            JunctionOrigin origin = settings != null && settings.AutoManageAll ? JunctionOrigin.Auto : JunctionOrigin.Manual;
            ManagedJunction junction = ManagedJunction.Create(origin,
                settings != null ? settings.AutoControl() : ControlMode.Adaptive,
                settings != null ? settings.InitialStrategy() : PlanStrategy.Permissive,
                settings != null && settings.InitialScramble());
            EntityManager.RemoveComponent<JunctionExcluded>(node);
            EntityManager.AddComponentData(node, junction);
            EntityManager.AddComponent<RebuildRequest>(node);
            WriteUser("manage", origin.ToString());
            m_DetailTime = default;
            m_SummaryTime = default;
        }

        private void OnRelease()
        {
            if (!TryGetSelected(out _))
                return;
            WriteUser("release", null);
            // Excluded, so the automation does not take it straight back.
            JunctionInitSystem.Release(EntityManager, m_Selected, exclude: true);
            m_Selected = Entity.Null;
            m_SummaryTime = default;
            m_DetailTime = default;
        }

        private bool TryGetSelected(out ManagedJunction junction)
        {
            junction = default;
            if (m_Selected == Entity.Null || !EntityManager.Exists(m_Selected) || !EntityManager.HasComponent<ManagedJunction>(m_Selected))
                return false;
            junction = EntityManager.GetComponentData<ManagedJunction>(m_Selected);
            return true;
        }

        // ---- Helpers ----

        /// <summary>
        /// Whether the node is a roundabout (the game's roundabout upgrade),
        /// recognised by the roundabout flag the game puts on its lanes.
        /// </summary>
        private bool IsRoundabout(Entity node)
        {
            if (!EntityManager.HasBuffer<SubLane>(node))
                return false;
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (EntityManager.HasComponent<CarLane>(lane) && (EntityManager.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Roundabout) != 0)
                    return true;
            }
            return false;
        }

        /// <summary>A junction is named after the roads meeting there, e.g. "Main Street / Oak Avenue".</summary>
        private string JunctionName(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node))
                return node.ToString();
            var names = new List<string>();
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < edges.Length && names.Count < 3; i++)
            {
                string name = RoadName(edges[i].m_Edge);
                if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                    names.Add(name);
            }
            return names.Count > 0 ? string.Join(" / ", names) : node.ToString();
        }

        /// <summary>
        /// The street name of a road segment. The name belongs to the street
        /// the segment is part of (its aggregate); the segment itself only
        /// carries the name of its road type.
        /// </summary>
        private string RoadName(Entity edge)
        {
            if (edge == Entity.Null || !EntityManager.Exists(edge))
                return "";
            if (EntityManager.HasComponent<Aggregated>(edge))
            {
                Entity street = EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
                if (street != Entity.Null && EntityManager.Exists(street))
                    return m_NameSystem.GetRenderedLabelName(street) ?? "";
            }
            return m_NameSystem.GetRenderedLabelName(edge) ?? "";
        }

        private static void WriteEntity(IJsonWriter writer, Entity entity)
        {
            writer.PropertyName("index");
            writer.Write(entity.Index);
            writer.PropertyName("version");
            writer.Write(entity.Version);
        }

        /// <summary>Writes the set bits of a movement mask as an array of indices.</summary>
        private static void WriteBits(IJsonWriter writer, ulong bits)
        {
            var indices = new List<int>();
            for (int i = 0; i < 64; i++)
            {
                if ((bits & (1UL << i)) != 0)
                    indices.Add(i);
            }
            writer.ArrayBegin((uint)indices.Count);
            foreach (int i in indices)
                writer.Write(i);
            writer.ArrayEnd();
        }
    }
}
