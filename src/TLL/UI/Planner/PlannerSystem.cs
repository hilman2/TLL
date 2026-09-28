using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using Game.Common;
using Game.Net;
using Game.Tools;
using Game.UI;
using TLL.Components;
using TLL.Core;
using TLL.Core.Control;
using TLL.Core.Planning;
using TLL.Metrics;
using TLL.Systems;
using Unity.Entities;

namespace TLL.UI.Planner
{
    /// <summary>
    /// The planner: one junction's phases and times, edited by hand in a
    /// panel of their own (src/TLL.UI/src/planner.tsx).
    ///
    /// The player edits a draft (<see cref="PlannerDraft"/>). The junction
    /// keeps running its plan until the draft is applied, or with every
    /// edit while the setting PlannerLive is on. A draft with an error the
    /// check finds (PlanCheck) is never applied. Every edit can be undone
    /// while the planner is open.
    ///
    /// All values go to the panel in one binding, "tll.planner", written
    /// whenever something changes and a few times a second while the planner
    /// is open, for the running phase. The panel talks back through
    /// triggers named "planner…".
    /// </summary>
    public partial class PlannerSystem : UISystemBase
    {
        private const string kGroup = "tll";

        /// <summary>How often the open planner looks at the junction for changes it did not make, and at the running phase.</summary>
        private static readonly TimeSpan kRefresh = TimeSpan.FromMilliseconds(250);

        /// <summary>How long a message (a refused click, "copied") stays in the panel.</summary>
        private static readonly TimeSpan kMessageTime = TimeSpan.FromSeconds(6);

        private TllUISystem m_UI;
        private Game.City.CityConfigurationSystem m_CityConfiguration;
        private Game.Rendering.CameraUpdateSystem m_Camera;
        private Game.Simulation.SimulationSystem m_Simulation;
        private RawValueBinding m_Binding;
        private readonly PresetStore m_Presets = new PresetStore();

        private bool m_Open;
        private Entity m_Node;
        private JunctionLayout m_Layout;

        /// <summary>Per movement of the layout: the day's peak and the recent vehicles per hour; -1 where not measured.</summary>
        private float[] m_Peak;
        private float[] m_Recent;

        private PlannerDraft m_Draft;

        /// <summary>The draft as the junction runs it: what Discard returns to, and what tells a draft with changes.</summary>
        private PlannerDraft m_Applied;
        private readonly List<PlannerDraft> m_Undo = new List<PlannerDraft>();
        private readonly List<PlannerDraft> m_Redo = new List<PlannerDraft>();

        private int m_Selected;

        /// <summary>The draft phase held at green from the planner, or -1.</summary>
        private int m_Hold = -1;

        /// <summary>The movement under the pointer in the planner's diagram, drawn on the road; -1 for none.</summary>
        private int m_Hovered = -1;

        private Refusal m_Refusal;
        private string m_Message;
        private string m_MessageArgument;
        private DateTime m_MessageTime;
        private DateTime m_RefreshTime;

        private bool m_TemplateScramble;
        private List<TemplateRow> m_Templates;
        private string m_TemplatesLanes;
        /// <summary>The preset list as last written, and the store's version it was made from.</summary>
        private List<PresetRow> m_PresetRows;
        private int m_PresetVersion = -1;

        /// <summary>The preset last applied, to turn it round to its next fit.</summary>
        private string m_PresetId;
        private int m_PresetRotation;

        /// <summary>The running controller, as last read: its phase and stage, mapped to the draft's phases.</summary>
        private JunctionRuntime m_Runtime;
        private bool m_HasRuntime;

        /// <summary>A click the planner refused, with what stood against it.</summary>
        private struct Refusal
        {
            public int Movement;
            public ulong Blocking;
            public uint FitsIn;
            public DateTime Time;
        }

        private struct TemplateRow
        {
            public TemplateKind Kind;
            public List<ulong> Phases;

            /// <summary>The expected mean wait in seconds with the measured traffic, or -1 without traffic.</summary>
            public float Delay;
            public float Saturation;
        }

        private struct PresetRow
        {
            public string Id;
            public string Name;
            public int Phases;

            /// <summary>The ways the preset lies on this junction; 0 where it does not fit.</summary>
            public int Rotations;
            public int Arms;
        }

        /// <summary>The junction the planner edits, or Null while it is closed.</summary>
        public Entity Node => m_Open ? m_Node : Entity.Null;

        /// <summary>The draft's selected phase, for the map (MapOverlaySystem): its movements and those giving way.</summary>
        public ulong PreviewGreen => m_Open && m_Draft != null && m_Selected < m_Draft.Phases.Count ? m_Draft.Phases[m_Selected] : 0UL;

        public ulong PreviewPermitted => m_Open && m_Layout != null ? PhasePlanner.PermittedIn(Model, PreviewGreen) : 0UL;

        /// <summary>The movement under the pointer in the planner, or -1.</summary>
        public int HoveredMovement => m_Open ? m_Hovered : -1;

        /// <summary>The lanes the draft's solid lines cover, for the map; empty while the planner is closed.</summary>
        public List<Entity> SolidLanes
        {
            get
            {
                if (!m_Open || m_Draft == null || m_Layout == null)
                    return m_NoLanes;
                string key = string.Join(",", m_Draft.Solid);
                if (key != m_SolidKey)
                {
                    m_SolidKey = key;
                    m_SolidLanes = new List<Entity>();
                    for (int a = 0; a < m_Draft.Solid.Length && a < m_Layout.Edges.Count; a++)
                    {
                        if (m_Draft.Solid[a] > 0)
                            m_SolidLanes.AddRange(SolidLines.Lanes(EntityManager, SolidLines.Chain(EntityManager, m_Node, m_Layout.Edges[a], m_Draft.Solid[a])));
                    }
                }
                return m_SolidLanes;
            }
        }

        private static readonly List<Entity> m_NoLanes = new List<Entity>();
        private List<Entity> m_SolidLanes = new List<Entity>();
        private string m_SolidKey;

        private bool Dirty => m_Draft != null && !m_Draft.SameAs(m_Applied);

        private bool Live => Mod.Settings != null && Mod.Settings.PlannerLive;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_UI = World.GetOrCreateSystemManaged<TllUISystem>();
            m_CityConfiguration = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>();
            m_Camera = World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
            m_Simulation = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
            AddBinding(m_Binding = new RawValueBinding(kGroup, "planner", Write));
            CreateTriggers();
            if (Mod.Settings != null)
            {
                m_Keys = new[]
                {
                    Mod.Settings.GetAction(Setting.kPlannerUndo),
                    Mod.Settings.GetAction(Setting.kPlannerRedo),
                    Mod.Settings.GetAction(Setting.kPlannerCopy),
                    Mod.Settings.GetAction(Setting.kPlannerPaste),
                    Mod.Settings.GetAction(Setting.kPlannerApply),
                };
            }
        }

        /// <summary>The planner's keys: undo, redo, copy, paste, apply (Setting). Null without settings.</summary>
        private Game.Input.ProxyAction[] m_Keys;

        /// <summary>
        /// The keys act only while the planner is open, and are switched off
        /// otherwise, so Ctrl+Z and the others stay free for the game and
        /// other mods.
        /// </summary>
        private void HandleKeys()
        {
            if (m_Keys == null)
                return;
            foreach (Game.Input.ProxyAction key in m_Keys)
                key.shouldBeEnabled = m_Open;
            if (!m_Open)
                return;
            if (m_Keys[0].WasPerformedThisFrame())
                Undo();
            else if (m_Keys[1].WasPerformedThisFrame())
                Redo();
            else if (m_Keys[2].WasPerformedThisFrame())
                Copy();
            else if (m_Keys[3].WasPerformedThisFrame())
                Edit(Paste);
            else if (m_Keys[4].WasPerformedThisFrame() && !Live)
            {
                Apply();
                Changed();
            }
        }

        protected override void OnUpdate()
        {
            try
            {
                base.OnUpdate();
                HandleKeys();
                if (!m_Open)
                    return;
                if (DateTime.UtcNow - m_RefreshTime < kRefresh)
                    return;
                m_RefreshTime = DateTime.UtcNow;
                Refresh();
            }
            catch (Exception e)
            {
                Mod.Log.Critical(e, "The planner failed and was closed.");
                CloseQuietly();
            }
        }

        // ---- Opening and closing ----

        /// <summary>Opens the planner on the junction selected in the panel, if TLL runs its signals.</summary>
        private void Open()
        {
            Entity node = m_UI.Selected;
            if (!Managed(node))
                return;
            CloseQuietly();
            m_Node = node;
            if (!Load())
            {
                m_Node = Entity.Null;
                return;
            }
            m_Open = true;
            m_Selected = 0;
            m_Hold = -1;
            m_Hovered = -1;
            m_Refusal = default;
            m_Message = null;
            m_PresetId = null;
            m_TemplateScramble = m_Draft.Scramble;
            m_Templates = null;
            m_PresetRows = null;
            m_UI.Select(node, true);
            WriteUser("planner_open", null);
            Changed();
        }

        /// <summary>Closes the planner. A draft not applied is dropped; a phase held is let go.</summary>
        private void Close()
        {
            if (m_Open && Dirty)
                WriteUser("planner_discard", null);
            CloseQuietly();
            Changed();
        }

        private void CloseQuietly()
        {
            SetHold(-1);
            m_Open = false;
            m_Node = Entity.Null;
            m_Layout = null;
            m_Draft = null;
            m_Applied = null;
            m_Undo.Clear();
            m_Redo.Clear();
        }

        private bool Managed(Entity node)
        {
            return node != Entity.Null && EntityManager.Exists(node) && EntityManager.HasComponent<ManagedJunction>(node)
                && EntityManager.HasBuffer<JunctionPhase>(node) && EntityManager.HasBuffer<JunctionMovement>(node)
                && !EntityManager.HasComponent<JunctionDormant>(node) && !EntityManager.HasComponent<Deleted>(node);
        }

        /// <summary>
        /// Reads the junction: its layout, traffic and plan. The plan's
        /// phases refer to the movements as the junction stored them; they
        /// are matched to the layout by road and kind, which is the same
        /// order unless the roads changed since.
        /// </summary>
        /// <returns>Whether the junction could be read.</returns>
        private bool Load()
        {
            m_Layout = JunctionAnalysis.Analyse(EntityManager, m_Node, m_CityConfiguration.leftHandTraffic);
            if (m_Layout == null)
                return false;
            LoadLanes();
            ReadTraffic();
            m_Draft = FromJunction();
            m_Applied = m_Draft.Clone();
            m_Undo.Clear();
            m_Redo.Clear();
            return true;
        }

        private void ReadTraffic()
        {
            int n = m_Layout.Keys.Count;
            m_Peak = new float[n];
            m_Recent = new float[n];
            int[] toLayout = StoredToLayout();
            bool measured = EntityManager.HasBuffer<MovementStatistics>(m_Node);
            DynamicBuffer<MovementStatistics> statistics = measured ? EntityManager.GetBuffer<MovementStatistics>(m_Node, true) : default;
            for (int i = 0; i < n; i++)
            {
                m_Peak[i] = -1f;
                m_Recent[i] = -1f;
            }
            for (int i = 0; measured && i < statistics.Length && i < toLayout.Length; i++)
            {
                int m = toLayout[i];
                MovementStatistics s = statistics[i];
                if (m < 0 || (s.Recent == 0f && s.Daily == 0f && s.Peak == 0f))
                    continue;
                m_Peak[m] = s.Peak;
                m_Recent[m] = s.Recent;
            }
        }

        /// <summary>For each movement as the junction stores it, its index in the planner's layout, or -1.</summary>
        private int[] StoredToLayout()
        {
            DynamicBuffer<JunctionMovement> stored = EntityManager.GetBuffer<JunctionMovement>(m_Node, true);
            var map = new int[stored.Length];
            for (int i = 0; i < stored.Length; i++)
            {
                map[i] = -1;
                for (int m = 0; m < m_Layout.Keys.Count; m++)
                {
                    MovementKey key = m_Layout.Keys[m];
                    Entity source = m_Layout.Edges[key.Source];
                    Entity target = key.Target >= 0 ? m_Layout.Edges[key.Target] : Entity.Null;
                    if (stored[i].Source == source && stored[i].Target == target && stored[i].Kind == key.Kind)
                    {
                        map[i] = m;
                        break;
                    }
                }
            }
            return map;
        }

        /// <summary>Whether the movements the junction stores are the layout's, in its order.</summary>
        private bool SameMovements()
        {
            int[] map = StoredToLayout();
            if (map.Length != m_Layout.Keys.Count)
                return false;
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] != i)
                    return false;
            }
            return true;
        }

        /// <summary>The junction's plan and settings as a draft.</summary>
        private PlannerDraft FromJunction()
        {
            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(m_Node);
            var draft = new PlannerDraft
            {
                Owner = junction.Origin == JunctionOrigin.Auto ? PlannerOwner.Autopilot
                    : (junction.Options & JunctionOptions.AutoTiming) != 0 ? PlannerOwner.Layout : PlannerOwner.Everything,
                Mode = junction.Mode,
                TurnOnRed = (junction.Options & JunctionOptions.TurnOnRed) != 0,
                Scramble = (junction.Options & JunctionOptions.Scramble) != 0,
                Yellow = junction.Yellow,
                AllRed = junction.AllRed,
                Prepare = junction.Prepare,
                MaxWait = junction.MaxWait,
                MajorApproach = junction.MajorApproach,
            };
            draft.Solid = new int[m_Layout.Edges.Count];
            if (EntityManager.HasBuffer<SolidLineRule>(m_Node))
            {
                DynamicBuffer<SolidLineRule> rules = EntityManager.GetBuffer<SolidLineRule>(m_Node, true);
                for (int i = 0; i < rules.Length; i++)
                {
                    int approach = m_Layout.Edges.IndexOf(rules[i].Edge);
                    if (approach >= 0)
                        draft.Solid[approach] = rules[i].Pieces;
                }
            }
            int[] toLayout = StoredToLayout();
            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(m_Node, true);
            for (int p = 0; p < phases.Length; p++)
            {
                ulong green = 0UL;
                for (int i = 0; i < toLayout.Length && i < 64; i++)
                {
                    if ((phases[p].Movements & (1UL << i)) != 0UL && toLayout[i] >= 0)
                        green |= 1UL << toLayout[i];
                }
                PhaseData data = phases[p].Data;
                draft.Insert(draft.Phases.Count, green, new PhaseTiming { MinGreen = data.MinGreen, MaxGreen = data.MaxGreen, Green = data.Green });
            }
            return draft;
        }

        /// <summary>
        /// Looks at the junction for what the planner did not do: roads
        /// rebuilt, the optimiser's new times, the autopilot's new layout. A
        /// draft without changes follows the junction; one with changes stays
        /// the player's, unless its movements are gone.
        /// </summary>
        private void Refresh()
        {
            if (!Managed(m_Node))
            {
                CloseQuietly();
                Changed();
                return;
            }
            if (EntityManager.HasComponent<JunctionDirty>(m_Node) || EntityManager.HasComponent<RebuildRequest>(m_Node) || EntityManager.HasComponent<Updated>(m_Node))
                return;
            if (!SameMovements())
            {
                // The roads changed, or a rebuild after Apply numbered the
                // movements anew. A draft with changes goes, since its masks
                // mean other movements now.
                bool hadChanges = Dirty;
                if (!Load())
                {
                    CloseQuietly();
                    Changed();
                    return;
                }
                m_Templates = null;
                m_PresetRows = null;
                m_Selected = Math.Min(m_Selected, m_Draft.Phases.Count - 1);
                if (hadChanges)
                    Say("Reloaded", null);
                Changed();
                return;
            }
            ReadTraffic();
            if (!Dirty)
            {
                // The lanes as they run now are what a lane edit starts from.
                LoadLanes();
                PlannerDraft now = FromJunction();
                if (!now.SameAs(m_Applied))
                {
                    m_Draft = now;
                    m_Applied = now.Clone();
                    m_Selected = Math.Min(m_Selected, m_Draft.Phases.Count - 1);
                }
            }
            if (EntityManager.HasComponent<JunctionRuntime>(m_Node))
            {
                m_Runtime = EntityManager.GetComponentData<JunctionRuntime>(m_Node);
                m_HasRuntime = true;
            }
            KeepHold();
            Changed();
        }

        // ---- Applying ----

        /// <summary>
        /// Writes the draft to the junction and rebuilds it. With the
        /// autopilot as owner, the junction goes back to it instead.
        /// </summary>
        /// <returns>Whether it was applied; not with an error in the draft.</returns>
        private bool Apply()
        {
            if (m_Draft == null || !Dirty)
                return false;
            if (PlanCheck.HasErrors(Check()))
            {
                Say("CannotApply", null);
                return false;
            }
            WriteUser("planner_apply", $"{m_Draft.Phases.Count} phases, owner {m_Draft.Owner}");
            ApplySolidLines();
            bool lanes = ApplyLanes();
            // Solid lines and lanes alone leave the signals as they are; new
            // lanes need the junction rebuilt, solid lines not even that.
            PlannerDraft signals = m_Draft.Clone();
            signals.Solid = (int[])m_Applied.Solid.Clone();
            signals.Lanes.Clear();
            PlannerDraft appliedSignals = m_Applied.Clone();
            appliedSignals.Lanes.Clear();
            if (signals.SameAs(appliedSignals))
            {
                if (lanes)
                    EntityManager.AddComponent<RebuildRequest>(m_Node);
                m_Applied = m_Draft.Clone();
                return true;
            }
            if (m_Draft.Owner == PlannerOwner.Autopilot)
            {
                m_UI.MakeAutomatic(m_Node);
                m_Applied = m_Draft.Clone();
                return true;
            }

            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(m_Node);
            // In a green wave the wave sets the times. With the times left to
            // TLL ("You: layout") and the mode not changed, the junction stays
            // in it, and the next round of the waves plans it with its new
            // phases; otherwise it leaves.
            bool inWave = junction.Mode == ControlMode.Coordinated || junction.Group != 0;
            bool staysInWave = inWave && m_Draft.Owner == PlannerOwner.Layout && m_Draft.Mode == ControlMode.Coordinated;
            if (staysInWave)
            {
                junction.Origin = JunctionOrigin.Manual;
                Requests.RebuildGreenWaves = true;
            }
            else
            {
                TllUISystem.TakeOverByPlayer(ref junction);
                if (m_Draft.Mode != ControlMode.Coordinated)
                    junction.Mode = m_Draft.Mode;
            }
            junction.Options = Set(junction.Options, JunctionOptions.AutoTiming, m_Draft.Owner == PlannerOwner.Layout);
            junction.Options = Set(junction.Options, JunctionOptions.TurnOnRed, m_Draft.TurnOnRed);
            bool scramble = m_Draft.Scramble && CrosswalkPhases(m_Draft.Phases) != 0U;
            junction.Options = Set(junction.Options, JunctionOptions.Scramble, scramble);
            junction.Yellow = m_Draft.Yellow;
            junction.AllRed = m_Draft.AllRed;
            junction.Prepare = m_Draft.Prepare;
            junction.MaxWait = m_Draft.MaxWait;
            junction.MajorApproach = m_Draft.MajorApproach;
            EntityManager.SetComponentData(m_Node, junction);

            // The movements are written in the layout's order, which the
            // draft's masks refer to; the set-up keeps a manual plan whose
            // movements it finds again (JunctionInitSystem.ExistingPlan).
            DynamicBuffer<JunctionMovement> movements = EntityManager.GetBuffer<JunctionMovement>(m_Node);
            movements.Clear();
            foreach (MovementKey key in m_Layout.Keys)
            {
                movements.Add(new JunctionMovement
                {
                    Source = m_Layout.Edges[key.Source],
                    Target = key.Target >= 0 ? m_Layout.Edges[key.Target] : Entity.Null,
                    Kind = key.Kind,
                });
            }
            uint crosswalkPhases = CrosswalkPhases(m_Draft.Phases);
            DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(m_Node);
            // A phase that carried the wave keeps doing so until the wave's
            // next round has planned the new phases.
            var carried = new HashSet<ulong>();
            for (int p = 0; staysInWave && p < phases.Length; p++)
            {
                if (phases[p].Data.HasFlag(PhaseFlags.Coordinated))
                    carried.Add(phases[p].Movements);
            }
            phases.Clear();
            for (int p = 0; p < m_Draft.Phases.Count; p++)
            {
                ulong green = m_Draft.Phases[p];
                PhaseTiming timing = m_Draft.Timing[p];
                PhaseFlags flags = HasCrosswalk(green) ? PhaseFlags.Pedestrian : PhaseFlags.None;
                if (scramble && (crosswalkPhases & (1U << p)) != 0U)
                    flags |= PhaseFlags.Scramble;
                if (carried.Contains(green))
                    flags |= PhaseFlags.Coordinated;
                phases.Add(new JunctionPhase
                {
                    Movements = green,
                    Permitted = PhasePlanner.PermittedIn(Model, green),
                    Data = new PhaseData { MinGreen = timing.MinGreen, MaxGreen = timing.MaxGreen, Green = timing.Green, Flags = flags },
                });
            }
            // Rebuilt as a whole, so the signal poles follow the phases.
            EntityManager.AddComponent<RebuildRequest>(m_Node);
            m_Applied = m_Draft.Clone();
            return true;
        }

        /// <summary>
        /// Writes the draft's solid lines as the junction's rules and puts
        /// the bans on its roads (SolidLines.Sync). They do not change the
        /// junction's movements, so they need no rebuild of it.
        /// </summary>
        private void ApplySolidLines()
        {
            bool any = false;
            foreach (int pieces in m_Draft.Solid)
                any |= pieces > 0;
            if (!any && !EntityManager.HasBuffer<SolidLineRule>(m_Node))
                return;
            DynamicBuffer<SolidLineRule> rules = EntityManager.HasBuffer<SolidLineRule>(m_Node)
                ? EntityManager.GetBuffer<SolidLineRule>(m_Node)
                : EntityManager.AddBuffer<SolidLineRule>(m_Node);
            rules.Clear();
            for (int a = 0; a < m_Draft.Solid.Length && a < m_Layout.Edges.Count; a++)
            {
                if (m_Draft.Solid[a] > 0)
                    rules.Add(new SolidLineRule { Edge = m_Layout.Edges[a], Pieces = (byte)Math.Min(m_Draft.Solid[a], SolidLines.MaxPieces) });
            }
            SolidLines.Sync(EntityManager, m_Node);
        }

        private static JunctionOptions Set(JunctionOptions options, JunctionOptions flag, bool on)
        {
            return on ? options | flag : options & ~flag;
        }

        /// <summary>The phases that give green to crosswalks and nothing else, as a bit mask: where a scramble lets people walk.</summary>
        private uint CrosswalkPhases(List<ulong> phases)
        {
            uint result = 0U;
            for (int p = 0; p < phases.Count && p < 32; p++)
            {
                bool vehicles = false;
                for (int m = 0; m < Model.Movements.Count; m++)
                    vehicles |= (phases[p] & (1UL << m)) != 0UL && !Model.Movements[m].IsPedestrian;
                if (!vehicles && phases[p] != 0UL)
                    result |= 1U << p;
            }
            return result;
        }

        private bool HasCrosswalk(ulong phase)
        {
            for (int m = 0; m < Model.Movements.Count; m++)
            {
                if ((phase & (1UL << m)) != 0UL && Model.Movements[m].IsPedestrian)
                    return true;
            }
            return false;
        }

        /// <summary>The check bar's findings for the draft.</summary>
        private List<Finding> Check()
        {
            float[] volumes = null;
            if (m_Peak != null)
            {
                volumes = new float[m_Peak.Length];
                for (int m = 0; m < volumes.Length; m++)
                    volumes[m] = Math.Max(0f, m_Peak[m]);
            }
            return PlanCheck.Run(Model, m_Draft.Phases, volumes, CycleSeconds(true));
        }

        /// <summary>
        /// The cycle the draft plans, in seconds: in fixed time the greens,
        /// else the longest it can get, every phase at its maximum; each with
        /// the change after it.
        /// </summary>
        private float CycleSeconds(bool longest)
        {
            int intergreen = m_Draft.Yellow + m_Draft.AllRed + m_Draft.Prepare;
            int steps = 0;
            bool fixedTime = m_Draft.Mode == ControlMode.FixedTime || m_Draft.Mode == ControlMode.Coordinated;
            foreach (PhaseTiming t in m_Draft.Timing)
                steps += (fixedTime || !longest ? t.Green : t.MaxGreen) + intergreen;
            return SimTime.ToSeconds(steps);
        }

        // ---- Holding a phase ----

        /// <summary>
        /// Holds a phase of the running plan at green, to watch it on the
        /// road, or lets go with -1. Only a draft phase the junction runs as
        /// it is can be held.
        /// </summary>
        private void SetHold(int phase)
        {
            m_Hold = phase;
            KeepHold();
        }

        /// <summary>
        /// Keeps the controller asked for the held phase: the movements only
        /// that phase has, so no other phase that shares some of them
        /// answers the call.
        /// </summary>
        private void KeepHold()
        {
            if (m_Node == Entity.Null || !EntityManager.Exists(m_Node) || !EntityManager.HasComponent<JunctionRuntime>(m_Node))
                return;
            ulong hold = 0UL;
            if (m_Hold >= 0 && m_Draft != null && m_Hold < m_Draft.Phases.Count && RunningIndex(m_Draft.Phases[m_Hold]) >= 0)
            {
                ulong green = m_Draft.Phases[m_Hold];
                ulong others = 0UL;
                for (int p = 0; p < m_Draft.Phases.Count; p++)
                {
                    if (p != m_Hold)
                        others |= m_Draft.Phases[p];
                }
                hold = (green & ~others) != 0UL ? green & ~others : green;
            }
            else
            {
                m_Hold = -1;
            }
            JunctionRuntime runtime = EntityManager.GetComponentData<JunctionRuntime>(m_Node);
            if (runtime.HoldMovements == hold)
                return;
            runtime.HoldMovements = hold;
            EntityManager.SetComponentData(m_Node, runtime);
        }

        /// <summary>The index in the running plan of the phase giving green to exactly <paramref name="green"/>, or -1.</summary>
        private int RunningIndex(ulong green)
        {
            return m_Applied != null ? m_Applied.Phases.IndexOf(green) : -1;
        }

        // ---- Helpers ----

        private void Changed()
        {
            m_Binding.Update();
        }

        private void Say(string message, string argument)
        {
            m_Message = message;
            m_MessageArgument = argument;
            m_MessageTime = DateTime.UtcNow;
        }

        private void WriteUser(string action, string value)
        {
            MetricsLog.Write(MetricsRecords.Decision(m_Simulation.frameIndex, m_Node, "user")?.Add("action", action).Add("value", value));
        }
    }
}
