import { useValue } from "cs2/api";
import { Button, Panel, Scrollable, Tooltip } from "cs2/ui";
import classNames from "classnames";
import { ReactElement, useState } from "react";
import {
  actions,
  AutopilotInfo,
  ControlMode,
  JunctionInfo,
  panelOpen$,
  PhaseInfo,
  PlanStrategy,
  Problem,
  selected$,
  setPanelOpen,
  SignalAdvice,
  Stage,
  Summary,
  summary$,
  Tab,
  tab$,
  toolActive$,
} from "bindings";
import { JunctionDiagram } from "junction-diagram";
import { useTranslate } from "localization";
import logo from "images/tll.svg";
import styles from "tll-panel.module.scss";

// Layout note: the game's UI engine (Coherent Gameface) lays out every text
// node as its own flex item, so "{a} · {b}" in JSX renders as separate blocks
// under each other. Texts are therefore built as one string, and every row
// states its flex direction. Wrapping flex rows overlap, so choices are laid
// out in fixed rows of two.
//
// Explanations go into tooltips, so the panel itself shows state and
// choices, not paragraphs.

type Translate = ReturnType<typeof useTranslate>;

const modes = [ControlMode.Adaptive, ControlMode.Actuated, ControlMode.FixedTime, ControlMode.Flashing];
const strategies = [PlanStrategy.Permissive, PlanStrategy.ProtectedTurns, PlanStrategy.Split, PlanStrategy.ExclusivePedestrian];

/** Icons that ship with the game. */
const icons = {
  junction: "Media/Game/Icons/Intersections.svg",
  city: "Media/Game/Icons/TrafficLights.svg",
  problems: "Media/Game/Icons/Traffic.svg",
};

/** Queue in vehicles at which a problem spot's bar is full. */
const severeQueue = 15;

const seconds = (s: number) => `${Math.round(s)} s`;
const perHour = (v: number) => `${Math.round(v)}/h`;
/** Time to the next review on the game clock, which is what the player sees tick. */
const nextReview = (junction: JunctionInfo, t: Translate) => `${t("Panel.NextReview", "Next review in")} ${Math.round(junction.reviewMinutes)} min`;

function pairs<T>(items: T[]): T[][] {
  const rows: T[][] = [];
  for (let i = 0; i < items.length; i += 2) rows.push(items.slice(i, i + 2));
  return rows;
}

/**
 * Wraps an element in the game's tooltip, or leaves it alone without text.
 * The tooltip attaches to its one child through a ref, which the game's
 * types express as a type React's own element type does not satisfy.
 */
const Hint = ({ text, children }: { text: string | null; children: ReactElement }) =>
  text ? <Tooltip tooltip={<div className={styles.tooltip}>{text}</div>}>{children as any}</Tooltip> : children;

/**
 * The panel stays mounted while closed, only hidden: the game's draggable
 * panel keeps where the player moved it in its own state, which would be
 * lost with every close. It opens in the top left corner, below the
 * buttons, not in the middle of the screen.
 */
export const TllPanel = () => {
  const open = useValue(panelOpen$);
  const tab = useValue(tab$);
  const summary = useValue(summary$);
  const selected = useValue(selected$);
  const t = useTranslate();

  const tabs: [Tab, string, string][] = [
    ["junction", t("Panel.TabJunction", "Junction"), icons.junction],
    ["city", t("Panel.TabCity", "City"), icons.city],
    [
      "problems",
      summary.problems.length > 0 ? `${t("Panel.TabProblems", "Problems")} ${summary.problems.length}` : t("Panel.TabProblems", "Problems"),
      icons.problems,
    ],
  ];

  return (
    <div className={open ? undefined : styles.hidden}>
      <Panel
        draggable
        initialPosition={{ x: 0, y: 0 }}
        className={styles.panel}
        header={
          <div className={styles.header}>
            <img className={styles.logo} src={logo} />
            <div className={styles.title}>{t("Panel.Title", "Traffic Lights & Lanes")}</div>
          </div>
        }
        onClose={() => setPanelOpen(false)}
      >
        <div className={styles.tabs}>
          {tabs.map(([id, label, icon]) => (
            <Button key={id} variant="flat" className={classNames(styles.tab, tab === id && styles.tabActive)} selected={tab === id} onSelect={() => tab$.update(id)}>
              <img className={styles.tabIcon} src={icon} />
              <div className={styles.tabLabel}>{label}</div>
            </Button>
          ))}
        </div>
        {!summary.available && <UnavailableNote summary={summary} t={t} />}
        <Scrollable className={styles.scroll} vertical>
          {tab === "junction" && <JunctionTab junction={selected} t={t} />}
          {tab === "city" && <CityTab summary={summary} t={t} />}
          {tab === "problems" && <ProblemsTab summary={summary} selected={selected} t={t} />}
        </Scrollable>
      </Panel>
    </div>
  );
};

const UnavailableNote = ({ summary, t }: { summary: Summary; t: Translate }) => (
  <div className={styles.warning}>
    {summary.conflict === "!"
      ? t("Panel.Failed", "TLL ran into an error and gave all traffic lights back to the game. Details are in Logs/TLL.log.")
      : summary.conflict === "?"
      ? t("Panel.ConflictUnknown", "Another mod drives the traffic lights as well. TLL stays off while it is loaded.")
      : summary.conflict
      ? `${summary.conflict}: ${t("Panel.Conflict", "this mod drives the traffic lights as well. TLL stays off while it is loaded.")}`
      : t("Panel.Unavailable", "The game has changed in a way TLL does not recognise. All traffic lights are left to the game.")}
  </div>
);

/** A small rounded label; tone picks its colour. */
const Chip = ({ text, tone, hint }: { text: string; tone?: "green" | "amber" | "blue" | "red" | "grey"; hint?: string }) => (
  <Hint text={hint ?? null}>
    <div className={classNames(styles.chip, tone && styles[`chip_${tone}`])}>{text}</div>
  </Hint>
);

/** An on/off switch in a row, with the label on the left. */
const Switch = ({ label, hint, on, onSelect }: { label: string; hint: string; on: boolean; onSelect: () => void }) => (
  <div className={styles.row}>
    <Hint text={hint || null}>
      <div className={styles.grow}>{label}</div>
    </Hint>
    {/* The pill is its own element: the game's button styles its own
        background, and would paint over the switch's. */}
    <Button variant="flat" className={styles.switchButton} onSelect={onSelect}>
      <div className={classNames(styles.switch, on && styles.switchOn)}>
        <div className={classNames(styles.knob, on && styles.knobOn)} />
      </div>
    </Button>
  </div>
);

/** A row of equal buttons, one of which is selected. */
function Segments<T extends number>({ items, value, label, hint, onSelect }: {
  items: T[];
  value: T;
  label: (item: T) => string;
  hint: (item: T) => string;
  onSelect: (item: T) => void;
}) {
  return (
    <>
      {pairs(items).map((row, i) => (
        <div key={i} className={styles.segments}>
          {row.map((item) => (
            <Hint key={item} text={hint(item) || null}>
              <Button variant="flat" className={classNames(styles.segment, value === item && styles.segmentOn)} selected={value === item} onSelect={() => onSelect(item)}>
                {label(item)}
              </Button>
            </Hint>
          ))}
        </div>
      ))}
    </>
  );
}

// ---- Junction tab ----

const JunctionTab = ({ junction, t }: { junction: JunctionInfo | null; t: Translate }) => {
  const toolActive = useValue(toolActive$);
  return (
    <>
      <div className={styles.row}>
        <Button variant="flat" className={classNames(styles.wide, toolActive && styles.picking)} selected={toolActive} onSelect={actions.toggleTool}>
          {toolActive ? t("Panel.PickingJunction", "Click a junction…") : t("Panel.PickJunction", "Pick a junction on the map")}
        </Button>
      </div>
      {!junction && <div className={styles.empty}>{t("Panel.NothingSelected", "Pick a junction to see and change its signals.")}</div>}
      {junction && (junction.managed ? <ManagedDetail junction={junction} t={t} /> : <UnmanagedDetail junction={junction} t={t} />)}
    </>
  );
};

const UnmanagedDetail = ({ junction, t }: { junction: JunctionInfo; t: Translate }) => (
  <div className={styles.card}>
    <div className={styles.cardTitle}>{junction.name}</div>
    {junction.roundabout ? (
      <div className={styles.muted}>{t("Panel.Roundabout", "A roundabout: traffic entering gives way to traffic in the ring. Traffic lights are not used here.")}</div>
    ) : junction.hasSignals ? (
      <>
        <div className={styles.muted}>{t("Panel.VanillaSignals", "The game controls these traffic lights.")}</div>
        <div className={styles.row}>
          <Button variant="flat" className={styles.wide} onSelect={actions.manage}>
            {t("Panel.Manage", "Control with TLL")}
          </Button>
        </div>
      </>
    ) : (
      <div className={styles.muted}>{t("Panel.NoSignals", "This junction has no traffic lights. Add them with the game's intersection upgrade.")}</div>
    )}
  </div>
);

const modeTone = (mode: ControlMode): "green" | "amber" | "blue" | "grey" =>
  mode === ControlMode.Coordinated ? "blue" : mode === ControlMode.Flashing ? "amber" : mode === ControlMode.FixedTime ? "grey" : "green";

const ManagedDetail = ({ junction, t }: { junction: JunctionInfo; t: Translate }) => (
  <>
    <div className={styles.card}>
      <div className={styles.cardTitle}>{junction.name}</div>
      <div className={styles.chips}>
        <Chip text={junction.manual ? t("Panel.Manual", "Set by you") : t("Panel.Automatic", "Automatic")} tone={junction.manual ? "grey" : "green"} />
        <Chip
          text={junction.mode === ControlMode.Coordinated ? `${t("Mode.Coordinated", "Green wave")} #${junction.group}` : t("Mode." + ControlMode[junction.mode], ControlMode[junction.mode])}
          tone={modeTone(junction.mode)}
          hint={junction.mode === ControlMode.Coordinated ? t("Panel.CoordinatedNote", "") : t("ModeHint." + ControlMode[junction.mode], "")}
        />
        {junction.mode !== ControlMode.Flashing && <Chip text={`${t("Panel.Cycle", "cycle")} ${seconds(junction.cycleSeconds)}`} tone="grey" />}
      </div>
      {junction.mode !== ControlMode.Flashing && <CycleBar junction={junction} t={t} />}
      {junction.manual && (
        <div className={styles.row}>
          <Hint text={t("Panel.MakeAutomaticHint", "")}>
            <Button variant="flat" className={styles.wide} onSelect={actions.makeAutomatic}>
              {t("Panel.MakeAutomatic", "Back to automatic")}
            </Button>
          </Hint>
        </div>
      )}
    </div>

    {junction.autopilot && <AutopilotCard autopilot={junction.autopilot} junction={junction} t={t} />}

    <div className={styles.card}>
      <div className={styles.label}>{t("Panel.Mode", "Control")}</div>
      <Segments
        items={modes}
        value={junction.mode}
        label={(m) => t("Mode." + ControlMode[m], ControlMode[m])}
        hint={(m) => t("ModeHint." + ControlMode[m], "")}
        onSelect={actions.setMode}
      />
      <div className={styles.label}>{t("Panel.Strategy", "Phase layout")}</div>
      <Segments
        items={strategies}
        value={junction.strategy}
        label={(s) => t("Strategy." + PlanStrategy[s], PlanStrategy[s])}
        hint={(s) => t("StrategyHint." + PlanStrategy[s], "")}
        onSelect={actions.setStrategy}
      />
      {junction.strategy !== PlanStrategy.ExclusivePedestrian && (
        <Switch
          label={
            junction.scrambleOnDemand
              ? `${t("Panel.ScrambleOnDemand", "Scramble on demand")}: ${junction.scrambleActive ? t("Panel.ScrambleActive", "active") : t("Panel.ScrambleWaiting", "standby")} (${junction.conflicts}/8)${junction.scrambleActive ? ` · ${nextReview(junction, t)}` : ""}`
              : t("Panel.ScrambleOnDemand", "Scramble on demand")
          }
          hint={t("Panel.ScrambleHint", "")}
          on={junction.scrambleOnDemand}
          onSelect={actions.toggleScramble}
        />
      )}
    </div>

    <div className={styles.card}>
      <div className={styles.label}>{t("Panel.Phases", "Phases")}</div>
      {junction.phases.map((phase, i) => (
        <PhaseRow key={i} index={i} phase={phase} junction={junction} t={t} />
      ))}
    </div>

    <div className={styles.footer}>
      <Hint text={t("Panel.DiagnoseHint", "")}>
        <Button variant="flat" className={styles.footerButton} onSelect={actions.diagnose}>
          {t("Panel.Diagnose", "Write diagnostics to the log")}
        </Button>
      </Hint>
      <Button variant="flat" className={styles.footerButton} onSelect={actions.release}>
        {t("Panel.Release", "Return to the game's control")}
      </Button>
    </div>
  </>
);

/** Colour of a phase's lamp: what drivers of its movements see right now. */
function lampOf(i: number, junction: JunctionInfo): string {
  if (junction.stage === Stage.Flashing) return styles.lampYellow;
  if (junction.stage === Stage.Green) return junction.phase === i ? styles.lampGreen : styles.lampRed;
  if (junction.phase === i && junction.stage === Stage.Yellow) return styles.lampYellow;
  if (junction.next === i && junction.stage === Stage.Prepare) return styles.lampYellow;
  return styles.lampRed;
}

/**
 * The cycle as a bar: one segment per phase, as long as its planned green
 * plus the change after it. The running green fills its segment. In the
 * timed modes, whose schedule is fixed, a cursor shows the position in the
 * cycle; the demand-driven modes have no fixed cycle, only an order.
 */
const CycleBar = ({ junction, t }: { junction: JunctionInfo; t: Translate }) => {
  const ig = junction.intergreenSeconds;
  const lengths = junction.phases.map((p) => Math.max(p.green, p.minGreen, 1) + ig);
  const total = lengths.reduce((a, b) => a + b, 0) || 1;
  const cursor = junction.cyclePosition >= 0 && junction.cycleSeconds > 0 ? junction.cyclePosition / junction.cycleSeconds : -1;
  return (
    <Hint text={t("Panel.CycleHint", "")}>
      <div className={styles.cycle}>
        {junction.phases.map((p, i) => {
          const active = junction.phase === i && junction.stage === Stage.Green;
          const coming = junction.next === i && junction.stage !== Stage.Green;
          const fill = active ? Math.min(1, junction.stageSeconds / Math.max(1, Math.max(p.green, p.minGreen))) : 0;
          return (
            <div
              key={i}
              className={classNames(styles.segmentBar, active && styles.segmentBarActive, coming && styles.segmentBarNext)}
              style={{ width: `${(100 * lengths[i]) / total}%` }}
            >
              {active && <div className={styles.segmentFill} style={{ width: `${100 * fill}%` }} />}
              <div className={styles.segmentLabel}>{`${i + 1}`}</div>
            </div>
          );
        })}
        {cursor >= 0 && <div className={styles.cursor} style={{ left: `${100 * cursor}%` }} />}
      </div>
    </Hint>
  );
};

/** Short name of a layout for the chart, which has less room than the choice buttons. */
const strategyShort = (s: PlanStrategy, t: Translate) => t("StrategyShort." + PlanStrategy[s], t("Strategy." + PlanStrategy[s], PlanStrategy[s]));

/**
 * The autopilot's comparison of the phase layouts: the expected mean wait
 * per layout as a bar, the current one marked, overloaded ones in red. The
 * estimates come from the busiest hours of the last days, so they change
 * slowly on purpose.
 */
const AutopilotCard = ({ autopilot, junction, t }: { autopilot: AutopilotInfo; junction: JunctionInfo; t: Translate }) => {
  const estimates = autopilot.estimates;
  const worst = Math.max(1, ...estimates.map((e) => e.delay));
  const traffic = `${t("Panel.MainRoad", "Main road")} ${perHour(autopilot.majorVolume)} · ${t("Panel.SideRoad", "side road")} ${perHour(autopilot.minorVolume)}`;
  return (
    <div className={styles.card}>
      <div className={styles.cardHead}>
        <Hint text={t("Panel.EstimateHint", "")}>
          <div className={styles.label}>{t("Panel.Autopilot", "Autopilot")}</div>
        </Hint>
        {estimates.length > 0 && <div className={styles.faint}>{traffic}</div>}
      </div>
      {estimates.length === 0 ? (
        <Hint text={t("Panel.CollectingHint", "")}>
          <div className={styles.muted}>
            {autopilot.tooQuiet ? t("Panel.TooQuiet", "too little traffic to compare layouts") : t("Panel.Collecting", "still measuring the traffic")}
          </div>
        </Hint>
      ) : (
        estimates.map((e) => {
          const current = e.strategy === junction.strategy;
          const pending = autopilot.pending === e.strategy && !current;
          return (
            <div key={e.strategy} className={styles.chartRow}>
              <div className={classNames(styles.chartLabel, current && styles.chartLabelCurrent)}>{strategyShort(e.strategy, t)}</div>
              <div className={styles.chartTrack}>
                <div
                  className={classNames(styles.chartBar, current && styles.chartBarCurrent, pending && styles.chartBarPending, e.saturation > 1 && styles.chartBarOver)}
                  style={{ width: `${Math.max(3, (100 * Math.min(e.delay, worst)) / worst)}%` }}
                />
              </div>
              <div className={classNames(styles.chartValue, e.saturation > 1 && styles.over)}>{`Ø ${seconds(e.delay)}`}</div>
            </div>
          );
        })
      )}
      {autopilot.pending >= 0 && autopilot.pending !== junction.strategy && (
        <div className={styles.note}>{`${t("Panel.PendingLayout", "Next review changes to")}: ${t("Strategy." + PlanStrategy[autopilot.pending], PlanStrategy[autopilot.pending])}`}</div>
      )}
      <div className={styles.faint}>{nextReview(junction, t)}</div>
      {autopilot.signalAdvice === SignalAdvice.RemoveSignals && (
        <div className={styles.note}>{t("Panel.RemoveSignals", "Priority rules would mean less waiting here than signals.")}</div>
      )}
    </div>
  );
};

/**
 * One phase as a compact row: lamp, a small plan of what goes, and its
 * timing. The waiting figures are in the tooltip.
 */
const PhaseRow = ({ index, phase, junction, t }: { index: number; phase: PhaseInfo; junction: JunctionInfo; t: Translate }) => {
  const active = junction.phase === index && junction.stage === Stage.Green;
  const timing = active
    ? `${seconds(junction.stageSeconds)} ${t("Panel.Of", "of")} ${Math.round(phase.minGreen)}–${seconds(phase.maxGreen)}`
    : `${Math.round(phase.minGreen)}–${seconds(phase.maxGreen)}`;
  const walk =
    phase.walkGreen > 0
      ? active && junction.walk
        ? t("Panel.Walking", "Pedestrians walk")
        : `${t("Panel.WalkOnCall", "Crosswalk on request")} · ${seconds(phase.walkGreen)}`
      : null;
  const details = `${t("Panel.Waiting", "waiting")}: ${phase.demand.toFixed(0)} · ${seconds(phase.wait)}`;
  return (
    <Hint text={details}>
      <div className={classNames(styles.phase, active && styles.phaseActive)}>
        <div className={classNames(styles.lamp, lampOf(index, junction))} />
        <JunctionDiagram
          approaches={junction.approaches}
          movements={junction.movements}
          green={phase.movements}
          permitted={phase.permitted}
          leftHandTraffic={junction.leftHandTraffic}
          cameraYaw={junction.cameraYaw}
          className={styles.phaseDiagram}
        />
        <div className={styles.phaseInfo}>
          <div className={styles.phaseTitle}>{`${t("Panel.Phase", "Phase")} ${index + 1}`}</div>
          <div className={styles.phaseLine}>{timing}</div>
          {walk && <div className={classNames(styles.phaseLine, active && junction.walk && styles.walking)}>{walk}</div>}
          {phase.preempt && <div className={styles.preempt}>{t("Panel.Emergency", "Emergency")}</div>}
        </div>
      </div>
    </Hint>
  );
};

// ---- City tab ----

const Tile = ({ value, label, tone }: { value: string; label: string; tone?: "green" | "amber" | "blue" | "red" }) => (
  <div className={styles.tile}>
    <div className={classNames(styles.tileValue, tone && styles[`tile_${tone}`])}>{value}</div>
    <div className={styles.tileLabel}>{label}</div>
  </div>
);

/** Resets every junction to automatic; the first click asks, the second does it. */
const ResetAll = ({ t }: { t: Translate }) => {
  const [asking, setAsking] = useState(false);
  return (
    <div className={styles.row}>
      {asking ? (
        <>
          <div className={styles.grow}>{t("Panel.ResetAllQuestion", "Your own settings are lost.")}</div>
          <Button variant="flat" className={styles.small} onSelect={() => { actions.resetAllToAutomatic(); setAsking(false); }}>
            {t("Panel.ResetAllConfirm", "Reset")}
          </Button>
          <Button variant="flat" className={styles.small} onSelect={() => setAsking(false)}>
            {t("Panel.Cancel", "Cancel")}
          </Button>
        </>
      ) : (
        <Hint text={t("Settings.ResetAllToAutomatic.Description", "")}>
          <Button variant="flat" className={styles.wide} onSelect={() => setAsking(true)}>
            {t("Settings.ResetAllToAutomatic.Label", "Reset all junctions to automatic")}
          </Button>
        </Hint>
      )}
    </div>
  );
};

const CityTab = ({ summary, t }: { summary: Summary; t: Translate }) => {
  const flashing = summary.byMode[ControlMode.Flashing] ?? 0;
  return (
    <>
      <div className={styles.tiles}>
        <Tile value={`${summary.managed}`} label={t("Panel.TileManaged", "junctions controlled")} tone="green" />
        <Tile value={`${summary.coordinated}`} label={`${t("Panel.TileWaves", "in green waves")} (${summary.greenWaves})`} tone="blue" />
      </div>
      <div className={styles.tiles}>
        <Tile value={`${flashing}`} label={t("Panel.TileFlashing", "flashing yellow")} tone="amber" />
        <Tile value={`${summary.problems.length}`} label={t("Panel.TileProblems", "problem spots")} tone={summary.problems.length > 0 ? "red" : undefined} />
      </div>

      <div className={styles.card}>
        <Switch
          label={t("Panel.Automation", "Manage all traffic lights")}
          hint={t("Settings.AutoManageAll.Description", "")}
          on={summary.automation}
          onSelect={actions.toggleAutomation}
        />
        <Switch
          label={t("Settings.ShowProblems.Label", "Mark problem junctions")}
          hint={t("Settings.ShowProblems.Description", "")}
          on={summary.showProblems}
          onSelect={actions.toggleShowProblems}
        />
        <Switch
          label={t("Settings.ShowCongestion.Label", "Mark congestion")}
          hint={t("Settings.ShowCongestion.Description", "")}
          on={summary.showCongestion}
          onSelect={actions.toggleShowCongestion}
        />
      </div>

      <div className={styles.card}>
        <div className={styles.row}>
          <Hint text={t("Settings.AutoGreenWaves.Description", "")}>
            <Button variant="flat" className={styles.wide} onSelect={actions.rebuildGreenWaves}>
              {t("Panel.RebuildGreenWavesLong", "Recalculate green waves")}
            </Button>
          </Hint>
        </div>
        <ResetAll t={t} />
      </div>
    </>
  );
};

// ---- Problems tab ----

const ProblemsTab = ({ summary, selected, t }: { summary: Summary; selected: JunctionInfo | null; t: Translate }) => (
  <>
    <Hint text={t("Panel.ProblemsHint", "")}>
      <div className={styles.label}>{t("Panel.ProblemsTitle", "Queues in the rush hour")}</div>
    </Hint>
    {summary.problems.length === 0 ? (
      <div className={styles.empty}>{t("Panel.NoProblems", "No junction with long queues measured so far.")}</div>
    ) : (
      summary.problems.map((p) => <ProblemRow key={p.index} problem={p} t={t} selected={selected?.index === p.index} />)
    )}
  </>
);

/** One problem spot: name, a bar for how long the queue is, and the figure. Hovering marks it on the map. */
const ProblemRow = ({ problem, t, selected }: { problem: Problem; t: Translate; selected: boolean }) => {
  const share = Math.min(1, problem.queue / severeQueue);
  return (
    <div onMouseEnter={() => actions.hover(problem)} onMouseLeave={actions.unhover}>
      <Button
        variant="flat"
        className={classNames(styles.problem, selected && styles.selected)}
        onSelect={() => {
          actions.goto(problem);
          tab$.update("junction");
        }}
      >
        <div className={styles.problemTop}>
          <div className={styles.problemName}>{problem.name}</div>
          <div className={styles.problemStats}>{`Ø ${Math.round(problem.queue)} ${t("Panel.Vehicles", "vehicles")}`}</div>
        </div>
        <div className={styles.severityTrack}>
          <div className={classNames(styles.severityBar, share >= 0.66 ? styles.severityHigh : share >= 0.33 ? styles.severityMid : styles.severityLow)} style={{ width: `${Math.max(4, 100 * share)}%` }} />
        </div>
      </Button>
    </div>
  );
};
