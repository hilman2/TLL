// The planner's values and triggers, shared with the C# side
// (src/TLL/UI/Planner/PlannerSystem*.cs). Field names and enum numbers must
// match what those classes write.
import { bindValue, trigger } from "cs2/api";
import { Approach, ControlMode, MovementKind, Stage } from "bindings";

const group = "tll";

/** Numbers as in TLL.UI.Planner.PlannerOwner. */
export enum Owner {
  Autopilot = 0,
  Layout = 1,
  Everything = 2,
}

/** Numbers as in TLL.Core.Planning.TemplateKind. */
export enum TemplateKind {
  TurnsGiveWay = 0,
  ProtectedFirst = 1,
  ProtectedLast = 2,
  ProtectedMainRoad = 3,
  EachRoadAlone = 4,
}

/** Numbers as in TLL.Core.Planning.FindingKind. */
export enum FindingKind {
  NoGreen = 0,
  Crossing = 1,
  SplitLane = 2,
  TooManyPhases = 3,
  HeavyYield = 4,
  LongCycle = 5,
  EmptyPhase = 6,
  DuplicatePhase = 7,
}

/** Numbers as in TLL.Core.Planning.Severity. */
export enum Severity {
  Error = 0,
  Warning = 1,
  Info = 2,
}

export interface PlannerMovement {
  kind: MovementKind;
  source: number;
  /** -1 for a crosswalk. */
  target: number;
  /** Recent vehicles (people on a crosswalk) per hour; negative before the first measurement. */
  volume: number;
  /** The day's peak per hour; negative before the first measurement. */
  peak: number;
  /** Movements from the same approach lane: they only ever get green together. */
  partners: number[];
}

export interface PlannerPhase {
  movements: number[];
  permitted: number[];
  /** Seconds. */
  minGreen: number;
  maxGreen: number;
  green: number;
  /** Green the phase needs once people walk in it, seconds; 0 without a crosswalk. */
  walk: number;
  /** The junction runs this phase as it is, so it can be held at green. */
  canHold: boolean;
  /** Road users waiting for it now, and how long it has waited, seconds; -1 where it does not run yet. */
  demand: number;
  wait: number;
}

export interface Finding {
  kind: FindingKind;
  severity: Severity;
  phase: number;
  movement: number;
  other: number;
  /** For NoGreen: the phases it fits into. */
  fitsIn: number[];
  /** For HeavyYield: vehicles per hour given way to. For LongCycle: seconds. */
  value: number;
}

export interface Refusal {
  movement: number;
  /** The movements of the phase it would cross. */
  blocking: number[];
  /** The phases it fits into instead. */
  fitsIn: number[];
}

export interface PlannerMessage {
  key: string;
  argument: string;
}

export interface TemplatePhase {
  movements: number[];
  permitted: number[];
}

export interface Template {
  kind: TemplateKind;
  /** The draft has exactly this plan. */
  current: boolean;
  /** Expected mean wait, seconds, under the measured traffic; -1 without traffic. */
  delay: number;
  saturation: number;
  phases: TemplatePhase[];
}

/** Numbers as in TLL.Core.Planning.LaneEdit: what a click on a lane's direction would do. */
export enum LaneEdit {
  Done = 0,
  LastDirection = 1,
  Gap = 2,
  Cross = 3,
  Uncovered = 4,
  TooMany = 5,
}

export interface LaneUse {
  /** The directions a lane serves: targets[first] to targets[last]. */
  first: number;
  last: number;
}

/** The lanes of one road leading in, from the kerb outwards. */
export interface ApproachLanes {
  approach: number;
  /** The roads the lanes lead into, from the kerb side outwards. */
  targets: number[];
  uses: LaneUse[];
  /** The draft changes these lanes. */
  changed: boolean;
  /** The junction has lane rules for this road now, the player's or the autopilot's. */
  ruled: boolean;
  /** Per lane, per target: what a click would do. */
  options: LaneEdit[][];
  /** The lanes the autopilot would choose for the measured traffic; null where they are these. */
  suggestion: LaneUse[] | null;
  /** The share by which the suggestion lowers the busiest lane's load. */
  gain: number;
}

/** Solid lines on one road leading in. */
export interface Solid {
  approach: number;
  /** Road pieces back from the stop line; 0 for none. */
  pieces: number;
  /** The length of 1, 2, … pieces, metres, as far back as the road goes. */
  lengths: number[];
}

export interface Preset {
  id: string;
  name: string;
  phases: number;
  arms: number;
  fits: boolean;
}

export interface PlannerInfo {
  open: boolean;
  tourSeen: boolean;
  // The fields below are only there while the planner is open.
  index: number;
  version: number;
  name: string;
  leftHandTraffic: boolean;
  cameraYaw: number;
  /** An approach of the main road, and all approaches of the main road (it and the one opposite). */
  main: number;
  mainRoad: number[];
  approaches: Approach[];
  movements: PlannerMovement[];
  stage: Stage;
  stageSeconds: number;
  /** The draft phase running now, the next one, and the one held; -1 for none. */
  running: number;
  next: number;
  hold: number;
  /** Movements a click would add to the selected phase without a refusal. */
  addable: number[];
  phases: PlannerPhase[];
  lanes: ApproachLanes[];
  solid: Solid[];
  selected: number;
  owner: Owner;
  mode: ControlMode;
  turnOnRed: boolean;
  scramble: boolean;
  /** The plan has a phase of crosswalks alone, where a scramble lets people walk. */
  crosswalkPhases: boolean;
  hasCrosswalks: boolean;
  /** The junction runs in a green wave now; an edit takes it out. */
  coordinated: boolean;
  /** Seconds. */
  yellow: number;
  allRed: number;
  prepare: number;
  maxWait: number;
  /** The planned cycle, and the longest it can get with every phase at its maximum; seconds. */
  cycle: number;
  cycleLongest: number;
  live: boolean;
  dirty: boolean;
  canApply: boolean;
  canUndo: boolean;
  canRedo: boolean;
  findings: Finding[];
  refusal: Refusal | null;
  message: PlannerMessage | null;
  templateScramble: boolean;
  templates: Template[];
  /** Ways the preset last loaded lies on the junction; above 1 it can be turned. */
  presetTurns: number;
  /** Roads of this junction, for the presets that need another number. */
  roads: number;
  presets: Preset[];
}

export const planner$ = bindValue<PlannerInfo>(group, "planner", { open: false, tourSeen: true } as PlannerInfo);

export const planner = {
  open: () => trigger(group, "plannerOpen"),
  close: () => trigger(group, "plannerClose"),
  selectPhase: (phase: number) => trigger(group, "plannerSelectPhase", phase),
  toggle: (movement: number) => trigger(group, "plannerToggle", movement),
  addTo: (movement: number, phase: number) => trigger(group, "plannerAddTo", movement, phase),
  removeFrom: (movement: number, phase: number) => trigger(group, "plannerRemoveFrom", movement, phase),
  place: (movement: number) => trigger(group, "plannerPlace", movement),
  joinLane: (movement: number) => trigger(group, "plannerJoinLane", movement),
  protect: (movement: number, phase: number) => trigger(group, "plannerProtect", movement, phase),
  newPhaseWith: (movement: number) => trigger(group, "plannerNewPhaseWith", movement),
  addPhase: () => trigger(group, "plannerAddPhase"),
  duplicatePhase: (phase: number) => trigger(group, "plannerDuplicatePhase", phase),
  deletePhase: (phase: number) => trigger(group, "plannerDeletePhase", phase),
  movePhase: (from: number, to: number) => trigger(group, "plannerMovePhase", from, to),
  fillUp: (phase: number) => trigger(group, "plannerFillUp", phase),
  setTiming: (phase: number, min: number, max: number, green: number) => trigger(group, "plannerSetTiming", phase, min, max, green),
  setMode: (mode: ControlMode) => trigger(group, "plannerSetMode", mode),
  setOwner: (owner: Owner) => trigger(group, "plannerSetOwner", owner),
  setTurnOnRed: (on: boolean) => trigger(group, "plannerSetTurnOnRed", on),
  setScramble: (on: boolean) => trigger(group, "plannerSetScramble", on),
  setIntergreen: (yellow: number, allRed: number, prepare: number) => trigger(group, "plannerSetIntergreen", yellow, allRed, prepare),
  setMaxWait: (seconds: number) => trigger(group, "plannerSetMaxWait", seconds),
  setMain: (approach: number) => trigger(group, "plannerSetMain", approach),
  apply: () => trigger(group, "plannerApply"),
  discard: () => trigger(group, "plannerDiscard"),
  undo: () => trigger(group, "plannerUndo"),
  redo: () => trigger(group, "plannerRedo"),
  setLive: (on: boolean) => trigger(group, "plannerSetLive", on),
  hold: (phase: number) => trigger(group, "plannerHold", phase),
  hover: (movement: number) => trigger(group, "plannerHover", movement),
  template: (kind: TemplateKind) => trigger(group, "plannerTemplate", kind),
  templateScramble: (on: boolean) => trigger(group, "plannerTemplateScramble", on),
  savePreset: (name: string, timing: boolean, lanes: boolean) => trigger(group, "plannerSavePreset", name, timing, lanes),
  toggleLane: (approach: number, lane: number, target: number) => trigger(group, "plannerToggleLane", approach, lane, target),
  useLaneSuggestion: (approach: number) => trigger(group, "plannerUseLaneSuggestion", approach),
  resetLanes: (approach: number) => trigger(group, "plannerResetLanes", approach),
  setSolid: (approach: number, pieces: number) => trigger(group, "plannerSetSolid", approach, pieces),
  applyPreset: (id: string) => trigger(group, "plannerApplyPreset", id),
  turnPreset: () => trigger(group, "plannerTurnPreset"),
  deletePreset: (id: string) => trigger(group, "plannerDeletePreset", id),
  sharePreset: (id: string) => trigger(group, "plannerSharePreset", id),
  copy: () => trigger(group, "plannerCopy"),
  paste: () => trigger(group, "plannerPaste"),
  tourSeen: () => trigger(group, "plannerTourSeen"),
};
