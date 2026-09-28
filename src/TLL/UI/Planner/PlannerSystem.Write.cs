using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using Game.Rendering;
using TLL.Components;
using TLL.Core;
using TLL.Core.Advisor;
using TLL.Core.Control;
using TLL.Core.Planning;
using TLL.Systems;
using Unity.Mathematics;

namespace TLL.UI.Planner
{
    // What the panel gets: the binding "tll.planner". Field names and enum
    // numbers must match src/TLL.UI/src/planner-bindings.ts.
    public partial class PlannerSystem
    {
        private void Write(IJsonWriter writer)
        {
            if (!m_Open || m_Draft == null || m_Layout == null)
            {
                writer.TypeBegin("tll.Planner");
                writer.PropertyName("open");
                writer.Write(false);
                writer.PropertyName("tourSeen");
                writer.Write(Mod.Settings == null || Mod.Settings.PlannerTourSeen);
                writer.TypeEnd();
                return;
            }
            JunctionModel model = Model;
            List<Finding> findings = Check();
            bool errors = PlanCheck.HasErrors(findings);

            writer.TypeBegin("tll.Planner");
            writer.PropertyName("open");
            writer.Write(true);
            writer.PropertyName("tourSeen");
            writer.Write(Mod.Settings == null || Mod.Settings.PlannerTourSeen);
            writer.PropertyName("index");
            writer.Write(m_Node.Index);
            writer.PropertyName("version");
            writer.Write(m_Node.Version);
            writer.PropertyName("name");
            writer.Write(m_UI.JunctionNameOf(m_Node));
            writer.PropertyName("leftHandTraffic");
            writer.Write(model.LeftHandTraffic);
            writer.PropertyName("cameraYaw");
            IGameCameraController camera = m_Camera.activeCameraController;
            writer.Write(camera != null ? camera.rotation.y : 0f);

            int main = MainApproach();
            writer.PropertyName("main");
            writer.Write(main);
            writer.PropertyName("mainRoad");
            int opposite = main >= 0 && main < model.OppositeOf.Length ? model.OppositeOf[main] : -1;
            writer.ArrayBegin(opposite >= 0 ? 2u : main >= 0 ? 1u : 0u);
            if (main >= 0)
                writer.Write(main);
            if (opposite >= 0)
                writer.Write(opposite);
            writer.ArrayEnd();
            writer.PropertyName("approaches");
            writer.ArrayBegin((uint)m_Layout.Edges.Count);
            for (int a = 0; a < m_Layout.Edges.Count; a++)
            {
                float2 d = NetGeometry.Outward(EntityManager, m_Node, m_Layout.Edges[a]);
                writer.TypeBegin("tll.Approach");
                writer.PropertyName("x");
                writer.Write(d.x);
                writer.PropertyName("z");
                writer.Write(d.y);
                writer.PropertyName("name");
                writer.Write(m_UI.RoadNameOf(m_Layout.Edges[a]));
                writer.TypeEnd();
            }
            writer.ArrayEnd();

            writer.PropertyName("movements");
            writer.ArrayBegin((uint)model.Movements.Count);
            for (int m = 0; m < model.Movements.Count; m++)
            {
                Movement mv = model.Movements[m];
                writer.TypeBegin("tll.PlannerMovement");
                writer.PropertyName("kind");
                writer.Write((int)mv.Kind);
                writer.PropertyName("source");
                writer.Write(mv.Source);
                writer.PropertyName("target");
                writer.Write(mv.Target);
                writer.PropertyName("volume");
                writer.Write(m_Recent != null ? m_Recent[m] : -1f);
                writer.PropertyName("peak");
                writer.Write(m_Peak != null ? m_Peak[m] : -1f);
                writer.PropertyName("partners");
                WriteBits(writer, PlanEditing.Partners(model, m));
                writer.TypeEnd();
            }
            writer.ArrayEnd();

            WritePhases(writer);
            WriteSolid(writer);
            WriteLanes(writer);

            writer.PropertyName("selected");
            writer.Write(m_Selected);
            writer.PropertyName("owner");
            writer.Write((int)m_Draft.Owner);
            writer.PropertyName("mode");
            writer.Write((int)m_Draft.Mode);
            writer.PropertyName("turnOnRed");
            writer.Write(m_Draft.TurnOnRed);
            writer.PropertyName("scramble");
            writer.Write(m_Draft.Scramble);
            writer.PropertyName("crosswalkPhases");
            writer.Write(CrosswalkPhases(m_Draft.Phases) != 0U);
            writer.PropertyName("hasCrosswalks");
            writer.Write(PhasePlanner.Crosswalks(model) != 0UL);
            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(m_Node);
            writer.PropertyName("coordinated");
            writer.Write(junction.Mode == ControlMode.Coordinated || junction.Group != 0);
            writer.PropertyName("yellow");
            writer.Write(SimTime.ToSeconds(m_Draft.Yellow));
            writer.PropertyName("allRed");
            writer.Write(SimTime.ToSeconds(m_Draft.AllRed));
            writer.PropertyName("prepare");
            writer.Write(SimTime.ToSeconds(m_Draft.Prepare));
            writer.PropertyName("maxWait");
            writer.Write(SimTime.ToSeconds(m_Draft.MaxWait));
            writer.PropertyName("cycle");
            writer.Write(CycleSeconds(false));
            writer.PropertyName("cycleLongest");
            writer.Write(CycleSeconds(true));

            writer.PropertyName("live");
            writer.Write(Live);
            writer.PropertyName("dirty");
            writer.Write(Dirty);
            writer.PropertyName("canApply");
            writer.Write(Dirty && !errors);
            writer.PropertyName("canUndo");
            writer.Write(m_Undo.Count > 0);
            writer.PropertyName("canRedo");
            writer.Write(m_Redo.Count > 0);

            WriteFindings(writer, findings);
            WriteRefusal(writer);
            WriteMessage(writer);
            WriteTemplates(writer);
            WritePresets(writer);
            writer.TypeEnd();
        }

        private void WritePhases(IJsonWriter writer)
        {
            int running = -1;
            int next = -1;
            Stage stage = Stage.Green;
            float stageSeconds = 0f;
            if (m_HasRuntime && m_Applied != null)
            {
                // The controller counts the phases of the running plan; the
                // draft's phase with the same greens is the one that runs.
                int runningIndex = m_Runtime.State.Phase;
                int nextIndex = m_Runtime.State.Next;
                if (runningIndex < m_Applied.Phases.Count)
                    running = m_Draft.Phases.IndexOf(m_Applied.Phases[runningIndex]);
                if (nextIndex < m_Applied.Phases.Count)
                    next = m_Draft.Phases.IndexOf(m_Applied.Phases[nextIndex]);
                stage = m_Runtime.State.Stage;
                stageSeconds = SimTime.ToSeconds(m_Runtime.State.StageSteps);
            }
            writer.PropertyName("stage");
            writer.Write((int)stage);
            writer.PropertyName("stageSeconds");
            writer.Write(stageSeconds);
            writer.PropertyName("running");
            writer.Write(running);
            writer.PropertyName("next");
            writer.Write(next);
            writer.PropertyName("hold");
            writer.Write(m_Hold);

            // What a click in the selected phase would add without a
            // refusal, so the diagram can show which arrows are free.
            ulong addable = 0UL;
            if (m_Selected >= 0 && m_Selected < m_Draft.Phases.Count)
            {
                ulong selected = m_Draft.Phases[m_Selected];
                for (int m = 0; m < Model.Movements.Count && m < 64; m++)
                {
                    if ((selected & (1UL << m)) == 0UL
                        && PlanEditing.Blocking(Model, selected, PlanEditing.Partners(Model, m) | (1UL << m)) == 0UL)
                        addable |= 1UL << m;
                }
            }
            writer.PropertyName("addable");
            WriteBits(writer, addable);

            DynamicPhaseFigures(out float[] demand, out float[] wait);
            writer.PropertyName("phases");
            writer.ArrayBegin((uint)m_Draft.Phases.Count);
            for (int p = 0; p < m_Draft.Phases.Count; p++)
            {
                ulong green = m_Draft.Phases[p];
                PhaseTiming t = m_Draft.Timing[p];
                int applied = RunningIndex(green);
                writer.TypeBegin("tll.PlannerPhase");
                writer.PropertyName("movements");
                WriteBits(writer, green);
                writer.PropertyName("permitted");
                WriteBits(writer, PhasePlanner.PermittedIn(Model, green));
                writer.PropertyName("minGreen");
                writer.Write(SimTime.ToSeconds(t.MinGreen));
                writer.PropertyName("maxGreen");
                writer.Write(SimTime.ToSeconds(t.MaxGreen));
                writer.PropertyName("green");
                writer.Write(SimTime.ToSeconds(t.Green));
                writer.PropertyName("walk");
                writer.Write(SimTime.ToSeconds(Walk(green)));
                writer.PropertyName("canHold");
                writer.Write(applied >= 0);
                writer.PropertyName("demand");
                writer.Write(applied >= 0 && applied < demand.Length ? demand[applied] : -1f);
                writer.PropertyName("wait");
                writer.Write(applied >= 0 && applied < wait.Length ? wait[applied] : -1f);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        /// <summary>
        /// The solid lines of each road leading in: the draft's pieces, and
        /// how long 1, 2, … pieces are, as far back as the road goes before
        /// the previous junction.
        /// </summary>
        private void WriteSolid(IJsonWriter writer)
        {
            var roads = new List<int>();
            for (int a = 0; a < m_Layout.Edges.Count; a++)
            {
                foreach (Movement m in Model.Movements)
                {
                    if (m.Source == a && !m.IsPedestrian && !roads.Contains(a))
                        roads.Add(a);
                }
            }
            writer.PropertyName("solid");
            writer.ArrayBegin((uint)roads.Count);
            foreach (int a in roads)
            {
                float[] lengths = SolidLines.Lengths(EntityManager, SolidLines.Chain(EntityManager, m_Node, m_Layout.Edges[a], SolidLines.MaxPieces));
                writer.TypeBegin("tll.Solid");
                writer.PropertyName("approach");
                writer.Write(a);
                writer.PropertyName("pieces");
                writer.Write(a < m_Draft.Solid.Length ? m_Draft.Solid[a] : 0);
                writer.PropertyName("lengths");
                writer.ArrayBegin((uint)lengths.Length);
                foreach (float l in lengths)
                    writer.Write(l);
                writer.ArrayEnd();
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        /// <summary>Per phase of the running plan: road users waiting for it, and how long it has waited, in seconds.</summary>
        private void DynamicPhaseFigures(out float[] demand, out float[] wait)
        {
            if (!EntityManager.HasBuffer<JunctionPhase>(m_Node))
            {
                demand = new float[0];
                wait = new float[0];
                return;
            }
            Unity.Entities.DynamicBuffer<JunctionPhase> phases = EntityManager.GetBuffer<JunctionPhase>(m_Node, true);
            demand = new float[phases.Length];
            wait = new float[phases.Length];
            for (int p = 0; p < phases.Length; p++)
            {
                demand[p] = phases[p].Data.Demand;
                wait[p] = SimTime.ToSeconds(phases[p].Data.WaitSteps);
            }
        }

        private static void WriteFindings(IJsonWriter writer, List<Finding> findings)
        {
            writer.PropertyName("findings");
            writer.ArrayBegin((uint)findings.Count);
            foreach (Finding f in findings)
            {
                writer.TypeBegin("tll.Finding");
                writer.PropertyName("kind");
                writer.Write((int)f.Kind);
                writer.PropertyName("severity");
                writer.Write((int)f.Severity);
                writer.PropertyName("phase");
                writer.Write(f.Phase);
                writer.PropertyName("movement");
                writer.Write(f.Movement);
                writer.PropertyName("other");
                writer.Write(f.Other);
                writer.PropertyName("fitsIn");
                WriteBits(writer, f.FitsIn);
                writer.PropertyName("value");
                writer.Write(f.Value);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        private void WriteRefusal(IJsonWriter writer)
        {
            writer.PropertyName("refusal");
            if (m_Refusal.Time == default || DateTime.UtcNow - m_Refusal.Time > kMessageTime)
            {
                writer.WriteNull();
                return;
            }
            writer.TypeBegin("tll.Refusal");
            writer.PropertyName("movement");
            writer.Write(m_Refusal.Movement);
            writer.PropertyName("blocking");
            WriteBits(writer, m_Refusal.Blocking);
            writer.PropertyName("fitsIn");
            WriteBits(writer, m_Refusal.FitsIn);
            writer.TypeEnd();
        }

        private void WriteMessage(IJsonWriter writer)
        {
            writer.PropertyName("message");
            if (m_Message == null || DateTime.UtcNow - m_MessageTime > kMessageTime)
            {
                writer.WriteNull();
                return;
            }
            writer.TypeBegin("tll.PlannerMessage");
            writer.PropertyName("key");
            writer.Write(m_Message);
            writer.PropertyName("argument");
            writer.Write(m_MessageArgument ?? "");
            writer.TypeEnd();
        }

        /// <summary>
        /// The templates for the junction, each drawn from its phases and
        /// with the delay the autopilot's model expects under the measured
        /// traffic. Built once per opening, and again when the main road or
        /// the scramble choice changes.
        /// </summary>
        private void WriteTemplates(IJsonWriter writer)
        {
            // The templates plan with the draft's lanes: a lane of its own
            // lets a turn run apart from the straight traffic.
            string lanes = m_Draft.LanesKey();
            if (m_Templates == null || m_TemplatesLanes != lanes)
            {
                m_Templates = BuildTemplates();
                m_TemplatesLanes = lanes;
            }
            writer.PropertyName("templateScramble");
            writer.Write(m_TemplateScramble);
            writer.PropertyName("templates");
            writer.ArrayBegin((uint)m_Templates.Count);
            foreach (TemplateRow row in m_Templates)
            {
                writer.TypeBegin("tll.Template");
                writer.PropertyName("kind");
                writer.Write((int)row.Kind);
                writer.PropertyName("current");
                writer.Write(SamePhases(row.Phases, m_Draft.Phases));
                writer.PropertyName("delay");
                writer.Write(row.Delay);
                writer.PropertyName("saturation");
                writer.Write(row.Saturation);
                writer.PropertyName("phases");
                writer.ArrayBegin((uint)row.Phases.Count);
                foreach (ulong green in row.Phases)
                {
                    writer.TypeBegin("tll.TemplatePhase");
                    writer.PropertyName("movements");
                    WriteBits(writer, green);
                    writer.PropertyName("permitted");
                    WriteBits(writer, PhasePlanner.PermittedIn(Model, green));
                    writer.TypeEnd();
                }
                writer.ArrayEnd();
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        private List<TemplateRow> BuildTemplates()
        {
            var rows = new List<TemplateRow>();
            float[] weights = Weights();
            foreach (TemplatePlan plan in PlanTemplates.All(Model, MainApproach(), weights, m_TemplateScramble))
            {
                var row = new TemplateRow { Kind = plan.Kind, Phases = plan.Phases, Delay = -1f, Saturation = -1f };
                if (weights != null)
                {
                    var phasePlan = new PhasePlan();
                    foreach (ulong green in plan.Phases)
                        phasePlan.Phases.Add(new Phase { Green = green, Permitted = PhasePlanner.PermittedIn(Model, green) });
                    DelayParameters parameters = DelayParameters.Default;
                    parameters.TurnOnRed = m_Draft.TurnOnRed;
                    PlanEstimate estimate = DelayModel.Estimate(Model, phasePlan, weights, parameters);
                    row.Delay = estimate.AverageDelay;
                    row.Saturation = estimate.WorstSaturation;
                }
                rows.Add(row);
            }
            return rows;
        }

        private static bool SamePhases(List<ulong> a, List<ulong> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int p = 0; p < a.Count; p++)
            {
                if (a[p] != b[p])
                    return false;
            }
            return true;
        }

        /// <summary>The player's presets, those that fit this junction first, each with how many ways it lies on it.</summary>
        private void WritePresets(IJsonWriter writer)
        {
            IReadOnlyList<PresetStore.Entry> entries = m_Presets.All();
            if (m_PresetRows == null || m_PresetVersion != m_Presets.Version)
            {
                m_PresetVersion = m_Presets.Version;
                m_PresetRows = new List<PresetRow>();
                foreach (PresetStore.Entry e in entries)
                {
                    PlanPreset preset = e.Preset.For(Model.LeftHandTraffic);
                    m_PresetRows.Add(new PresetRow
                    {
                        Id = e.Id,
                        Name = preset.Name,
                        Phases = preset.Phases.Count,
                        Arms = preset.Angles.Length,
                        Rotations = PlanTransfer.Rotations(preset.Angles, m_Layout.Angles).Count,
                    });
                }
                m_PresetRows.Sort((a, b) => (b.Rotations > 0).CompareTo(a.Rotations > 0));
            }
            writer.PropertyName("presetTurns");
            int turns = 0;
            foreach (PresetRow row in m_PresetRows)
            {
                if (row.Id == m_PresetId)
                    turns = row.Rotations;
            }
            writer.Write(turns);
            writer.PropertyName("roads");
            writer.Write(m_Layout.Edges.Count);
            writer.PropertyName("presets");
            writer.ArrayBegin((uint)m_PresetRows.Count);
            foreach (PresetRow row in m_PresetRows)
            {
                writer.TypeBegin("tll.Preset");
                writer.PropertyName("id");
                writer.Write(row.Id);
                writer.PropertyName("name");
                writer.Write(row.Name);
                writer.PropertyName("phases");
                writer.Write(row.Phases);
                writer.PropertyName("arms");
                writer.Write(row.Arms);
                writer.PropertyName("fits");
                writer.Write(row.Rotations > 0);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        private static void WriteBits(IJsonWriter writer, ulong bits)
        {
            int count = PlanEditing.Count(bits);
            writer.ArrayBegin((uint)count);
            for (int i = 0; i < 64; i++)
            {
                if ((bits & (1UL << i)) != 0UL)
                    writer.Write(i);
            }
            writer.ArrayEnd();
        }
    }
}
