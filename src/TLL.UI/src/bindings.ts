// Values and triggers shared with the C# side (src/TLL/UI/TllUISystem.cs).
// Field names and enum numbers must match what that class writes.
import { bindLocalValue, bindValue, trigger } from "cs2/api";

const group = "tll";

/** Numbers as in TLL.Core.Control.ControlMode. */
export enum ControlMode {
  FixedTime = 0,
  Actuated = 1,
  Adaptive = 2,
  Coordinated = 3,
  Flashing = 4,
}

/** Numbers as in TLL.Core.Control.Stage. */
export enum Stage {
  Green = 0,
  Yellow = 1,
  AllRed = 2,
  Prepare = 3,
  Flashing = 4,
}

/** Numbers as in TLL.Core.Planning.PlanStrategy. */
export enum PlanStrategy {
  Permissive = 0,
  ProtectedTurns = 1,
  Split = 2,
  ExclusivePedestrian = 3,
}

/** Numbers as in TLL.Core.Planning.MovementKind. */
export enum MovementKind {
  Straight = 0,
  Left = 1,
  Right = 2,
  UTurn = 3,
  Pedestrian = 4,
  Track = 5,
}

export interface Approach {
  /** Unit direction from the junction out along the road, world x and z. */
  x: number;
  z: number;
  name: string;
}

export interface Movement {
  kind: MovementKind;
  /** Approach index the traffic comes from; for a crosswalk, the approach it crosses. */
  source: number;
  /** Approach index it leaves by; -1 for a crosswalk. */
  target: number;
  /** Recent vehicles per hour (people on a crosswalk); negative before the first measurement. */
  volume: number;
}

/** Numbers as in TLL.Core.Advisor.SignalAdvice. */
export enum SignalAdvice {
  Keep = 0,
  AddSignals = 1,
  RemoveSignals = 2,
}

export interface Estimate {
  strategy: PlanStrategy;
  /** Expected mean delay per vehicle, seconds. */
  delay: number;
  /** Degree of saturation of the busiest phase; above 1 the queue grows. */
  saturation: number;
}

export interface AutopilotInfo {
  majorVolume: number;
  minorVolume: number;
  signalAdvice: SignalAdvice;
  /** Too little traffic at the peak so far to compare layouts. */
  tooQuiet: boolean;
  /** Layout the autopilot wants to change to at the next review, -1 for none. */
  pending: number;
  /** From the last layout review; empty until there is enough traffic. */
  estimates: Estimate[];
}

export interface EntityRef {
  index: number;
  version: number;
}

export interface Problem extends EntityRef {
  name: string;
  /** Rush-hour queue of the worst movement, vehicles, from the long-term measurement. */
  queue: number;
}

export interface Summary {
  available: boolean;
  /** Name of another traffic light mod TLL stands back for, "?" if unknown, "" for none. */
  conflict: string;
  automation: boolean;
  showProblems: boolean;
  showCongestion: boolean;
  managed: number;
  greenWaves: number;
  coordinated: number;
  byMode: number[];
  problems: Problem[];
}

export interface PhaseInfo {
  minGreen: number;
  /** Green once pedestrians walk, seconds; 0 for a phase without crosswalk. */
  walkGreen: number;
  maxGreen: number;
  green: number;
  demand: number;
  pressure: number;
  wait: number;
  busy: boolean;
  preempt: boolean;
  movements: number[];
  permitted: number[];
}

export interface JunctionInfo extends EntityRef {
  name: string;
  /** False for a junction TLL does not control; the fields below are then meaningless. */
  managed: boolean;
  hasSignals: boolean;
  /** The game's roundabout upgrade; priority rules, not signals, run it. */
  roundabout: boolean;
  mode: ControlMode;
  strategy: PlanStrategy;
  /** Green wave the junction belongs to, 0 for none. */
  group: number;
  manual: boolean;
  stage: Stage;
  phase: number;
  next: number;
  /** The crosswalks of the green phase show walk (someone pressed the button, or fixed time). */
  walk: boolean;
  /** The plan has a scramble phase that runs while turning traffic keeps meeting pedestrians. */
  scrambleOnDemand: boolean;
  /** Pedestrians are diverted into the scramble right now. */
  scrambleActive: boolean;
  /** Vehicle greens with such a clash among the last eight. */
  conflicts: number;
  stageSeconds: number;
  cycleSeconds: number;
  /** Yellow, all red and prepare between two greens, seconds. */
  intergreenSeconds: number;
  /** Seconds into the fixed cycle in the timed modes; -1 when the cycle is not fixed. */
  cyclePosition: number;
  leftHandTraffic: boolean;
  /** Camera heading in degrees, so the diagram can face the way the player looks. */
  cameraYaw: number;
  approaches: Approach[];
  movements: Movement[];
  phases: PhaseInfo[];
  /** Null where the autopilot does not run: manual junctions, or automation off. */
  autopilot: AutopilotInfo | null;
}

const emptySummary: Summary = { available: true, conflict: "", automation: false, showProblems: false, showCongestion: false, managed: 0, greenWaves: 0, coordinated: 0, byMode: [], problems: [] };

export const summary$ = bindValue<Summary>(group, "summary", emptySummary);
export const selected$ = bindValue<JunctionInfo | null>(group, "selected", null);
export const toolActive$ = bindValue<boolean>(group, "toolActive", false);

/**
 * Whether the panel is open. The C# side owns it, so its key binding can
 * open and close the panel; the button and the close box ask through
 * setPanelOpen.
 */
export const panelOpen$ = bindValue<boolean>(group, "panelOpen", false);

export type Tab = "junction" | "city" | "problems";

/** The panel's open tab; kept while the panel is closed. */
export const tab$ = bindLocalValue<Tab>("junction");

export function setPanelOpen(open: boolean) {
  trigger(group, "setPanelOpen", open);
}

export const actions = {
  toggleAutomation: () => trigger(group, "toggleAutomation"),
  select: (e: EntityRef) => trigger(group, "select", e.index, e.version),
  goto: (e: EntityRef) => trigger(group, "goto", e.index, e.version),
  setMode: (mode: ControlMode) => trigger(group, "setMode", mode),
  setStrategy: (strategy: PlanStrategy) => trigger(group, "setStrategy", strategy),
  release: () => trigger(group, "release"),
  manage: () => trigger(group, "manage"),
  toggleTool: () => trigger(group, "toggleTool"),
  rebuildGreenWaves: () => trigger(group, "rebuildGreenWaves"),
  diagnose: () => trigger(group, "diagnose"),
  toggleScramble: () => trigger(group, "toggleScramble"),
  makeAutomatic: () => trigger(group, "makeAutomatic"),
  hover: (e: EntityRef) => trigger(group, "hover", e.index, e.version),
  unhover: () => trigger(group, "unhover"),
  resetAllToAutomatic: () => trigger(group, "resetAllToAutomatic"),
  toggleShowProblems: () => trigger(group, "toggleShowProblems"),
  toggleShowCongestion: () => trigger(group, "toggleShowCongestion"),
};
