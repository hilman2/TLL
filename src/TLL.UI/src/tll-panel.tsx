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

const seconds = (s: number) => `${Math.round(s)} s`;
const perHour = (v: number) => `${Math.round(v)}/h`;

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

export const TllPanel = () => {
  const open = useValue(panelOpen$);
  const tab = useValue(tab$);
  const summary = useValue(summary$);
  const selected = useValue(selected$);
  const t = useTranslate();
  if (!open) return null;

  const problemsLabel = summary.problems.length > 0
    ? `${t("Panel.TabProblems", "Problems")} (${summary.problems.length})`
    : t("Panel.TabProblems", "Problems");
  const tabs: [Tab, string][] = [
    ["junction", t("Panel.TabJunction", "Junction")],
    ["city", t("Panel.TabCity", "City")],
    ["problems", problemsLabel],
  ];

  return (
    <Panel
      draggable
      className={styles.panel}
      header={<div className={styles.title}>{t("Panel.Title", "Traffic Lights & Lanes")}</div>}
      onClose={() => setPanelOpen(false)}
    >
      <div className={styles.tabs}>
        {tabs.map(([id, label]) => (
          <Button key={id} variant="flat" className={classNames(styles.tab, tab === id && styles.tabActive)} selected={tab === id} onSelect={() => tab$.update(id)}>
            {label}
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

// ---- Junction tab ----

const JunctionTab = ({ junction, t }: { junction: JunctionInfo | null; t: Translate }) => {
  const toolActive = useValue(toolActive$);
  return (
    <>
      <div className={styles.row}>
        <Button variant="flat" className={styles.wide} selected={toolActive} onSelect={actions.toggleTool}>
          {toolActive ? t("Panel.PickingJunction", "Click a junction…") : t("Panel.PickJunction", "Pick a junction on the map")}
        </Button>
      </div>
      {junction && (junction.managed ? <ManagedDetail junction={junction} t={t} /> : <UnmanagedDetail junction={junction} t={t} />)}
    </>
  );
};

const UnmanagedDetail = ({ junction, t }: { junction: JunctionInfo; t: Translate }) => (
  <div className={styles.detail}>
    <div className={styles.heading}>{junction.name}</div>
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

const ManagedDetail = ({ junction, t }: { junction: JunctionInfo; t: Translate }) => {
  const origin = junction.manual ? t("Panel.Manual", "Set by you") : t("Panel.Automatic", "Automatic");
  const wave = junction.group > 0 ? ` · ${t("Panel.GreenWave", "green wave")} #${junction.group}` : "";
  const info = `${origin} · ${t("Panel.Cycle", "cycle")} ${seconds(junction.cycleSeconds)}${wave}`;
  const modeHint = junction.mode === ControlMode.Coordinated
    ? t("Panel.CoordinatedNote", "Runs in a green wave. Choose another mode to take it out.")
    : null;
  return (
    <div className={styles.detail}>
      <div className={styles.heading}>{junction.name}</div>
      <div className={styles.muted}>{info}</div>
      {junction.manual && (
        <div className={styles.row}>
          <Hint text={t("Panel.MakeAutomaticHint", "")}>
            <Button variant="flat" className={styles.wide} onSelect={actions.makeAutomatic}>
              {t("Panel.MakeAutomatic", "Back to automatic")}
            </Button>
          </Hint>
        </div>
      )}

      {junction.autopilot && <AutopilotLine autopilot={junction.autopilot} junction={junction} t={t} />}

      <div className={styles.label}>{t("Panel.Mode", "Control")}</div>
      {junction.mode === ControlMode.Coordinated && (
        <div className={styles.row}>
          <Hint text={modeHint}>
            <Button variant="flat" className={styles.wide} selected>
              {`${t("Mode.Coordinated", "Green wave")} #${junction.group}`}
            </Button>
          </Hint>
        </div>
      )}
      {pairs(modes).map((row, i) => (
        <div key={i} className={styles.row}>
          {row.map((m) => (
            <Hint key={m} text={t("ModeHint." + ControlMode[m], "")}>
              <Button variant="flat" className={styles.choice} selected={junction.mode === m} onSelect={() => actions.setMode(m)}>
                {t("Mode." + ControlMode[m], ControlMode[m])}
              </Button>
            </Hint>
          ))}
        </div>
      ))}

      <div className={styles.label}>{t("Panel.Strategy", "Phase layout")}</div>
      {pairs(strategies).map((row, i) => (
        <div key={i} className={styles.row}>
          {row.map((s) => (
            <Hint key={s} text={t("StrategyHint." + PlanStrategy[s], "")}>
              <Button variant="flat" className={styles.choice} selected={junction.strategy === s} onSelect={() => actions.setStrategy(s)}>
                {t("Strategy." + PlanStrategy[s], PlanStrategy[s])}
              </Button>
            </Hint>
          ))}
        </div>
      ))}

      {junction.strategy !== PlanStrategy.ExclusivePedestrian && (
        <Toggle
          label={
            junction.scrambleOnDemand
              ? `${t("Panel.ScrambleOnDemand", "Scramble on demand")}: ${junction.scrambleActive ? t("Panel.ScrambleActive", "active") : t("Panel.ScrambleWaiting", "standby")} (${junction.conflicts}/8)`
              : t("Panel.ScrambleOnDemand", "Scramble on demand")
          }
          hint={t("Panel.ScrambleHint", "")}
          on={junction.scrambleOnDemand}
          onSelect={actions.toggleScramble}
        />
      )}

      <div className={styles.label}>{t("Panel.Phases", "Phases")}</div>
      {junction.phases.map((phase, i) => (
        <PhaseCard key={i} index={i} phase={phase} junction={junction} t={t} />
      ))}

      <div className={styles.row}>
        <Button variant="flat" className={styles.wide} onSelect={actions.release}>
          {t("Panel.Release", "Return to the game's control")}
        </Button>
      </div>
      <div className={styles.row}>
        <Hint text={t("Panel.DiagnoseHint", "Writes every lane, its signal, and why the first vehicle waits, to Logs/TLL.log. Useful when vehicles stand at green.")}>
          <Button variant="flat" className={styles.wide} onSelect={actions.diagnose}>
            {t("Panel.Diagnose", "Write diagnostics to the log")}
          </Button>
        </Hint>
      </div>
    </div>
  );
};

/**
 * The autopilot in one line: measured traffic, and the comparison of the
 * layouts in the tooltip. The estimates come from the busiest hours of the
 * last days, so they change slowly on purpose.
 */
const AutopilotLine = ({ autopilot, junction, t }: { autopilot: AutopilotInfo; junction: JunctionInfo; t: Translate }) => {
  const measuring = autopilot.estimates.length === 0;
  const traffic = measuring
    ? autopilot.tooQuiet
      ? t("Panel.TooQuiet", "too little traffic to compare layouts")
      : t("Panel.Collecting", "still measuring the traffic")
    : `${t("Panel.MainRoad", "Main road")} ${perHour(autopilot.majorVolume)} · ${t("Panel.SideRoad", "side road")} ${perHour(autopilot.minorVolume)}`;
  const comparison = autopilot.estimates
    .map((e) => `${e.strategy === junction.strategy ? "▸ " : "   "}${t("Strategy." + PlanStrategy[e.strategy], PlanStrategy[e.strategy])}: Ø ${seconds(e.delay)}, ${Math.round(e.saturation * 100)} %`)
    .join("\n");
  const hint = measuring ? t("Panel.CollectingHint", "The layout is reviewed once enough traffic has been measured.") : `${t("Panel.EstimateHint", "Expected mean wait and load of the busiest phase, per layout:")}\n${comparison}`;
  return (
    <>
      <Hint text={hint}>
        <div className={styles.autopilot}>{`${t("Panel.Autopilot", "Autopilot")}: ${traffic}`}</div>
      </Hint>
      {autopilot.pending >= 0 && autopilot.pending !== junction.strategy && (
        <div className={styles.note}>{`${t("Panel.PendingLayout", "Next review changes to")}: ${t("Strategy." + PlanStrategy[autopilot.pending], PlanStrategy[autopilot.pending])}`}</div>
      )}
      {autopilot.signalAdvice === SignalAdvice.RemoveSignals && (
        <div className={styles.note}>{t("Panel.RemoveSignals", "Priority rules would mean less waiting here than signals.")}</div>
      )}
    </>
  );
};

/** Colour of a phase's lamp: what drivers of its movements see right now. */
function lampOf(i: number, junction: JunctionInfo): string {
  if (junction.stage === Stage.Flashing) return styles.lampYellow;
  if (junction.stage === Stage.Green) return junction.phase === i ? styles.lampGreen : styles.lampRed;
  if (junction.phase === i && junction.stage === Stage.Yellow) return styles.lampYellow;
  if (junction.next === i && junction.stage === Stage.Prepare) return styles.lampYellow;
  return styles.lampRed;
}

const PhaseCard = ({ index, phase, junction, t }: { index: number; phase: PhaseInfo; junction: JunctionInfo; t: Translate }) => {
  const active = junction.phase === index && junction.stage === Stage.Green;
  const title = `${t("Panel.Phase", "Phase")} ${index + 1}`;
  const timing = active
    ? `${seconds(junction.stageSeconds)} ${t("Panel.Of", "of")} ${Math.round(phase.minGreen)}–${seconds(phase.maxGreen)}`
    : `${Math.round(phase.minGreen)}–${seconds(phase.maxGreen)}`;
  return (
    <div className={classNames(styles.phase, active && styles.phaseActive)}>
      <JunctionDiagram
        approaches={junction.approaches}
        movements={junction.movements}
        green={phase.movements}
        permitted={phase.permitted}
        leftHandTraffic={junction.leftHandTraffic}
        cameraYaw={junction.cameraYaw}
      />
      <div className={styles.phaseInfo}>
        <div className={styles.phaseTitleRow}>
          <div className={classNames(styles.lamp, lampOf(index, junction))} />
          <div className={styles.phaseTitle}>{title}</div>
        </div>
        <div className={styles.phaseLine}>{timing}</div>
        <div className={styles.phaseLine}>{`${t("Panel.Waiting", "waiting")}: ${phase.demand.toFixed(0)} · ${seconds(phase.wait)}`}</div>
        {phase.walkGreen > 0 && (
          <div className={classNames(styles.phaseLine, active && junction.walk && styles.walking)}>
            {active && junction.walk
              ? t("Panel.Walking", "Pedestrians walk")
              : `${t("Panel.WalkOnCall", "Crosswalk on request")} · ${seconds(phase.walkGreen)}`}
          </div>
        )}
        {phase.preempt && <div className={styles.preempt}>{t("Panel.Emergency", "Emergency")}</div>}
      </div>
    </div>
  );
};

// ---- City tab ----

const Toggle = ({ label, hint, on, onSelect }: { label: string; hint: string; on: boolean; onSelect: () => void }) => (
  <div className={styles.row}>
    <Hint text={hint}>
      <div className={styles.grow}>{label}</div>
    </Hint>
    <Button variant="flat" className={styles.small} selected={on} onSelect={onSelect}>
      {on ? "✓" : "–"}
    </Button>
  </div>
);

const CityTab = ({ summary, t }: { summary: Summary; t: Translate }) => {
  const byMode = modes
    .concat([ControlMode.Coordinated])
    .filter((m) => (summary.byMode[m] ?? 0) > 0)
    .map((m) => `${t("Mode." + ControlMode[m], ControlMode[m])} ${summary.byMode[m]}`)
    .join(" · ");
  return (
    <>
      <Toggle
        label={t("Panel.Automation", "Manage all traffic lights")}
        hint={t("Settings.AutoManageAll.Description", "")}
        on={summary.automation}
        onSelect={actions.toggleAutomation}
      />
      <div className={styles.muted}>{`${t("Panel.Managed", "Junctions controlled by TLL")}: ${summary.managed}`}</div>
      {byMode && <div className={styles.muted}>{byMode}</div>}

      <div className={styles.row}>
        <div className={styles.grow}>{`${t("Panel.GreenWaves", "Green waves")}: ${summary.greenWaves} (${summary.coordinated} ${t("Panel.Junctions", "junctions")})`}</div>
        <Button variant="flat" className={styles.small} onSelect={actions.rebuildGreenWaves}>
          {t("Panel.RebuildGreenWaves", "Recalculate")}
        </Button>
      </div>

      <ResetAll t={t} />

      <div className={styles.label}>{t("Settings.Group.Map", "Map")}</div>
      <Toggle
        label={t("Settings.ShowProblems.Label", "Mark problem junctions")}
        hint={t("Settings.ShowProblems.Description", "")}
        on={summary.showProblems}
        onSelect={actions.toggleShowProblems}
      />
      <Toggle
        label={t("Settings.ShowCongestion.Label", "Mark congestion")}
        hint={t("Settings.ShowCongestion.Description", "")}
        on={summary.showCongestion}
        onSelect={actions.toggleShowCongestion}
      />
    </>
  );
};

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

// ---- Problems tab ----

const ProblemsTab = ({ summary, selected, t }: { summary: Summary; selected: JunctionInfo | null; t: Translate }) => (
  <>
    <Hint text={t("Panel.ProblemsHint", "Junctions where queues build up in the rush hour, measured over game days. Click one to go there.")}>
      <div className={styles.label}>{t("Panel.ProblemsTitle", "Queues in the rush hour")}</div>
    </Hint>
    {summary.problems.length === 0 ? (
      <div className={styles.muted}>{t("Panel.NoProblems", "No junction with long queues measured.")}</div>
    ) : (
      summary.problems.map((p) => <ProblemRow key={p.index} problem={p} t={t} selected={selected?.index === p.index} />)
    )}
  </>
);

const ProblemRow = ({ problem, t, selected }: { problem: Problem; t: Translate; selected: boolean }) => (
  <Button
    variant="flat"
    className={classNames(styles.problem, selected && styles.selected)}
    onSelect={() => {
      actions.goto(problem);
      tab$.update("junction");
    }}
  >
    <div className={styles.problemName}>{problem.name}</div>
    <div className={styles.problemStats}>{`Ø ${Math.round(problem.queue)} ${t("Panel.Vehicles", "vehicles")}`}</div>
  </Button>
);
