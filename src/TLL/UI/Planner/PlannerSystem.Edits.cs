using System;
using System.Collections.Generic;
using Colossal.UI.Binding;
using TLL.Components;
using TLL.Core;
using TLL.Core.Control;
using TLL.Core.Planning;
using TLL.Systems;
using Unity.Entities;

namespace TLL.UI.Planner
{
    public partial class PlannerSystem
    {
        /// <summary>Most steps Undo goes back.</summary>
        private const int kUndoDepth = 100;

        private void CreateTriggers()
        {
            AddBinding(new TriggerBinding(kGroup, "plannerOpen", Open));
            AddBinding(new TriggerBinding(kGroup, "plannerClose", Close));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerSelectPhase", phase =>
            {
                if (m_Draft != null && phase >= 0 && phase < m_Draft.Phases.Count)
                    m_Selected = phase;
                Changed();
            }));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerToggle", movement => Edit(() => Toggle(m_Selected, movement))));
            AddBinding(new TriggerBinding<int, int>(kGroup, "plannerAddTo", (movement, phase) => Edit(() => AddTo(movement, phase))));
            AddBinding(new TriggerBinding<int, int>(kGroup, "plannerRemoveFrom", (movement, phase) => Edit(() => RemoveFrom(movement, phase))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerPlace", movement => Edit(() => Place(movement))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerJoinLane", movement => Edit(() => JoinLane(movement))));
            AddBinding(new TriggerBinding<int, int>(kGroup, "plannerProtect", (movement, phase) => Edit(() => Protect(movement, phase))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerNewPhaseWith", movement => Edit(() => NewPhaseWith(movement))));
            AddBinding(new TriggerBinding(kGroup, "plannerAddPhase", () => Edit(AddPhase)));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerDuplicatePhase", phase => Edit(() => DuplicatePhase(phase))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerDeletePhase", phase => Edit(() => DeletePhase(phase))));
            AddBinding(new TriggerBinding<int, int>(kGroup, "plannerMovePhase", (from, to) => Edit(() => MovePhase(from, to))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerFillUp", phase => Edit(() => FillUp(phase))));
            AddBinding(new TriggerBinding<int, float, float, float>(kGroup, "plannerSetTiming", (phase, min, max, green) => Edit(() => SetTiming(phase, min, max, green))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerSetMode", mode => Edit(() => SetMode((ControlMode)mode))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerSetOwner", owner => Edit(() => SetOwner((PlannerOwner)owner), takesOver: false)));
            AddBinding(new TriggerBinding<bool>(kGroup, "plannerSetTurnOnRed", on => Edit(() => Change(ref m_Draft.TurnOnRed, on))));
            AddBinding(new TriggerBinding<bool>(kGroup, "plannerSetScramble", on => Edit(() => Change(ref m_Draft.Scramble, on))));
            AddBinding(new TriggerBinding<float, float, float>(kGroup, "plannerSetIntergreen", (yellow, allRed, prepare) => Edit(() => SetIntergreen(yellow, allRed, prepare))));
            AddBinding(new TriggerBinding<float>(kGroup, "plannerSetMaxWait", seconds => Edit(() => SetMaxWait(seconds))));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerSetMain", approach => Edit(() => SetMain(approach))));
            AddBinding(new TriggerBinding(kGroup, "plannerApply", () =>
            {
                if (m_Open)
                    Apply();
                Changed();
            }));
            AddBinding(new TriggerBinding(kGroup, "plannerDiscard", Discard));
            AddBinding(new TriggerBinding(kGroup, "plannerUndo", Undo));
            AddBinding(new TriggerBinding(kGroup, "plannerRedo", Redo));
            AddBinding(new TriggerBinding<bool>(kGroup, "plannerSetLive", SetLive));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerHold", phase =>
            {
                SetHold(phase);
                Changed();
            }));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerHover", movement =>
            {
                m_Hovered = movement;
            }));
            AddBinding(new TriggerBinding<int>(kGroup, "plannerTemplate", kind => Edit(() => LoadTemplate((TemplateKind)kind))));
            AddBinding(new TriggerBinding<bool>(kGroup, "plannerTemplateScramble", on =>
            {
                m_TemplateScramble = on;
                m_Templates = null;
                Changed();
            }));
            AddBinding(new TriggerBinding<string, bool>(kGroup, "plannerSavePreset", SavePreset));
            AddBinding(new TriggerBinding<string>(kGroup, "plannerApplyPreset", id => Edit(() => ApplyPreset(id, next: false))));
            AddBinding(new TriggerBinding(kGroup, "plannerTurnPreset", () => Edit(() => ApplyPreset(m_PresetId, next: true))));
            AddBinding(new TriggerBinding<string>(kGroup, "plannerDeletePreset", DeletePreset));
            AddBinding(new TriggerBinding<string>(kGroup, "plannerSharePreset", SharePreset));
            AddBinding(new TriggerBinding(kGroup, "plannerCopy", Copy));
            AddBinding(new TriggerBinding(kGroup, "plannerPaste", () => Edit(Paste)));
            AddBinding(new TriggerBinding(kGroup, "plannerTourSeen", () =>
            {
                if (Mod.Settings != null && !Mod.Settings.PlannerTourSeen)
                {
                    Mod.Settings.PlannerTourSeen = true;
                    Mod.Settings.ApplyAndSave();
                }
                Changed();
            }));
        }

        /// <summary>
        /// One edit of the draft: undoable, applied at once with Live changes
        /// on, and, except where the player picks who decides, taking the
        /// junction over from the autopilot.
        /// </summary>
        /// <param name="change">Called as <c>change()</c>. Changes the draft; returns false where nothing changed.</param>
        private void Edit(Func<bool> change, bool takesOver = true)
        {
            if (!m_Open || m_Draft == null)
                return;
            PlannerDraft before = m_Draft.Clone();
            if (!change() || m_Draft.SameAs(before))
            {
                Changed();
                return;
            }
            if (takesOver && m_Draft.Owner == PlannerOwner.Autopilot)
                m_Draft.Owner = PlannerOwner.Layout;
            m_Draft.FillTiming(m_Layout.Model);
            m_Undo.Add(before);
            if (m_Undo.Count > kUndoDepth)
                m_Undo.RemoveAt(0);
            m_Redo.Clear();
            m_Refusal = default;
            m_Selected = Math.Max(0, Math.Min(m_Selected, m_Draft.Phases.Count - 1));
            if (Live)
                Apply();
            KeepHold();
            Changed();
        }

        private bool Toggle(int phase, int movement)
        {
            if (!ValidMovement(movement) || phase < 0 || phase >= m_Draft.Phases.Count)
                return false;
            ToggleResult result = PlanEditing.Toggle(m_Layout.Model, m_Draft.Phases, phase, movement);
            if (!result.Changed)
            {
                m_Refusal = new Refusal
                {
                    Movement = movement,
                    Blocking = result.Blocking,
                    FitsIn = PlanEditing.FitsIn(m_Layout.Model, m_Draft.Phases, movement),
                    Time = DateTime.UtcNow,
                };
                return false;
            }
            return true;
        }

        private bool AddTo(int movement, int phase)
        {
            if (!ValidMovement(movement) || phase < 0 || phase >= m_Draft.Phases.Count || (m_Draft.Phases[phase] & (1UL << movement)) != 0UL)
                return false;
            m_Selected = phase;
            return Toggle(phase, movement);
        }

        private bool RemoveFrom(int movement, int phase)
        {
            if (!ValidMovement(movement) || phase < 0 || phase >= m_Draft.Phases.Count || (m_Draft.Phases[phase] & (1UL << movement)) == 0UL)
                return false;
            return Toggle(phase, movement);
        }

        private bool Place(int movement)
        {
            if (!ValidMovement(movement))
                return false;
            uint into = PlanEditing.Place(m_Layout.Model, m_Draft.Phases, movement);
            if (into == 0U)
            {
                Say("PlanFull", null);
                return false;
            }
            for (int p = 0; p < 32; p++)
            {
                if ((into & (1U << p)) != 0U)
                {
                    m_Selected = p;
                    break;
                }
            }
            return true;
        }

        /// <summary>
        /// Gives the movements of one approach lane the same greens again:
        /// every phase that has one of them gets them all where they fit and
        /// loses them all where they do not. What is left without green is
        /// placed anew.
        /// </summary>
        private bool JoinLane(int movement)
        {
            if (!ValidMovement(movement))
                return false;
            JunctionModel model = m_Layout.Model;
            ulong group = PlanEditing.Partners(model, movement) | (1UL << movement);
            for (int p = 0; p < m_Draft.Phases.Count; p++)
            {
                ulong inPhase = m_Draft.Phases[p] & group;
                if (inPhase == 0UL || inPhase == group)
                    continue;
                ulong rest = m_Draft.Phases[p] & ~group;
                m_Draft.Phases[p] = PlanEditing.Blocking(model, rest, group) == 0UL ? rest | group : rest;
            }
            bool covered = false;
            foreach (ulong p in m_Draft.Phases)
                covered |= (p & (1UL << movement)) != 0UL;
            if (!covered)
                PlanEditing.Place(model, m_Draft.Phases, movement);
            return true;
        }

        /// <summary>
        /// Gives a turn that gives way in <paramref name="phase"/> a green of
        /// its own there instead: it leaves that phase, with its lane
        /// partners, and goes into another phase where it gives way to
        /// nobody, or into a new one right before, so it leads.
        /// </summary>
        private bool Protect(int movement, int phase)
        {
            if (!ValidMovement(movement) || phase < 0 || phase >= m_Draft.Phases.Count || (m_Draft.Phases[phase] & (1UL << movement)) == 0UL)
                return false;
            JunctionModel model = m_Layout.Model;
            ulong group = PlanEditing.Partners(model, movement) | (1UL << movement);
            for (int p = 0; p < m_Draft.Phases.Count; p++)
            {
                ulong with = m_Draft.Phases[p] | group;
                if (p != phase && PlanEditing.Blocking(model, m_Draft.Phases[p], group) == 0UL && !model.Conflicts.YieldsWithin(movement, with))
                {
                    m_Draft.Phases[phase] &= ~group;
                    m_Draft.Phases[p] = with;
                    m_Selected = p;
                    return true;
                }
            }
            if (m_Draft.Phases.Count >= PhasePlanner.MaxPhases)
            {
                Say("PlanFull", null);
                return false;
            }
            m_Draft.Phases[phase] &= ~group;
            m_Draft.Insert(phase, group, PhaseTiming.Default(model, group));
            m_Selected = phase;
            return true;
        }

        /// <summary>A new phase after the selected one with the movement and its lane partners.</summary>
        private bool NewPhaseWith(int movement)
        {
            if (!ValidMovement(movement) || m_Draft.Phases.Count >= PhasePlanner.MaxPhases)
                return false;
            ulong group = PlanEditing.Partners(m_Layout.Model, movement) | (1UL << movement);
            int at = Math.Min(m_Selected + 1, m_Draft.Phases.Count);
            m_Draft.Insert(at, group, PhaseTiming.Default(m_Layout.Model, group));
            m_Selected = at;
            return true;
        }

        /// <summary>An empty phase after the selected one, selected, to be filled by clicking.</summary>
        private bool AddPhase()
        {
            if (m_Draft.Phases.Count >= PhasePlanner.MaxPhases)
                return false;
            int at = Math.Min(m_Selected + 1, m_Draft.Phases.Count);
            m_Draft.Insert(at, 0UL, PhaseTiming.Default(m_Layout.Model, 0UL));
            m_Selected = at;
            return true;
        }

        private bool DuplicatePhase(int phase)
        {
            if (phase < 0 || phase >= m_Draft.Phases.Count || m_Draft.Phases.Count >= PhasePlanner.MaxPhases)
                return false;
            m_Draft.Insert(phase + 1, m_Draft.Phases[phase], m_Draft.Timing[phase]);
            m_Selected = phase + 1;
            return true;
        }

        /// <summary>Deletes a phase; the last one stays, a junction needs one.</summary>
        private bool DeletePhase(int phase)
        {
            if (phase < 0 || phase >= m_Draft.Phases.Count || m_Draft.Phases.Count <= 1)
                return false;
            m_Draft.RemoveAt(phase);
            if (m_Hold == phase)
                m_Hold = -1;
            m_Selected = Math.Min(m_Selected, m_Draft.Phases.Count - 1);
            return true;
        }

        private bool MovePhase(int from, int to)
        {
            int count = m_Draft.Phases.Count;
            if (from < 0 || from >= count || to < 0 || to >= count || from == to)
                return false;
            ulong green = m_Draft.Phases[from];
            PhaseTiming timing = m_Draft.Timing[from];
            m_Draft.RemoveAt(from);
            m_Draft.Insert(to, green, timing);
            if (m_Selected == from)
                m_Selected = to;
            m_Hold = -1;
            return true;
        }

        private bool FillUp(int phase)
        {
            if (phase < 0 || phase >= m_Draft.Phases.Count)
                return false;
            m_Draft.Phases[phase] = PlanEditing.FillUp(m_Layout.Model, m_Draft.Phases[phase]);
            m_Selected = phase;
            return true;
        }

        /// <summary>
        /// A phase's times, in seconds. The minimum is at least a second and
        /// at most the maximum; in fixed time the green is at least as long
        /// as the crosswalks need, since a fixed schedule cannot stretch it.
        /// </summary>
        private bool SetTiming(int phase, float min, float max, float green)
        {
            if (phase < 0 || phase >= m_Draft.Phases.Count || float.IsNaN(min) || float.IsNaN(max) || float.IsNaN(green))
                return false;
            int minSteps = Math.Max(1, SimTime.ToSteps(Math.Max(1f, min)));
            int maxSteps = Math.Max(minSteps, SimTime.ToSteps(Math.Min(max, 600f)));
            int greenSteps = Math.Max(1, SimTime.ToSteps(Math.Min(green, 600f)));
            if (m_Draft.Mode == ControlMode.FixedTime)
                greenSteps = Math.Max(greenSteps, Walk(m_Draft.Phases[phase]));
            m_Draft.Timing[phase] = new PhaseTiming { MinGreen = (ushort)minSteps, MaxGreen = (ushort)maxSteps, Green = (ushort)greenSteps };
            // Times set by hand are the player's: with the optimiser still
            // fitting them, they would be gone at its next round.
            m_Draft.Owner = PlannerOwner.Everything;
            return true;
        }

        /// <summary>The green a phase needs once people walk in it, in steps (JunctionInitSystem.WalkGreen); 0 without a crosswalk.</summary>
        private int Walk(ulong phase)
        {
            ManagedJunction junction = EntityManager.GetComponentData<ManagedJunction>(m_Node);
            junction.Yellow = m_Draft.Yellow;
            junction.AllRed = m_Draft.AllRed;
            return JunctionInitSystem.WalkGreen(EntityManager, phase, m_Layout.Lanes, m_Layout.Keys, junction);
        }

        private bool SetMode(ControlMode mode)
        {
            if (mode != ControlMode.FixedTime && mode != ControlMode.Actuated && mode != ControlMode.Adaptive && mode != ControlMode.Drain && mode != ControlMode.Flashing)
                return false;
            m_Draft.Mode = mode;
            return true;
        }

        private bool SetOwner(PlannerOwner owner)
        {
            if (owner != PlannerOwner.Autopilot && owner != PlannerOwner.Layout && owner != PlannerOwner.Everything)
                return false;
            m_Draft.Owner = owner;
            return true;
        }

        private static bool Change(ref bool value, bool to)
        {
            bool changed = value != to;
            value = to;
            return changed;
        }

        private bool SetIntergreen(float yellow, float allRed, float prepare)
        {
            if (float.IsNaN(yellow) || float.IsNaN(allRed) || float.IsNaN(prepare))
                return false;
            m_Draft.Yellow = (byte)Math.Max(1, Math.Min(255, SimTime.ToSteps(Math.Max(1f, Math.Min(yellow, 8f)))));
            m_Draft.AllRed = (byte)Math.Max(0, Math.Min(255, SimTime.ToSteps(Math.Max(0f, Math.Min(allRed, 8f)))));
            m_Draft.Prepare = (byte)Math.Max(0, Math.Min(255, SimTime.ToSteps(Math.Max(0f, Math.Min(prepare, 4f)))));
            return true;
        }

        private bool SetMaxWait(float seconds)
        {
            if (float.IsNaN(seconds))
                return false;
            m_Draft.MaxWait = (ushort)Math.Max(1, Math.Min(ushort.MaxValue, SimTime.ToSteps(Math.Max(20f, Math.Min(seconds, 600f)))));
            return true;
        }

        private bool SetMain(int approach)
        {
            if (approach < 0 || approach >= m_Layout.Edges.Count)
                return false;
            m_Draft.MajorApproach = m_Layout.Edges[approach];
            m_Templates = null;
            return true;
        }

        private void Discard()
        {
            if (!m_Open || !Dirty)
                return;
            m_Undo.Add(m_Draft.Clone());
            m_Redo.Clear();
            m_Draft = m_Applied.Clone();
            m_Selected = Math.Min(m_Selected, m_Draft.Phases.Count - 1);
            m_Refusal = default;
            KeepHold();
            Changed();
        }

        private void Undo()
        {
            if (!m_Open || m_Undo.Count == 0)
                return;
            m_Redo.Add(m_Draft);
            m_Draft = m_Undo[m_Undo.Count - 1];
            m_Undo.RemoveAt(m_Undo.Count - 1);
            AfterStep();
        }

        private void Redo()
        {
            if (!m_Open || m_Redo.Count == 0)
                return;
            m_Undo.Add(m_Draft);
            m_Draft = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            AfterStep();
        }

        /// <summary>After Undo or Redo: the selection stays in range, and with Live changes the junction follows.</summary>
        private void AfterStep()
        {
            m_Selected = Math.Max(0, Math.Min(m_Selected, m_Draft.Phases.Count - 1));
            m_Refusal = default;
            if (Live)
                Apply();
            KeepHold();
            Changed();
        }

        private void SetLive(bool on)
        {
            if (Mod.Settings == null)
                return;
            Mod.Settings.PlannerLive = on;
            Mod.Settings.ApplyAndSave();
            if (on && m_Open)
                Apply();
            Changed();
        }

        private bool ValidMovement(int movement)
        {
            return m_Layout != null && movement >= 0 && movement < m_Layout.Model.Movements.Count && movement < 64;
        }

        // ---- Templates ----

        /// <summary>
        /// Loads a template into the draft. A phase the draft already has
        /// keeps its times; the others get the times a new phase starts with.
        /// </summary>
        private bool LoadTemplate(TemplateKind kind)
        {
            TemplatePlan plan = PlanTemplates.Build(m_Layout.Model, kind, MainApproach(), Weights(), m_TemplateScramble);
            if (plan == null)
                return false;
            var timing = new List<PhaseTiming>();
            foreach (ulong green in plan.Phases)
            {
                int same = m_Draft.Phases.IndexOf(green);
                timing.Add(same >= 0 ? m_Draft.Timing[same] : PhaseTiming.Default(m_Layout.Model, green));
            }
            m_Draft.Phases.Clear();
            m_Draft.Timing.Clear();
            m_Draft.Phases.AddRange(plan.Phases);
            m_Draft.Timing.AddRange(timing);
            m_Draft.Scramble = m_TemplateScramble;
            m_Selected = 0;
            m_Hold = -1;
            WriteUser("planner_template", kind.ToString());
            return true;
        }

        /// <summary>An approach of the main road: the draft's choice, else the one with the most lanes (JunctionInitSystem.MajorApproach).</summary>
        private int MainApproach()
        {
            return JunctionInitSystem.MajorApproach(m_Layout.Lanes, m_Layout.Model, m_Layout.Edges, m_Draft.MajorApproach);
        }

        /// <summary>The day's peak per movement, for planning by traffic; null before anything is measured.</summary>
        private float[] Weights()
        {
            if (m_Peak == null)
                return null;
            var weights = new float[m_Peak.Length];
            bool any = false;
            for (int m = 0; m < weights.Length; m++)
            {
                weights[m] = Math.Max(0f, m_Peak[m]);
                any |= weights[m] > 0f;
            }
            return any ? weights : null;
        }

        // ---- Presets ----

        /// <summary>
        /// The draft as a preset: the junction's arms counter-clockwise from
        /// the main road, the draft's phases, and its times if the player
        /// keeps them with it.
        /// </summary>
        private PlanPreset Capture(string name, bool timing)
        {
            int arms = m_Layout.Edges.Count;
            int main = Math.Max(0, MainApproach());
            int[] order = PlanTransfer.CounterClockwise(m_Layout.Angles);
            int start = Array.IndexOf(order, main);
            var armOf = new int[arms];
            var preset = new PlanPreset { Name = name, LeftHandTraffic = m_CityConfiguration.leftHandTraffic, Angles = new float[arms] };
            for (int k = 0; k < arms; k++)
            {
                int approach = order[(start + k) % arms];
                armOf[approach] = k;
                float relative = (m_Layout.Angles[approach] - m_Layout.Angles[main]) % 360f;
                preset.Angles[k] = relative < 0f ? relative + 360f : relative;
            }
            foreach (Movement m in m_Layout.Model.Movements)
                preset.Movements.Add(new Movement(armOf[m.Source], m.Target >= 0 ? armOf[m.Target] : -1, m.Kind));
            uint crosswalkPhases = CrosswalkPhases(m_Draft.Phases);
            for (int p = 0; p < m_Draft.Phases.Count; p++)
            {
                PhaseTiming t = m_Draft.Timing[p];
                preset.Phases.Add(new PresetPhase
                {
                    Green = m_Draft.Phases[p],
                    MinGreen = SimTime.ToSeconds(t.MinGreen),
                    MaxGreen = SimTime.ToSeconds(t.MaxGreen),
                    GreenTime = SimTime.ToSeconds(t.Green),
                    Scramble = m_Draft.Scramble && (crosswalkPhases & (1U << p)) != 0U,
                });
            }
            preset.HasTiming = timing;
            preset.Mode = m_Draft.Mode == ControlMode.Coordinated ? ControlMode.Adaptive : m_Draft.Mode;
            preset.AutoTiming = m_Draft.Owner != PlannerOwner.Everything;
            preset.TurnOnRed = m_Draft.TurnOnRed;
            preset.Scramble = m_Draft.Scramble;
            return preset;
        }

        private void SavePreset(string name, bool timing)
        {
            if (!m_Open)
                return;
            name = string.IsNullOrWhiteSpace(name) ? DefaultPresetName() : name.Trim();
            string id = m_Presets.Save(Capture(name, timing));
            Say(id != null ? "PresetSaved" : "PresetNotSaved", name);
            m_PresetRows = null;
            Changed();
        }

        /// <summary>A preset's name from its junction's shape, e.g. "4 roads, 3 phases".</summary>
        private string DefaultPresetName()
        {
            return $"{m_Layout.Edges.Count} roads, {m_Draft.Phases.Count} phases";
        }

        /// <summary>
        /// Loads a preset into the draft, laid onto the junction the way that
        /// puts its first arm, its main road, on the main road here; with
        /// <paramref name="next"/>, the next way it fits, to turn it round.
        /// </summary>
        private bool ApplyPreset(string id, bool next)
        {
            PresetStore.Entry entry = id != null ? m_Presets.Find(id) : null;
            if (entry == null)
                return false;
            PlanPreset preset = entry.Preset.For(m_CityConfiguration.leftHandTraffic);
            List<int[]> fits = PlanTransfer.Rotations(preset.Angles, m_Layout.Angles);
            if (fits.Count == 0)
            {
                Say("PresetDoesNotFit", preset.Name);
                return false;
            }
            int rotation;
            if (next && id == m_PresetId)
            {
                rotation = (m_PresetRotation + 1) % fits.Count;
            }
            else
            {
                int main = MainApproach();
                rotation = Math.Max(0, fits.FindIndex(map => map[0] == main));
            }
            m_PresetId = id;
            m_PresetRotation = rotation;
            return LoadPreset(preset, fits[rotation], fits.Count);
        }

        private bool LoadPreset(PlanPreset preset, int[] map, int fits)
        {
            TransferResult result = PlanTransfer.Transfer(preset.Movements, preset.Greens(), map, m_Layout.Model);
            if (result.Unplaced != 0UL)
            {
                Say("PresetDoesNotFit", preset.Name);
                return false;
            }
            m_Draft.Phases.Clear();
            m_Draft.Timing.Clear();
            for (int p = 0; p < result.Phases.Count; p++)
            {
                int origin = result.Origin[p];
                PhaseTiming timing = PhaseTiming.Default(m_Layout.Model, result.Phases[p]);
                if (preset.HasTiming && origin >= 0)
                {
                    PresetPhase source = preset.Phases[origin];
                    timing = new PhaseTiming
                    {
                        MinGreen = (ushort)Math.Max(1, SimTime.ToSteps(source.MinGreen)),
                        MaxGreen = (ushort)Math.Max(1, SimTime.ToSteps(source.MaxGreen)),
                        Green = (ushort)Math.Max(1, SimTime.ToSteps(source.GreenTime)),
                    };
                }
                m_Draft.Insert(p, result.Phases[p], timing);
            }
            if (preset.HasTiming)
            {
                m_Draft.Mode = preset.Mode;
                m_Draft.Owner = preset.AutoTiming ? PlannerOwner.Layout : PlannerOwner.Everything;
                m_Draft.TurnOnRed = preset.TurnOnRed;
                m_Draft.Scramble = preset.Scramble;
            }
            m_Selected = 0;
            m_Hold = -1;
            int added = PlanEditing.Count(result.Added);
            int dropped = result.Dropped.Count;
            Say(added + dropped > 0 ? "PresetLoadedWithChanges" : fits > 1 ? "PresetLoadedTurnable" : "PresetLoaded",
                added + dropped > 0 ? $"{added}/{dropped}" : preset.Name);
            WriteUser("planner_preset", preset.Name);
            return true;
        }

        private void DeletePreset(string id)
        {
            if (id != null && m_Presets.Delete(id))
            {
                if (m_PresetId == id)
                    m_PresetId = null;
                m_PresetRows = null;
            }
            Changed();
        }

        /// <summary>Puts a preset's text on the clipboard, for sharing it in a chat or forum.</summary>
        private void SharePreset(string id)
        {
            PresetStore.Entry entry = id != null ? m_Presets.Find(id) : null;
            if (entry == null)
                return;
            UnityEngine.GUIUtility.systemCopyBuffer = entry.Preset.ToText();
            Say("PresetCopied", entry.Preset.Name);
            Changed();
        }

        /// <summary>Puts the draft on the clipboard as a preset, to paste it at another junction or share it.</summary>
        private void Copy()
        {
            if (!m_Open)
                return;
            UnityEngine.GUIUtility.systemCopyBuffer = Capture($"{m_UI.JunctionNameOf(m_Node)}", true).ToText();
            Say("Copied", null);
            Changed();
        }

        /// <summary>Loads a preset from the clipboard into the draft; it is not applied.</summary>
        private bool Paste()
        {
            PlanPreset preset = PlanPreset.Parse(UnityEngine.GUIUtility.systemCopyBuffer, out string error);
            if (preset == null)
            {
                Say("PasteNothing", error);
                return false;
            }
            preset = preset.For(m_CityConfiguration.leftHandTraffic);
            List<int[]> fits = PlanTransfer.Rotations(preset.Angles, m_Layout.Angles);
            if (fits.Count == 0)
            {
                Say("PresetDoesNotFit", preset.Name);
                return false;
            }
            int main = MainApproach();
            int rotation = Math.Max(0, fits.FindIndex(map => map[0] == main));
            // Saved so "Turn" can go round the other fits, as for a preset
            // from the library.
            m_PresetId = m_Presets.Save(preset);
            m_PresetRotation = rotation;
            m_PresetRows = null;
            return LoadPreset(preset, fits[rotation], fits.Count);
        }
    }
}
