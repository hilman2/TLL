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
  Drain = 5,
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

/** Numbers as in TllUISystem.TurnState. */
export enum TurnState {
  Allowed = 0,
  ForbiddenByAutopilot = 1,
  ForbiddenByPlayer = 2,
  AllowedByPlayer = 3,
  /** A road upgrade of the game forbids it; TLL does not change that. */
  ForbiddenByGame = 4,
}

/** One way through a junction, from one road into another, with its rule. */
export interface Turn {
  /** Approach indices, as in JunctionInfo.approaches. */
  source: number;
  target: number;
  kind: MovementKind;
  state: TurnState;
  /** Peak vehicles per hour; for a forbidden turn, what it carried before; negative if not known. */
  volume: number;
}

/** Numbers as in TLL.Components.PrioritySign. */
export enum PrioritySign {
  /** The game's rule: side roads give way to bigger roads, otherwise right before left. */
  Game = 0,
  Priority = 1,
  Yield = 2,
  Stop = 3,
}

/** The sign on one approach of a junction without signals. */
export interface Sign {
  approach: number;
  /** The sign the player chose; Game where the game's rule applies. */
  sign: PrioritySign;
  /** The sign the approach's lanes carry now; Game where they carry none (right before left). */
  showing: PrioritySign;
  /** The autopilot chose the junction's signs from its traffic. */
  auto: boolean;
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
  /** The junction has run this layout and corrected the estimate by what it measured. */
  measured: boolean;
  /** When it ran, queues built up that did not clear. */
  jammed: boolean;
  /** The same for the layout with a pedestrian scramble; a delay of -1 where the junction has no crosswalks. */
  scrambleDelay: number;
  scrambleSaturation: number;
  scrambleMeasured: boolean;
  scrambleJammed: boolean;
}

export interface AutopilotInfo {
  majorVolume: number;
  minorVolume: number;
  signalAdvice: SignalAdvice;
  /** Too little traffic at the peak so far to compare layouts. */
  tooQuiet: boolean;
  /** Layout the autopilot wants to change to at the next review, -1 for none. */
  pending: number;
  /** That layout is to have a pedestrian scramble. */
  pendingScramble: boolean;
  /** Vehicles' mean wait over the last measurement period, and what the model expected, seconds; 0 until measured. */
  measuredWait: number;
  modelledWait: number;
  /** Game minutes the junction stays out of green waves after one did not help; 0 if none. */
  waveBanMinutes: number;
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
  /** Pedestrians have a phase of their own, in all directions, and walk in no other. */
  scramble: boolean;
  /** Short turns may go on red where they only meet traffic they give way to. */
  turnOnRed: boolean;
  /** Of the last eight vehicle greens with turns across a crosswalk, those in which a turning vehicle waited for people. */
  conflicts: number;
  /** Game minutes until the next review, which decides the layout and the scramble. */
  reviewMinutes: number;
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
  /** The ways through the junction and their rules, also where TLL does not run the signals; empty elsewhere. */
  turns: Turn[];
  /** The sign on each approach of a junction without signals; empty elsewhere. */
  signs: Sign[];
  phases: PhaseInfo[];
  /** Null where the autopilot does not run: manual junctions, or automation off. */
  autopilot: AutopilotInfo | null;
}

const emptySummary: Summary = { available: true, conflict: "", automation: false, showProblems: false, showCongestion: false, managed: 0, greenWaves: 0, coordinated: 0, byMode: [], problems: [] };

export const summary$ = bindValue<Summary>(group, "summary", emptySummary);
export const selected$ = bindValue<JunctionInfo | null>(group, "selected", null);
export const toolActive$ = bindValue<boolean>(group, "toolActive", false);
/** The tool for connecting the selected junction's lanes is open. */
export const laneToolActive$ = bindValue<boolean>(group, "laneToolActive", false);

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
  toggleLaneTool: () => trigger(group, "toggleLaneTool"),
  rebuildGreenWaves: () => trigger(group, "rebuildGreenWaves"),
  diagnose: () => trigger(group, "diagnose"),
  toggleScramble: () => trigger(group, "toggleScramble"),
  toggleTurnOnRed: () => trigger(group, "toggleTurnOnRed"),
  /** Next rule for a turn: the autopilot's, forbidden by the player, allowed by the player. */
  cycleTurn: (source: number, target: number) => trigger(group, "cycleTurn", source, target),
  setSign: (approach: number, sign: PrioritySign) => trigger(group, "setSign", approach, sign),
  makeAutomatic: () => trigger(group, "makeAutomatic"),
  hover: (e: EntityRef) => trigger(group, "hover", e.index, e.version),
  unhover: () => trigger(group, "unhover"),
  resetAllToAutomatic: () => trigger(group, "resetAllToAutomatic"),
  toggleShowProblems: () => trigger(group, "toggleShowProblems"),
  toggleShowCongestion: () => trigger(group, "toggleShowCongestion"),
};
