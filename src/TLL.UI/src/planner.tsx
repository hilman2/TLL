import { useValue } from "cs2/api";
import { Button, Panel, Scrollable } from "cs2/ui";
import classNames from "classnames";
import { ReactNode, useState } from "react";
import { ControlMode, Stage } from "bindings";
import { JunctionDiagram } from "junction-diagram";
import { useTranslate } from "localization";
import { PlannerDiagram } from "planner-diagram";
import { LanesStep } from "planner-lanes";
import {
  Hint,
  LinkButton,
  movementName,
  roadName,
  rows,
  seconds,
  Segments,
  Sheet,
  sheet$,
  Step,
  step$,
  Stepper,
  Switch,
  Tool,
  Translate,
} from "planner-parts";
import {
  Finding,
  FindingKind,
  Owner,
  planner,
  planner$,
  PlannerInfo,
  PlannerMessage,
  PlannerPhase,
  Preset,
  Refusal,
  Severity,
  Template,
  TemplateKind,
} from "planner-bindings";
import logo from "images/tll.svg";
import styles from "planner.module.scss";

// The planner: one junction's lanes, phases and times, edited in a panel of
// its own. Everything the player does goes to the C# side as a trigger
// (planner-bindings.ts), which keeps the draft, checks it and sends it back;
// this file only draws it.
//
// Layout note, as in tll-panel.tsx: the game's UI engine lays out every text
// node as its own flex item, so texts are built as one string, and rows
// never wrap.

/** The time a phase card shows: its green in fixed time, else the range the demand chooses from. */
function phaseTime(info: PlannerInfo, p: PlannerPhase): string {
  return info.mode === ControlMode.FixedTime ? seconds(p.green) : `${Math.round(p.minGreen)}–${seconds(p.maxGreen)}`;
}

/** Colour of a phase card's lamp: what drivers of its movements see right now. */
function lamp(info: PlannerInfo, i: number): string | null {
  if (info.running < 0 && info.next < 0) return null;
  if (info.stage === Stage.Flashing) return styles.lampYellow;
  if (info.running === i && info.stage === Stage.Green) return styles.lampGreen;
  if ((info.running === i && info.stage === Stage.Yellow) || (info.next === i && info.stage === Stage.Prepare)) return styles.lampYellow;
  return "";
}

// ---- The panel ----

export const Planner = () => {
  const info = useValue(planner$);
  const t = useTranslate();
  const step = useValue(step$);
  const sheet = useValue(sheet$);
  const setStep = (s: Step) => step$.update(s);
  const setSheet = (s: Sheet) => sheet$.update(s);
  const [tour, setTour] = useState(-1);
  const [hovered, setHovered] = useState(-1);
  if (!info.open) return null;
  const tourStep = tour >= 0 ? tour : info.tourSeen ? -1 : 0;

  const steps: [Step, string][] = [
    ["lanes", t("Planner.StepLanes", "Lanes")],
    ["phases", t("Planner.StepPhases", "Phases")],
    ["times", t("Planner.StepTimes", "Times")],
  ];

  return (
    <Panel
      draggable
      initialPosition={{ x: 0, y: 0 }}
      className={styles.panel}
      header={
        <div className={styles.header}>
          <img className={styles.logo} src={logo} />
          <div className={styles.title}>{t("Planner.Title", "Planner")}</div>
          <div className={styles.subtitle}>{info.name}</div>
        </div>
      }
      onClose={planner.close}
    >
      <div className={styles.row}>
        <Tool label={t("Planner.Templates", "Templates")} hint={t("Planner.TemplatesHint", "")} on={sheet === "templates"} onSelect={() => setSheet(sheet === "templates" ? "none" : "templates")} />
        <Tool label={t("Planner.Presets", "Presets")} hint={t("Planner.PresetsHint", "")} on={sheet === "presets"} onSelect={() => setSheet(sheet === "presets" ? "none" : "presets")} />
        <Tool label={t("Planner.Copy", "Copy")} hint={t("Planner.CopyHint", "")} onSelect={planner.copy} />
        <Tool label={t("Planner.Paste", "Paste")} hint={t("Planner.PasteHint", "")} onSelect={planner.paste} />
        <div className={styles.spacer} />
        <Tool label={t("Planner.Undo", "Undo")} disabled={!info.canUndo} onSelect={planner.undo} />
        <Tool label={t("Planner.Redo", "Redo")} disabled={!info.canRedo} onSelect={planner.redo} />
        <Tool label="?" hint={t("Planner.TourHint", "")} onSelect={() => setTour(0)} />
      </div>

      <div className={styles.label}>{t("Planner.Decides", "Who decides")}</div>
      <Segments
        items={[Owner.Autopilot, Owner.Layout, Owner.Everything]}
        value={info.owner}
        label={(o) => t("Planner.Owner." + Owner[o], ownerText[o])}
        hint={(o) => t("Planner.OwnerHint." + Owner[o], "")}
        onSelect={planner.setOwner}
      />
      {info.owner === Owner.Autopilot && <div className={styles.muted}>{t("Planner.AutopilotNote", "The autopilot plans this junction. Click an arrow or pick a template to take it over.")}</div>}
      {info.owner !== Owner.Autopilot && info.coordinated && <div className={styles.muted}>{t("Planner.WaveNote", "Applying takes this junction out of its green wave.")}</div>}

      {tourStep >= 0 && <Tour step={tourStep} t={t} onNext={() => setTour(tourStep + 1)} onDone={() => { setTour(-1); planner.tourSeen(); }} />}

      {sheet === "none" && (
        <div className={styles.steps}>
          {steps.map(([id, label], i) => (
            <Button key={id} variant="flat" className={classNames(styles.step, step === id && styles.stepOn)} onSelect={() => setStep(id)}>
              <div className={classNames(styles.stepNumber, step === id && styles.stepNumberOn)}>{`${i + 1}`}</div>
              <div>{label}</div>
            </Button>
          ))}
        </div>
      )}

      <Scrollable className={styles.scroll} vertical>
        {sheet === "templates" && <TemplatesSheet info={info} t={t} onDone={() => { setSheet("none"); setStep("phases"); }} />}
        {sheet === "presets" && <PresetsSheet info={info} t={t} onDone={() => { setSheet("none"); setStep("phases"); }} />}
        {sheet === "none" && step === "lanes" && <LanesStep info={info} t={t} />}
        {sheet === "none" && step === "phases" && <PhasesStep info={info} t={t} hovered={hovered} onHover={setHovered} />}
        {sheet === "none" && step === "times" && <TimesStep info={info} t={t} />}
      </Scrollable>

      {info.message && <Message message={info.message} t={t} />}
      <Checks info={info} t={t} />
      <Footer info={info} t={t} />
    </Panel>
  );
};

const ownerText: Record<Owner, string> = {
  [Owner.Autopilot]: "Autopilot",
  [Owner.Layout]: "You: layout",
  [Owner.Everything]: "You: everything",
};

// ---- Tour ----

const tourSteps: [string, string, string][] = [
  ["Planner.Tour1", "Three steps", "Lanes, phases, times: the order a traffic planner works in. Jump between them freely."],
  ["Planner.Tour2", "Click the arrows", "In a phase, a click on an arrow gives it green, another click takes it away. What would crash is refused, with the reason and where it fits instead."],
  ["Planner.Tour3", "Nothing changes until Apply", "Your changes wait for Apply, and the check below says what still needs fixing. With Live changes on, they reach the traffic at once."],
];

const Tour = ({ step, t, onNext, onDone }: { step: number; t: Translate; onNext: () => void; onDone: () => void }) => {
  const [key, title, text] = tourSteps[Math.min(step, tourSteps.length - 1)];
  const last = step >= tourSteps.length - 1;
  return (
    <div className={styles.tour}>
      <div className={styles.tourTitle}>{`${step + 1}/${tourSteps.length}  ${t(key + "Title", title)}`}</div>
      <div className={styles.tourText}>{t(key + "Text", text)}</div>
      <div className={styles.row}>
        <div className={styles.spacer} />
        {!last && <Button variant="flat" className={styles.secondary} onSelect={onDone}>{t("Planner.TourSkip", "Skip")}</Button>}
        <Button variant="flat" className={styles.primary} onSelect={last ? onDone : onNext}>
          {last ? t("Planner.TourDone", "Got it") : t("Planner.TourNext", "Next")}
        </Button>
      </div>
    </div>
  );
};

// ---- Step 2: phases ----

const PhasesStep = ({ info, t, hovered, onHover }: { info: PlannerInfo; t: Translate; hovered: number; onHover: (m: number) => void }) => {
  const phase: PlannerPhase | undefined = info.phases[info.selected];
  const cards: (number | "add")[] = info.phases.map((_, i) => i);
  if (info.phases.length < 16) cards.push("add");
  const noGreen = info.findings.filter((f) => f.kind === FindingKind.NoGreen);
  const hint =
    hovered >= 0
      ? `${movementName(info, hovered, t)}${info.movements[hovered].volume >= 0 ? ` · ${Math.round(info.movements[hovered].volume)}/h` : ""}`
      : t("Planner.ClickHint", "Click an arrow to give it green in this phase. Click again to take it away.");
  return (
    <>
      {rows(cards, 6).map((row, r) => (
        <div key={r} className={styles.cards}>
          {row.map((c) =>
            c === "add" ? (
              <Hint key="add" text={t("Planner.AddPhaseHint", "A new, empty phase after the selected one.")}>
                <Button variant="flat" className={styles.addCard} onSelect={planner.addPhase}>
                  +
                </Button>
              </Hint>
            ) : (
              <PhaseCard key={c} info={info} index={c} t={t} />
            ),
          )}
        </div>
      ))}

      {phase && (
        <div className={styles.phaseTools}>
          <div className={styles.phaseTitle}>{`${t("Planner.Phase", "Phase")} ${info.selected + 1}`}</div>
          <Tool label="<" hint={t("Planner.MoveEarlier", "Runs earlier")} disabled={info.selected === 0} onSelect={() => planner.movePhase(info.selected, info.selected - 1)} />
          <Tool label=">" hint={t("Planner.MoveLater", "Runs later")} disabled={info.selected >= info.phases.length - 1} onSelect={() => planner.movePhase(info.selected, info.selected + 1)} />
          <div className={styles.spacer} />
          <Tool label={t("Planner.FillUp", "Fill up")} hint={t("Planner.FillUpHint", "")} onSelect={() => planner.fillUp(info.selected)} />
          <Tool label={t("Planner.Duplicate", "Duplicate")} disabled={info.phases.length >= 16} onSelect={() => planner.duplicatePhase(info.selected)} />
          <Tool
            label={info.hold === info.selected ? t("Planner.Release", "Release") : t("Planner.Hold", "Hold")}
            hint={t("Planner.HoldHint", "")}
            on={info.hold === info.selected}
            disabled={!phase.canHold}
            onSelect={() => planner.hold(info.hold === info.selected ? -1 : info.selected)}
          />
          <Tool label={t("Planner.Delete", "Delete")} disabled={info.phases.length <= 1} onSelect={() => planner.deletePhase(info.selected)} />
        </div>
      )}

      <PlannerDiagram
        info={info}
        green={phase?.movements ?? []}
        permitted={phase?.permitted ?? []}
        addable={info.addable}
        refused={info.refusal?.movement ?? -1}
        blocking={info.refusal?.blocking ?? []}
        onHover={(m) => {
          onHover(m);
          planner.hover(m);
        }}
        onClick={planner.toggle}
      />
      <div className={styles.hint}>{hint}</div>
      <div className={styles.legend}>
        <div className={styles.legendItem}>
          <div className={classNames(styles.legendSwatch, styles.swatchOn)} />
          <div>{t("Planner.LegendGreen", "green")}</div>
        </div>
        <div className={styles.legendItem}>
          <div className={classNames(styles.legendSwatch, styles.swatchYield)} />
          <div>{t("Planner.LegendYield", "green, gives way")}</div>
        </div>
        <div className={styles.legendItem}>
          <div className={classNames(styles.legendSwatch, styles.swatchOff)} />
          <div>{t("Planner.LegendRed", "red")}</div>
        </div>
      </div>

      {info.refusal && <RefusalBox info={info} refusal={info.refusal} t={t} />}

      {noGreen.length > 0 && (
        <>
          <Hint text={t("Planner.NoGreenYetHint", "")}>
            <div className={styles.label}>{t("Planner.NoGreenYet", "No green yet")}</div>
          </Hint>
          {rows(noGreen, 3).map((row, r) => (
            <div key={r} className={styles.chips}>
              {row.map((f) => (
                <Button key={f.movement} variant="flat" className={styles.chip} onSelect={() => planner.place(f.movement)}>
                  {movementName(info, f.movement, t)}
                </Button>
              ))}
            </div>
          ))}
        </>
      )}
    </>
  );
};

const PhaseCard = ({ info, index, t }: { info: PlannerInfo; index: number; t: Translate }) => {
  const p = info.phases[index];
  const on = info.selected === index;
  const lampClass = lamp(info, index);
  return (
    <Button
      variant="flat"
      className={classNames(styles.phaseCard, on && styles.phaseCardOn, info.running === index && info.stage === Stage.Green && styles.phaseCardRunning)}
      onSelect={() => planner.selectPhase(index)}
    >
      <div className={styles.phaseCardTop}>
        <div className={styles.phaseNumber}>{info.hold === index ? `${index + 1} · ${t("Planner.Held", "held")}` : `${index + 1}`}</div>
        {lampClass !== null && <div className={classNames(styles.lamp, lampClass)} />}
      </div>
      <JunctionDiagram
        approaches={info.approaches}
        movements={info.movements}
        green={p.movements}
        permitted={p.permitted}
        leftHandTraffic={info.leftHandTraffic}
        cameraYaw={info.cameraYaw}
        className={styles.cardDiagram}
      />
      <div className={styles.cardTime}>{phaseTime(info, p)}</div>
    </Button>
  );
};

/** A refused click: what the movement would cross, and where it fits instead. */
const RefusalBox = ({ info, refusal, t }: { info: PlannerInfo; refusal: Refusal; t: Translate }) => {
  const crossing = refusal.blocking.map((m) => movementName(info, m, t)).join("; ");
  return (
    <div className={styles.refusal}>
      <div className={styles.muted}>{`${movementName(info, refusal.movement, t)}: ${t("Planner.Crosses", "its path crosses")} ${crossing}.`}</div>
      <div className={styles.row}>
        <div className={styles.faint}>{t("Planner.PutItInto", "Put it into")}</div>
        {refusal.fitsIn.slice(0, 4).map((p) => (
          <LinkButton key={p} label={`${t("Planner.Phase", "Phase")} ${p + 1}`} onSelect={() => planner.addTo(refusal.movement, p)} />
        ))}
        {info.phases.length < 16 && <LinkButton label={t("Planner.NewPhase", "a new phase")} onSelect={() => planner.newPhaseWith(refusal.movement)} />}
      </div>
      {info.movements[refusal.movement]?.partners.length > 0 && (
        <div className={styles.row}>
          <div className={styles.faint}>{t("Planner.SharesLane", "It shares a lane with other directions and goes only with them.")}</div>
          <LinkButton label={t("Planner.OwnLane", "Give it its own lane")} onSelect={() => step$.update("lanes")} />
        </div>
      )}
    </div>
  );
};

// ---- Step 3: times ----

const modes = [ControlMode.Adaptive, ControlMode.Actuated, ControlMode.FixedTime, ControlMode.Drain, ControlMode.Flashing];

const TimesStep = ({ info, t }: { info: PlannerInfo; t: Translate }) => {
  const [plan, setPlan] = useState(false);
  const [expert, setExpert] = useState(false);
  const fixed = info.mode === ControlMode.FixedTime;
  return (
    <>
      <div className={styles.label}>{t("Planner.Mode", "How the signals decide")}</div>
      <Segments
        items={modes}
        value={info.mode}
        label={(m) => t("Mode." + ControlMode[m], ControlMode[m])}
        hint={(m) => t("ModeHint." + ControlMode[m], "")}
        onSelect={planner.setMode}
      />
      <div className={styles.muted}>{t("ModeHint." + ControlMode[info.mode], "")}</div>
      <Switch
        label={t("Planner.AutoTiming", "Adjust times automatically")}
        hint={t("Planner.AutoTimingHint", "")}
        on={info.owner !== Owner.Everything}
        onSelect={() => planner.setOwner(info.owner === Owner.Everything ? Owner.Layout : Owner.Everything)}
      />

      <div className={styles.label}>{`${t("Planner.Cycle", "Cycle")}: ${fixed ? seconds(info.cycle) : `${t("Planner.UpTo", "up to")} ${seconds(info.cycleLongest)}`}`}</div>
      <CycleBar info={info} t={t} />

      {info.phases.map((p, i) => (
        <TimeRow key={i} info={info} index={i} phase={p} t={t} />
      ))}

      <div className={styles.row}>
        <Tool label={plan ? t("Planner.HidePlan", "Hide timing plan") : t("Planner.ShowPlan", "Show timing plan")} hint={t("Planner.PlanHint", "")} onSelect={() => setPlan(!plan)} />
        <Tool label={expert ? t("Planner.HideExpert", "Hide more settings") : t("Planner.ShowExpert", "More settings")} onSelect={() => setExpert(!expert)} />
      </div>
      {plan && <TimingPlan info={info} t={t} />}
      {expert && <Expert info={info} t={t} />}
    </>
  );
};

/** The cycle as a bar: each phase as long as its green (or maximum), with the change after it. */
const CycleBar = ({ info, t }: { info: PlannerInfo; t: Translate }) => {
  const fixed = info.mode === ControlMode.FixedTime;
  const change = info.yellow + info.allRed + info.prepare;
  const greens = info.phases.map((p) => (fixed ? p.green : p.maxGreen));
  const total = greens.reduce((a, b) => a + b + change, 0) || 1;
  const parts: ReactNode[] = [];
  greens.forEach((g, i) => {
    parts.push(
      <div key={`g${i}`} className={classNames(styles.cycleSegment, i === info.selected && styles.cycleSegmentOn)} style={{ width: `${(100 * g) / total}%` }}>
        <div className={styles.cycleLabel}>{`${i + 1}`}</div>
      </div>,
    );
    parts.push(<div key={`c${i}`} className={styles.cycleGap} style={{ width: `${(100 * change) / total}%` }} />);
  });
  return (
    <Hint text={t("Planner.CycleHint", "")}>
      <div className={styles.cycle}>{parts}</div>
    </Hint>
  );
};

const TimeRow = ({ info, index, phase, t }: { info: PlannerInfo; index: number; phase: PlannerPhase; t: Translate }) => {
  const fixed = info.mode === ControlMode.FixedTime;
  const set = (min: number, max: number, green: number) => planner.setTiming(index, min, max, green);
  const figures = phase.demand >= 0 ? `${Math.round(phase.demand)} ${t("Planner.Waiting", "waiting")} · ${seconds(phase.wait)}` : "";
  return (
    <div className={classNames(styles.timeRow, info.selected === index && styles.timeRowOn)} onMouseEnter={() => planner.selectPhase(index)}>
      <JunctionDiagram
        approaches={info.approaches}
        movements={info.movements}
        green={phase.movements}
        permitted={phase.permitted}
        leftHandTraffic={info.leftHandTraffic}
        cameraYaw={info.cameraYaw}
        className={styles.timeDiagram}
      />
      <div className={styles.timeInfo}>
        <div className={styles.timeName}>{`${t("Planner.Phase", "Phase")} ${index + 1}`}</div>
        {phase.walk > 0 && <div className={styles.faint}>{`${t("Planner.Walk", "crosswalk needs")} ${seconds(phase.walk)}`}</div>}
        {figures && <div className={styles.faint}>{figures}</div>}
      </div>
      <div className={styles.spacer} />
      {fixed ? (
        <Stepper label={t("Planner.Green", "Green")} value={phase.green} min={Math.max(1, phase.walk)} hint={t("Planner.GreenHint", "")} onChange={(v) => set(phase.minGreen, Math.max(phase.maxGreen, v), v)} />
      ) : (
        <>
          <Stepper label={t("Planner.Min", "Min")} value={phase.minGreen} min={1} hint={t("Planner.MinHint", "")} onChange={(v) => set(v, Math.max(phase.maxGreen, v), phase.green)} />
          <Stepper label={t("Planner.Max", "Max")} value={phase.maxGreen} min={phase.minGreen} hint={t("Planner.MaxHint", "")} onChange={(v) => set(phase.minGreen, v, Math.min(phase.green, v))} />
        </>
      )}
    </div>
  );
};

/**
 * The signal timing plan traffic engineers use: one row per movement, its
 * green over the cycle. A movement green in two phases in a row keeps its
 * green through the change between them, and shows as one bar.
 */
const TimingPlan = ({ info, t }: { info: PlannerInfo; t: Translate }) => {
  const fixed = info.mode === ControlMode.FixedTime;
  const change = info.yellow + info.allRed + info.prepare;
  const greens = info.phases.map((p) => (fixed ? p.green : p.maxGreen));
  const total = greens.reduce((a, b) => a + b + change, 0) || 1;
  return (
    <div className={styles.plan}>
      {info.movements.map((mv, m) => {
        const parts: ReactNode[] = [];
        info.phases.forEach((p, i) => {
          const on = p.movements.includes(m);
          const yields = p.permitted.includes(m);
          const nextOn = info.phases[(i + 1) % info.phases.length]?.movements.includes(m);
          parts.push(<div key={`p${i}`} className={on ? (yields ? styles.planYield : styles.planGreen) : styles.planRed} style={{ width: `${(100 * greens[i]) / total}%` }} />);
          parts.push(<div key={`c${i}`} className={on && nextOn ? (yields ? styles.planYield : styles.planGreen) : styles.planRed} style={{ width: `${(100 * change) / total}%` }} />);
        });
        return (
          <div key={m} className={styles.planRow}>
            <div className={styles.planName}>{movementName(info, m, t)}</div>
            <div className={styles.planTrack}>{parts}</div>
          </div>
        );
      })}
    </div>
  );
};

const Expert = ({ info, t }: { info: PlannerInfo; t: Translate }) => (
  <div className={styles.card}>
    <div className={styles.row}>
      <Stepper label={t("Planner.Yellow", "Yellow")} value={info.yellow} min={1} onChange={(v) => planner.setIntergreen(v, info.allRed, info.prepare)} />
      <Stepper label={t("Planner.AllRed", "All red")} value={info.allRed} min={0} onChange={(v) => planner.setIntergreen(info.yellow, v, info.prepare)} />
      <Stepper label={t("Planner.Prepare", "Red-amber")} value={info.prepare} min={0} onChange={(v) => planner.setIntergreen(info.yellow, info.allRed, v)} />
    </div>
    <div className={styles.row}>
      <Stepper label={t("Planner.MaxWait", "Longest wait")} value={info.maxWait} min={20} hint={t("Planner.MaxWaitHint", "")} onChange={planner.setMaxWait} />
    </div>
    <Switch label={t("Panel.TurnOnRed", "Turn on red")} hint={t("Panel.TurnOnRedHint", "")} on={info.turnOnRed} onSelect={() => planner.setTurnOnRed(!info.turnOnRed)} />
    {info.crosswalkPhases && (
      <Switch label={t("Planner.Scramble", "Pedestrians only in their own phase")} hint={t("Planner.ScrambleHint", "")} on={info.scramble} onSelect={() => planner.setScramble(!info.scramble)} />
    )}
  </div>
);

// ---- Templates ----

const templateText: Record<TemplateKind, [string, string]> = {
  [TemplateKind.TurnsGiveWay]: ["Turns give way", "Oncoming traffic runs together; turns across it wait for gaps. Fewest phases."],
  [TemplateKind.ProtectedFirst]: ["Protected turns first", "Turns across oncoming traffic get a green of their own, before the straight traffic of their road."],
  [TemplateKind.ProtectedLast]: ["Protected turns last", "Turns across oncoming traffic get a green of their own, after the straight traffic of their road."],
  [TemplateKind.ProtectedMainRoad]: ["Protected turns on the main road", "Only the main road's turns get a green of their own; the side road's give way."],
  [TemplateKind.EachRoadAlone]: ["Each road alone", "Every road gets green on its own. Nobody gives way to anybody; the most waiting."],
};

const TemplatesSheet = ({ info, t, onDone }: { info: PlannerInfo; t: Translate; onDone: () => void }) => (
  <>
    <div className={styles.sheetHead}>
      <div className={styles.sheetTitle}>{t("Planner.Templates", "Templates")}</div>
      <Tool label={t("Planner.Close", "Close")} onSelect={onDone} />
    </div>
    <div className={styles.muted}>{t("Planner.TemplatesNote", "Drawn on this junction. A click loads one into your plan; nothing changes on the road until you apply it.")}</div>
    <div className={styles.row}>
      <div className={styles.grow}>{`${t("Planner.MainRoad", "Main road")}: ${roadName(info, info.main)}`}</div>
      <LinkButton label={t("Planner.OtherMainRoad", "Choose another")} hint={t("Planner.MainRoadHint", "")} onSelect={() => planner.setMain((info.main + 1) % info.approaches.length)} />
    </div>
    {info.hasCrosswalks && (
      <Switch label={t("Planner.WithScramble", "With a phase for pedestrians alone")} hint={t("Planner.ScrambleHint", "")} on={info.templateScramble} onSelect={() => planner.templateScramble(!info.templateScramble)} />
    )}
    <div className={styles.list}>
      {info.templates.map((tpl) => (
        <TemplateRow key={tpl.kind} info={info} template={tpl} t={t} onDone={onDone} />
      ))}
    </div>
  </>
);

const TemplateRow = ({ info, template, t, onDone }: { info: PlannerInfo; template: Template; t: Translate; onDone: () => void }) => {
  const [name, description] = templateText[template.kind];
  return (
    <Hint text={t("Planner.TemplateHint." + TemplateKind[template.kind], description)}>
      <Button
        variant="flat"
        className={classNames(styles.template, template.current && styles.templateOn)}
        onSelect={() => {
          planner.template(template.kind);
          onDone();
        }}
      >
        <div className={styles.templatePhases}>
          {template.phases.slice(0, 5).map((p, i) => (
            <JunctionDiagram
              key={i}
              approaches={info.approaches}
              movements={info.movements}
              green={p.movements}
              permitted={p.permitted}
              leftHandTraffic={info.leftHandTraffic}
              cameraYaw={info.cameraYaw}
              className={styles.templateDiagram}
            />
          ))}
        </div>
        <div className={styles.templateInfo}>
          <div className={styles.templateName}>{t("Planner.Template." + TemplateKind[template.kind], name)}</div>
          <div className={styles.faint}>{`${template.phases.length} ${t("Planner.PhasesCount", "phases")}`}</div>
        </div>
        <div className={classNames(styles.templateDelay, template.saturation > 1 && styles.over)}>{template.delay >= 0 ? `Ø ${seconds(template.delay)}` : ""}</div>
      </Button>
    </Hint>
  );
};

// ---- Presets ----

const PresetsSheet = ({ info, t, onDone }: { info: PlannerInfo; t: Translate; onDone: () => void }) => {
  const [name, setName] = useState("");
  const [timing, setTiming] = useState(true);
  const [lanes, setLanes] = useState(true);
  const [deleting, setDeleting] = useState<string | null>(null);
  return (
    <>
      <div className={styles.sheetHead}>
        <div className={styles.sheetTitle}>{t("Planner.Presets", "Presets")}</div>
        <Tool label={t("Planner.Close", "Close")} onSelect={onDone} />
      </div>
      <div className={styles.muted}>{t("Planner.PresetsNote", "Your saved plans, in every city. One fits every junction of the same shape, turned if needed.")}</div>
      <div className={styles.row}>
        <input className={styles.input} value={name} placeholder={t("Planner.PresetName", "Name of the preset")} onChange={(e) => setName(e.target.value)} />
        <Button variant="flat" className={styles.primary} onSelect={() => { planner.savePreset(name, timing, lanes); setName(""); }}>
          {t("Planner.Save", "Save")}
        </Button>
      </div>
      <Switch label={t("Planner.WithTimes", "Keep the times with it")} hint={t("Planner.WithTimesHint", "")} on={timing} onSelect={() => setTiming(!timing)} />
      <Switch label={t("Planner.WithLanes", "Keep the lanes with it")} hint={t("Planner.WithLanesHint", "")} on={lanes} onSelect={() => setLanes(!lanes)} />
      <div className={styles.row}>
        <Tool label={t("Planner.PasteText", "Paste a shared preset")} hint={t("Planner.PasteHint", "")} onSelect={planner.paste} />
        {info.presetTurns > 1 && <Tool label={t("Planner.Turn", "Turn it")} hint={t("Planner.TurnHint", "")} onSelect={planner.turnPreset} />}
      </div>
      <div className={styles.list}>
        {info.presets.length === 0 && <div className={styles.muted}>{t("Planner.NoPresets", "No presets yet. Save the plan of a junction to use it at others.")}</div>}
        {info.presets.map((p) => (
          <PresetRow key={p.id} info={info} preset={p} t={t} deleting={deleting === p.id} onDelete={() => setDeleting(p.id)} onKeep={() => setDeleting(null)} onUse={onDone} />
        ))}
      </div>
    </>
  );
};

const PresetRow = ({ info, preset, t, deleting, onDelete, onKeep, onUse }: {
  info: PlannerInfo;
  preset: Preset;
  t: Translate;
  deleting: boolean;
  onDelete: () => void;
  onKeep: () => void;
  onUse: () => void;
}) => {
  const detail = preset.fits
    ? `${preset.phases} ${t("Planner.PhasesCount", "phases")}`
    : preset.arms !== info.roads
    ? `${t("Planner.Needs", "needs")} ${preset.arms} ${t("Planner.RoadsCount", "roads")}`
    : t("Planner.OtherShape", "other shape");
  return (
    <div className={classNames(styles.preset, !preset.fits && styles.presetOff)}>
      <div className={styles.presetName}>{preset.name}</div>
      <div className={styles.faint}>{detail}</div>
      {deleting ? (
        <>
          <LinkButton label={t("Planner.ReallyDelete", "Delete")} onSelect={() => { planner.deletePreset(preset.id); onKeep(); }} />
          <LinkButton label={t("Planner.Keep", "Keep")} onSelect={onKeep} />
        </>
      ) : (
        <>
          {preset.fits && <LinkButton label={t("Planner.Use", "Use")} onSelect={() => { planner.applyPreset(preset.id); onUse(); }} />}
          <LinkButton label={t("Planner.Share", "Share")} hint={t("Planner.ShareHint", "")} onSelect={() => planner.sharePreset(preset.id)} />
          <LinkButton label={t("Planner.Delete", "Delete")} onSelect={onDelete} />
        </>
      )}
    </div>
  );
};

// ---- Messages, checks, footer ----

const messageText: Record<string, string> = {
  Reloaded: "The junction's roads changed. The planner loaded it again.",
  CannotApply: "The plan still has an error. Fix it first.",
  PlanFull: "The plan has 16 phases, the most the game allows.",
  PresetSaved: "Saved as preset:",
  PresetNotSaved: "This preset could not be saved:",
  PresetDoesNotFit: "This preset does not fit the junction:",
  PresetLoaded: "Loaded, check it and apply:",
  PresetLoadedTurnable: "Loaded. It fits more than one way; turn it if the roads are wrong:",
  PresetLoadedWithChanges: "Loaded. Ways added / left out:",
  PresetCopied: "On the clipboard, ready to share:",
  Copied: "The plan is on the clipboard. Paste it at another junction.",
  PasteNothing: "The clipboard holds no TLL preset.",
};

const Message = ({ message, t }: { message: PlannerMessage; t: Translate }) => {
  const text = t("Planner.Message." + message.key, messageText[message.key] ?? message.key);
  return <div className={styles.message}>{message.argument ? `${text} ${message.argument}` : text}</div>;
};

/** The check bar: what keeps the plan from running first, then what makes it slow. */
const Checks = ({ info, t }: { info: PlannerInfo; t: Translate }) => {
  const [all, setAll] = useState(false);
  const findings = [...info.findings].sort((a, b) => a.severity - b.severity);
  const shown = all ? findings : findings.slice(0, 3);
  const errors = findings.filter((f) => f.severity === Severity.Error).length;
  return (
    <div className={styles.checks}>
      {findings.length === 0 && (
        <div className={styles.check}>
          <div className={classNames(styles.checkIcon, styles.checkOk)} />
          <div className={styles.checkText}>{t("Planner.AllGood", "Every way through the junction gets green, and no paths cross.")}</div>
        </div>
      )}
      {findings.length > 0 && errors === 0 && (
        <div className={styles.check}>
          <div className={classNames(styles.checkIcon, styles.checkOk)} />
          <div className={styles.checkText}>{t("Planner.RunsFine", "The plan can run.")}</div>
        </div>
      )}
      {shown.map((f, i) => (
        <FindingRow key={i} info={info} finding={f} t={t} />
      ))}
      {findings.length > 3 && (
        <div className={styles.row}>
          <LinkButton label={all ? t("Planner.Fewer", "Show fewer") : `${t("Planner.More", "Show all")} (${findings.length})`} onSelect={() => setAll(!all)} />
        </div>
      )}
    </div>
  );
};

const severityClass: Record<Severity, string> = {
  [Severity.Error]: styles.checkError,
  [Severity.Warning]: styles.checkWarning,
  [Severity.Info]: styles.checkInfo,
};

const severityMark: Record<Severity, string> = {
  [Severity.Error]: "×",
  [Severity.Warning]: "!",
  [Severity.Info]: "i",
};

const FindingRow = ({ info, finding: f, t }: { info: PlannerInfo; finding: Finding; t: Translate }) => {
  const name = (m: number) => movementName(info, m, t);
  const phase = (p: number) => `${t("Planner.Phase", "Phase")} ${p + 1}`;
  let text = "";
  let fix: ReactNode = null;
  switch (f.kind) {
    case FindingKind.NoGreen:
      text = `${name(f.movement)}: ${t("Planner.Check.NoGreen", "green in no phase. Its traffic would wait forever.")}`;
      fix = <LinkButton label={t("Planner.Fix.Place", "Give it green")} onSelect={() => planner.place(f.movement)} />;
      break;
    case FindingKind.Crossing:
      text = `${phase(f.phase)}: ${name(f.movement)} ${t("Planner.Check.CrossesWith", "crosses")} ${name(f.other)}.`;
      fix = <LinkButton label={t("Planner.Fix.TakeOut", "Take the second out")} onSelect={() => planner.removeFrom(f.other, f.phase)} />;
      break;
    case FindingKind.SplitLane:
      text = `${name(f.movement)} ${t("Planner.Check.And", "and")} ${name(f.other)}: ${t("Planner.Check.SplitLane", "one lane, different greens. The first car blocks the others.")}`;
      fix = (
        <>
          <LinkButton label={t("Planner.Fix.Join", "Give them the same greens")} onSelect={() => planner.joinLane(f.movement)} />
          <LinkButton label={t("Planner.Fix.Lanes", "Change the lanes")} onSelect={() => step$.update("lanes")} />
        </>
      );
      break;
    case FindingKind.TooManyPhases:
      text = t("Planner.Check.TooMany", "More than 16 phases. The game has no more signal groups; delete or merge phases.");
      break;
    case FindingKind.HeavyYield:
      text = `${name(f.movement)}: ${t("Planner.Check.HeavyYield", "gives way to")} ${Math.round(f.value)}/h ${t("Planner.Check.HeavyYield2", "and will hardly get through.")}`;
      fix = <LinkButton label={t("Planner.Fix.Protect", "Protect it")} onSelect={() => planner.protect(f.movement, f.phase)} />;
      break;
    case FindingKind.LongCycle:
      text = `${t("Planner.Check.LongCycle", "The cycle can reach")} ${seconds(f.value)}. ${t("Planner.Check.LongCycle2", "Everyone waits long at red; shorter maxima help.")}`;
      break;
    case FindingKind.EmptyPhase:
      text = `${phase(f.phase)}: ${t("Planner.Check.Empty", "gives green to nothing.")}`;
      fix = <LinkButton label={t("Planner.Delete", "Delete")} onSelect={() => planner.deletePhase(f.phase)} />;
      break;
    case FindingKind.DuplicatePhase:
      text = `${phase(f.phase)}: ${t("Planner.Check.Duplicate", "the same as")} ${phase(f.other)}.`;
      fix = <LinkButton label={t("Planner.Delete", "Delete")} onSelect={() => planner.deletePhase(f.phase)} />;
      break;
  }
  return (
    <div className={styles.check}>
      <div className={classNames(styles.checkIcon, severityClass[f.severity])}>{severityMark[f.severity]}</div>
      <div className={styles.checkText}>{text}</div>
      {fix}
    </div>
  );
};

const Footer = ({ info, t }: { info: PlannerInfo; t: Translate }) => (
  <div className={styles.footer}>
    <Hint text={t("Planner.LiveHint", "")}>
      <div className={styles.faint}>{t("Planner.Live", "Live changes")}</div>
    </Hint>
    <Button variant="flat" className={styles.switchButton} onSelect={() => planner.setLive(!info.live)}>
      <div className={classNames(styles.switch, info.live && styles.switchOn)}>
        <div className={classNames(styles.knob, info.live && styles.knobOn)} />
      </div>
    </Button>
    {info.live && !info.dirty && <div className={styles.liveNote}>{t("Planner.IsLive", "Changes are live")}</div>}
    {info.live && info.dirty && !info.canApply && <div className={styles.pending}>{t("Planner.NotLive", "Not live until fixed")}</div>}
    <div className={styles.spacer} />
    {!info.live && (
      <>
        <Button variant="flat" className={classNames(styles.secondary, !info.dirty && styles.toolDisabled)} disabled={!info.dirty} onSelect={planner.discard}>
          {t("Planner.Discard", "Discard")}
        </Button>
        <Hint text={info.dirty && !info.canApply ? t("Planner.ApplyBlocked", "Fix the errors the check shows first.") : null}>
          <Button variant="flat" className={classNames(styles.primary, !info.canApply && styles.primaryDisabled)} disabled={!info.canApply} onSelect={planner.apply}>
            {t("Planner.Apply", "Apply")}
          </Button>
        </Hint>
      </>
    )}
  </div>
);
