import { useValue } from "cs2/api";
import { Button, Panel, Scrollable } from "cs2/ui";
import classNames from "classnames";
import {
  actions,
  ControlMode,
  JunctionInfo,
  panelOpen$,
  PhaseInfo,
  PlanStrategy,
  Problem,
  selected$,
  setPanelOpen,
  Stage,
  summary$,
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

type Translate = ReturnType<typeof useTranslate>;

const modes = [ControlMode.Adaptive, ControlMode.Actuated, ControlMode.FixedTime, ControlMode.Flashing];
const strategies = [PlanStrategy.Permissive, PlanStrategy.ProtectedTurns, PlanStrategy.Split, PlanStrategy.ExclusivePedestrian];

const seconds = (s: number) => `${Math.round(s)} s`;

function pairs<T>(items: T[]): T[][] {
  const rows: T[][] = [];
  for (let i = 0; i < items.length; i += 2) rows.push(items.slice(i, i + 2));
  return rows;
}

export const TllPanel = () => {
  const open = useValue(panelOpen$);
  const summary = useValue(summary$);
  const selected = useValue(selected$);
  const toolActive = useValue(toolActive$);
  const t = useTranslate();
  if (!open) return null;

  const managedText = `${t("Panel.Managed", "Junctions controlled by TLL")}: ${summary.managed}`;
  const wavesText = `${t("Panel.GreenWaves", "Green waves")}: ${summary.greenWaves} (${summary.coordinated} ${t("Panel.Junctions", "junctions")})`;

  return (
    <Panel
      draggable
      className={styles.panel}
      header={<div className={styles.title}>{t("Panel.Title", "Traffic Lights & Lanes")}</div>}
      onClose={() => setPanelOpen(false)}
    >
      <Scrollable className={styles.scroll} vertical>
        {!summary.available && (
          <div className={styles.warning}>
            {t("Panel.Unavailable", "The game has changed in a way TLL does not recognise. All traffic lights are left to the game.")}
          </div>
        )}

        <div className={styles.row}>
          <div className={styles.grow}>{managedText}</div>
          <Button variant="flat" className={styles.small} selected={summary.automation} onSelect={actions.toggleAutomation}>
            {summary.automation ? t("Panel.AutomationOn", "Automation on") : t("Panel.AutomationOff", "Automation off")}
          </Button>
        </div>
        <div className={styles.row}>
          <div className={styles.grow}>{wavesText}</div>
          <Button variant="flat" className={styles.small} onSelect={actions.rebuildGreenWaves}>
            {t("Panel.RebuildGreenWaves", "Recalculate")}
          </Button>
        </div>
        <div className={styles.row}>
          <Button variant="flat" className={styles.wide} selected={toolActive} onSelect={actions.toggleTool}>
            {toolActive ? t("Panel.PickingJunction", "Click a junction…") : t("Panel.PickJunction", "Pick a junction on the map")}
          </Button>
        </div>

        {selected && <JunctionDetail junction={selected} t={t} />}

        <div className={styles.heading}>{t("Panel.Problems", "Needs attention")}</div>
        {summary.problems.length === 0 ? (
          <div className={styles.muted}>{t("Panel.NoProblems", "No junction is struggling right now.")}</div>
        ) : (
          summary.problems.map((p) => <ProblemRow key={p.index} problem={p} t={t} selected={selected?.index === p.index} />)
        )}
      </Scrollable>
    </Panel>
  );
};

const ProblemRow = ({ problem, t, selected }: { problem: Problem; t: Translate; selected: boolean }) => (
  <Button variant="flat" className={classNames(styles.problem, selected && styles.selected)} onSelect={() => actions.goto(problem)}>
    <div className={styles.problemName}>{problem.name}</div>
    <div className={styles.problemStats}>{`${seconds(problem.longestWait)} · ${problem.maxOuts}× ${t("Panel.MaxOut", "maxed")}`}</div>
  </Button>
);

const JunctionDetail = ({ junction, t }: { junction: JunctionInfo; t: Translate }) =>
  junction.managed ? <ManagedDetail junction={junction} t={t} /> : <UnmanagedDetail junction={junction} t={t} />;

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
  return (
    <div className={styles.detail}>
      <div className={styles.heading}>{junction.name}</div>
      <div className={styles.muted}>{info}</div>

      <div className={styles.label}>{t("Panel.Mode", "Control")}</div>
      {junction.mode === ControlMode.Coordinated && (
        <div className={styles.note}>{t("Panel.CoordinatedNote", "Runs in a green wave. Choose another mode to take it out.")}</div>
      )}
      {pairs(modes).map((row, i) => (
        <div key={i} className={styles.row}>
          {row.map((m) => (
            <Button key={m} variant="flat" className={styles.choice} selected={junction.mode === m} onSelect={() => actions.setMode(m)}>
              {t("Mode." + ControlMode[m], ControlMode[m])}
            </Button>
          ))}
        </div>
      ))}

      <div className={styles.label}>{t("Panel.Strategy", "Phase layout")}</div>
      {pairs(strategies).map((row, i) => (
        <div key={i} className={styles.row}>
          {row.map((s) => (
            <Button key={s} variant="flat" className={styles.choice} selected={junction.strategy === s} onSelect={() => actions.setStrategy(s)}>
              {t("Strategy." + PlanStrategy[s], PlanStrategy[s])}
            </Button>
          ))}
        </div>
      ))}

      <div className={styles.label}>{t("Panel.Phases", "Phases")}</div>
      {junction.phases.map((phase, i) => (
        <PhaseCard key={i} index={i} phase={phase} junction={junction} t={t} />
      ))}

      <div className={styles.row}>
        <Button variant="flat" className={styles.wide} onSelect={actions.release}>
          {t("Panel.Release", "Return to the game's control")}
        </Button>
      </div>
    </div>
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
        <div className={styles.phaseLine}>{`${t("Panel.Demand", "Demand")}: ${phase.demand.toFixed(0)}`}</div>
        <div className={styles.phaseLine}>{`${t("Panel.Waiting", "waiting")}: ${seconds(phase.wait)}`}</div>
        {phase.preempt && <div className={styles.preempt}>{t("Panel.Emergency", "Emergency")}</div>}
      </div>
    </div>
  );
};
