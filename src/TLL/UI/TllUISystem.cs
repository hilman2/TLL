using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.Net;
using Game.Rendering;
using Game.Tools;
using Game.UI;
using TLL.Components;
using TLL.Core;
using TLL.Core.Advisor;
using TLL.Core.Control;
using TLL.Core.Planning;
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
        private CoordinationSystem m_Coordination;
        private Game.City.CityConfigurationSystem m_CityConfiguration;

        private Entity m_Selected;
        private bool m_PanelOpen;
        private DateTime m_SummaryTime;

        /// <summary>The junction shown in the panel, or Null.</summary>
        public Entity Selected => m_Selected;

        /// <summary>Whether the player has the panel open; the panel reports it.</summary>
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
            public readonly int[] ByMode = new int[5];
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
            public int Cycle;
            public readonly List<PhaseRow> Phases = new List<PhaseRow>();
            public readonly List<MovementRow> Movements = new List<MovementRow>();
            public readonly List<ApproachRow> Approaches = new List<ApproachRow>();
            public bool LeftHandTraffic;
            public float CameraYaw;
            public bool HasAutopilot;
            public AutopilotState Autopilot;
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
            m_Coordination = World.GetOrCreateSystemManaged<CoordinationSystem>();
            m_CityConfiguration = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
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
            AddBinding(new TriggerBinding(kGroup, "release", OnRelease));
            AddBinding(new TriggerBinding(kGroup, "manage", OnManage));
            AddBinding(new TriggerBinding<bool>(kGroup, "setPanelOpen", open => m_PanelOpen = open));
            AddBinding(new TriggerBinding(kGroup, "rebuildGreenWaves", () => Requests.RebuildGreenWaves = true));
            AddBinding(new TriggerBinding(kGroup, "toggleTool", () => m_Tool.Toggle()));
            AddBinding(new TriggerBinding(kGroup, "diagnose", OnDiagnose));
            AddBinding(new TriggerBinding(kGroup, "toggleShowProblems", () => ChangeSetting(s => s.ShowProblems = !s.ShowProblems)));
            AddBinding(new TriggerBinding(kGroup, "toggleShowCongestion", () => ChangeSetting(s => s.ShowCongestion = !s.ShowCongestion)));
            AddUpdateBinding(new GetterValueBinding<bool>(kGroup, "toolActive", () => m_Tool.IsActive));
        }

        protected override void OnUpdate()
        {
            try
            {
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
            writer.PropertyName("stageSeconds");
            writer.Write(SimTime.ToSeconds(d.State.StageSteps));
            writer.PropertyName("cycleSeconds");
            writer.Write(SimTime.ToSeconds(d.Cycle));
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
            writer.PropertyName("pending");
            writer.Write(a.PendingRounds > 0 ? (int)a.Pending : -1);
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
            if (!detail.Managed)
                return detail;
            detail.Junction = EntityManager.GetComponentData<ManagedJunction>(node);
            if (EntityManager.HasComponent<JunctionRuntime>(node))
                detail.State = EntityManager.GetComponentData<JunctionRuntime>(node).State;

            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(node, true);
            int intergreen = detail.Junction.Yellow + detail.Junction.AllRed + detail.Junction.Prepare;
            for (int i = 0; i < phases.Length; i++)
            {
                detail.Phases.Add(new PhaseRow { Data = phases[i].Data, Movements = phases[i].Movements, Permitted = phases[i].Permitted });
                detail.Cycle += phases[i].Data.Green + intergreen;
            }

            detail.LeftHandTraffic = m_CityConfiguration.leftHandTraffic;
            IGameCameraController camera = m_CameraSystem.activeCameraController;
            detail.CameraYaw = camera != null ? camera.rotation.y : 0f;

            List<Entity> edges = NetGeometry.ConnectedEdges(EntityManager, node);
            foreach (Entity edge in edges)
                detail.Approaches.Add(new ApproachRow { Direction = NetGeometry.Outward(EntityManager, node, edge), Name = RoadName(edge) });
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

        // ---- Triggers ----

        private void OnToggleAutomation()
        {
            ChangeSetting(s => s.AutoManageAll = !s.AutoManageAll);
        }

        private void OnDiagnose()
        {
            if (m_Selected == Entity.Null || !EntityManager.Exists(m_Selected))
                return;
            Mod.Log.Info(JunctionDiagnostics.Describe(EntityManager, m_Selected));
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

        private void OnSetMode(int mode)
        {
            if (!TryGetSelected(out ManagedJunction junction) || mode < 0 || mode > (int)ControlMode.Flashing)
                return;
            junction.Mode = (ControlMode)mode;
            junction.Origin = JunctionOrigin.Manual;
            EntityManager.SetComponentData(m_Selected, junction);
            m_DetailTime = default;
        }

        private void OnSetStrategy(int strategy)
        {
            if (!TryGetSelected(out ManagedJunction junction) || strategy < 0 || strategy > (int)PlanStrategy.ExclusivePedestrian)
                return;
            junction.Strategy = (PlanStrategy)strategy;
            junction.Origin = JunctionOrigin.Manual;
            EntityManager.SetComponentData(m_Selected, junction);
            // An empty plan makes JunctionInitSystem generate a new one. The
            // node is rebuilt as a whole, so the signal poles follow the new
            // phase count.
            EntityManager.GetBuffer<JunctionPhase>(m_Selected).Clear();
            EntityManager.AddComponent<RebuildRequest>(m_Selected);
            m_DetailTime = default;
        }

        /// <summary>Takes over the selected signalled junction as one the player configures.</summary>
        private void OnManage()
        {
            Entity node = m_Selected;
            if (node == Entity.Null || !EntityManager.Exists(node) || !EntityManager.HasComponent<TrafficLights>(node)
                || EntityManager.HasComponent<ManagedJunction>(node))
                return;
            Setting settings = Mod.Settings;
            ManagedJunction junction = ManagedJunction.Create(JunctionOrigin.Manual,
                settings != null ? settings.AutoControl() : ControlMode.Adaptive,
                settings != null ? settings.InitialStrategy() : PlanStrategy.Permissive);
            EntityManager.RemoveComponent<JunctionExcluded>(node);
            EntityManager.AddComponentData(node, junction);
            EntityManager.AddComponent<RebuildRequest>(node);
            m_DetailTime = default;
            m_SummaryTime = default;
        }

        private void OnRelease()
        {
            if (!TryGetSelected(out _))
                return;
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
