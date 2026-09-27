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
  Stage,
  summary$,
} from "bindings";
import { useTranslate } from "localization";
import styles from "tll-panel.module.scss";

type Translate = ReturnType<typeof useTranslate>;

const modes = [ControlMode.Adaptive, ControlMode.Actuated, ControlMode.FixedTime, ControlMode.Flashing];
const strategies = [PlanStrategy.Permissive, PlanStrategy.ProtectedTurns, PlanStrategy.Split, PlanStrategy.ExclusivePedestrian];

export const TllPanel = () => {
  const open = useValue(panelOpen$);
  const summary = useValue(summary$);
  const selected = useValue(selected$);
  const t = useTranslate();
  if (!open) return null;

  return (
    <Panel
      draggable
      className={styles.panel}
      header={<div className={styles.title}>{t("Panel.Title", "Traffic Lights & Lanes")}</div>}
      onClose={() => panelOpen$.update(false)}
    >
      <Scrollable className={styles.scroll} vertical>
        {!summary.available && (
          <div className={styles.warning}>
            {t("Panel.Unavailable", "The game has changed in a way TLL does not recognise. All traffic lights are left to the game.")}
          </div>
        )}
        <div className={styles.row}>
          <span>
            {t("Panel.Managed", "Junctions controlled by TLL")}: <b>{summary.managed}</b>
          </span>
          <Button variant="flat" className={styles.toggle} selected={summary.automation} onSelect={actions.toggleAutomation}>
            {summary.automation ? t("Panel.AutomationOn", "Automation on") : t("Panel.AutomationOff", "Automation off")}
          </Button>
        </div>

        <div className={styles.heading}>{t("Panel.Problems", "Needs attention")}</div>
        {summary.problems.length === 0 ? (
          <div className={styles.muted}>{t("Panel.NoProblems", "No junction is struggling right now.")}</div>
        ) : (
          summary.problems.map((p) => <ProblemRow key={p.index} problem={p} t={t} selected={selected?.index === p.index} />)
        )}

        {selected && <JunctionDetail junction={selected} t={t} />}
      </Scrollable>
    </Panel>
  );
};

const ProblemRow = ({ problem, t, selected }: { problem: Problem; t: Translate; selected: boolean }) => (
  <Button variant="flat" className={classNames(styles.problem, selected && styles.selected)} onSelect={() => actions.goto(problem)}>
    <span className={styles.problemName}>{problem.name}</span>
    <span className={styles.problemStats}>
      {Math.round(problem.longestWait)} s · {problem.maxOuts}× {t("Panel.MaxOut", "maxed")}
    </span>
  </Button>
);

const JunctionDetail = ({ junction, t }: { junction: JunctionInfo; t: Translate }) => (
  <div className={styles.detail}>
    <div className={styles.heading}>{junction.name}</div>
    <div className={styles.muted}>
      {junction.manual ? t("Panel.Manual", "Set by you") : t("Panel.Automatic", "Automatic")} · {t("Panel.Cycle", "cycle")}{" "}
      {Math.round(junction.cycleSeconds)} s
    </div>

    <div className={styles.label}>{t("Panel.Mode", "Control")}</div>
    <div className={styles.choices}>
      {modes.map((m) => (
        <Button key={m} variant="flat" className={styles.choice} selected={junction.mode === m} onSelect={() => actions.setMode(m)}>
          {t("Mode." + ControlMode[m], ControlMode[m])}
        </Button>
      ))}
    </div>

    <div className={styles.label}>{t("Panel.Strategy", "Phase layout")}</div>
    <div className={styles.choices}>
      {strategies.map((s) => (
        <Button key={s} variant="flat" className={styles.choice} selected={junction.strategy === s} onSelect={() => actions.setStrategy(s)}>
          {t("Strategy." + PlanStrategy[s], PlanStrategy[s])}
        </Button>
      ))}
    </div>

    <div className={styles.label}>{t("Panel.Phases", "Phases")}</div>
    {junction.phases.map((phase, i) => (
      <PhaseCard key={i} index={i} phase={phase} junction={junction} t={t} />
    ))}

    <Button variant="flat" className={styles.release} onSelect={actions.release}>
      {t("Panel.Release", "Return to the game's control")}
    </Button>
  </div>
);

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
  return (
    <div className={classNames(styles.phase, active && styles.phaseActive)}>
      <div className={styles.phaseHeader}>
        <span className={classNames(styles.lamp, lampOf(index, junction))} />
        <b>
          {t("Panel.Phase", "Phase")} {index + 1}
        </b>
        <span className={styles.muted}>
          {active ? `${Math.round(junction.stageSeconds)} s / ` : ""}
          {Math.round(phase.minGreen)}–{Math.round(phase.maxGreen)} s
        </span>
        {phase.preempt && <span className={styles.preempt}>{t("Panel.Emergency", "Emergency")}</span>}
      </div>
      <div className={styles.phaseStats}>
        {t("Panel.Demand", "Demand")} {phase.demand.toFixed(1)} · {t("Panel.Waiting", "waiting")} {Math.round(phase.wait)} s
      </div>
      <div className={styles.movements}>
        {phase.movements.map((m) => (
          <MovementLabel key={m} label={junction.movements[m]} permitted={phase.permitted.includes(m)} t={t} />
        ))}
      </div>
    </div>
  );
};

const MovementLabel = ({ label, permitted, t }: { label: string | undefined; permitted: boolean; t: Translate }) => {
  const [kind, source, target] = (label ?? "||").split("|");
  const kindText = t("Movement." + kind, kind);
  const text = kind === "Pedestrian" ? `${kindText}: ${source}` : `${source || "?"} → ${target || "?"} (${kindText})`;
  return (
    <div className={classNames(styles.movement, permitted && styles.permitted)}>
      {text}
      {permitted && <span className={styles.yield}> {t("Panel.Yield", "yields")}</span>}
    </div>
  );
};
