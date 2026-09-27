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

export interface EntityRef {
  index: number;
  version: number;
}

export interface Problem extends EntityRef {
  name: string;
  longestWait: number;
  maxOuts: number;
}

export interface Summary {
  available: boolean;
  automation: boolean;
  managed: number;
  greenWaves: number;
  coordinated: number;
  byMode: number[];
  problems: Problem[];
}

export interface PhaseInfo {
  minGreen: number;
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
  mode: ControlMode;
  strategy: PlanStrategy;
  /** Green wave the junction belongs to, 0 for none. */
  group: number;
  manual: boolean;
  stage: Stage;
  phase: number;
  next: number;
  stageSeconds: number;
  cycleSeconds: number;
  /** One entry per movement: "Kind|source road|target road". */
  movements: string[];
  phases: PhaseInfo[];
}

const emptySummary: Summary = { available: true, automation: false, managed: 0, greenWaves: 0, coordinated: 0, byMode: [], problems: [] };

export const summary$ = bindValue<Summary>(group, "summary", emptySummary);
export const selected$ = bindValue<JunctionInfo | null>(group, "selected", null);
export const toolActive$ = bindValue<boolean>(group, "toolActive", false);

/** Whether the panel is open. Lives only in the UI; the button and the panel share it. */
export const panelOpen$ = bindLocalValue(false);

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
};
