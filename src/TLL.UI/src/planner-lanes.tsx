import { useValue } from "cs2/api";
import { Button } from "cs2/ui";
import classNames from "classnames";
import { useState } from "react";
import { actions, JunctionInfo, laneToolActive$, MovementKind, selected$, Turn, TurnState } from "bindings";
import { JunctionDiagram } from "junction-diagram";
import { ApproachLanes, LaneEdit, LaneUse, planner, PlannerInfo, Solid } from "planner-bindings";
import { Hint, kindName, LinkButton, roadName, Stepper, Tool, Translate } from "planner-parts";
import styles from "planner.module.scss";

// Step 1 of the planner: the lanes. Per road leading in, its lanes as tiles
// with the arrows painted on the road; a click on a tile shows its
// directions to switch. Then solid lines before the junction, and the turn
// rules.

/** The kind of turn from <approach> into <target>, as the junction's movements have it. */
function kindOf(info: PlannerInfo, approach: number, target: number): MovementKind {
  const mv = info.movements.find((m) => m.source === approach && m.target === target && m.kind !== MovementKind.Pedestrian && m.kind !== MovementKind.Track);
  return mv ? mv.kind : MovementKind.Straight;
}

const laneEditText: Record<LaneEdit, string> = {
  [LaneEdit.Done]: "",
  [LaneEdit.LastDirection]: "A lane has to lead somewhere.",
  [LaneEdit.Gap]: "A lane serves neighbouring directions only.",
  [LaneEdit.Cross]: "The paths of two lanes would cross.",
  [LaneEdit.Uncovered]: "This direction would lose its last lane.",
  [LaneEdit.TooMany]: "The road it leads into has fewer lanes.",
};

export const laneEditReason = (edit: LaneEdit, t: Translate) => t("Planner.LaneEdit." + LaneEdit[edit], laneEditText[edit]);

/**
 * One lane's arrows as painted on the road: a shaft from the bottom, a
 * head for each direction it serves. Drawn, not typed, since the game's
 * font need not have the arrow characters.
 */
const LaneArrows = ({ kinds, className }: { kinds: MovementKind[]; className: string }) => (
  <svg viewBox="0 0 24 32" className={className}>
    <line x1={12} y1={31} x2={12} y2={17} className={styles.laneMark} />
    {kinds.includes(MovementKind.Straight) && (
      <>
        <line x1={12} y1={18} x2={12} y2={7} className={styles.laneMark} />
        <path d="M 12 2 L 7.5 8.5 L 16.5 8.5 Z" className={styles.laneHead} />
      </>
    )}
    {kinds.includes(MovementKind.Left) && (
      <>
        <path d="M 12 19 Q 12 11 6 11" className={styles.laneMark} />
        <path d="M 1.5 11 L 7 6.5 L 7 15.5 Z" className={styles.laneHead} />
      </>
    )}
    {kinds.includes(MovementKind.Right) && (
      <>
        <path d="M 12 19 Q 12 11 18 11" className={styles.laneMark} />
        <path d="M 22.5 11 L 17 6.5 L 17 15.5 Z" className={styles.laneHead} />
      </>
    )}
  </svg>
);

/** The kinds a lane serves, from its range of targets. */
const kindsOf = (info: PlannerInfo, lanes: ApproachLanes, use: LaneUse) =>
  lanes.targets.slice(use.first, use.last + 1).map((target) => kindOf(info, lanes.approach, target));

/**
 * The order to draw lanes in: as the driver sees them, left to right. The
 * lanes come from the kerb outwards, which is right to left in right-hand
 * traffic.
 */
const driverOrder = (count: number, leftHandTraffic: boolean) => {
  const order = Array.from({ length: count }, (_, i) => i);
  return leftHandTraffic ? order : order.reverse();
};

const LaneStrip = ({ info, lanes, uses, selected, onSelect, small }: {
  info: PlannerInfo;
  lanes: ApproachLanes;
  uses: LaneUse[];
  selected: number;
  onSelect?: (lane: number) => void;
  small?: boolean;
}) => (
  <div className={styles.laneStrip}>
    {driverOrder(uses.length, info.leftHandTraffic).map((j) => {
      const tile = <LaneArrows kinds={kindsOf(info, lanes, uses[j])} className={small ? styles.laneSvgSmall : styles.laneSvg} />;
      return onSelect ? (
        <Button key={j} variant="flat" className={classNames(styles.laneTile, j === selected && styles.laneTileOn)} onSelect={() => onSelect(j)}>
          {tile}
        </Button>
      ) : (
        <div key={j} className={classNames(styles.laneTile, styles.laneTileSmall)}>
          {tile}
        </div>
      );
    })}
  </div>
);

const RoadLanes = ({ info, lanes, t }: { info: PlannerInfo; lanes: ApproachLanes; t: Translate }) => {
  const [lane, setLane] = useState(-1);
  const selected = lane >= 0 && lane < lanes.uses.length ? lane : -1;
  const use = selected >= 0 ? lanes.uses[selected] : null;
  return (
    <div className={classNames(styles.card, lanes.changed && styles.cardChanged)}>
      <div className={styles.row}>
        <div className={styles.grow}>{`${t("Planner.From", "From")} ${roadName(info, lanes.approach)}`}</div>
        {lanes.changed && <div className={styles.faint}>{t("Planner.LanesChanged", "changed")}</div>}
        {(lanes.ruled || lanes.changed) && (
          <LinkButton label={t("Planner.GameLanes", "The game's lanes")} hint={t("Planner.GameLanesHint", "")} onSelect={() => planner.resetLanes(lanes.approach)} />
        )}
      </div>
      <LaneStrip info={info} lanes={lanes} uses={lanes.uses} selected={selected} onSelect={(j) => setLane(j === selected ? -1 : j)} />
      {use ? (
        <div className={styles.row}>
          <div className={styles.faint}>{`${t("Planner.LaneN", "Lane")} ${driverOrder(lanes.uses.length, info.leftHandTraffic).indexOf(selected) + 1} ${t("Planner.FromLeft", "from the left")}:`}</div>
          {lanes.targets.map((target, m) => {
            const on = m >= use.first && m <= use.last;
            const edit = lanes.options[selected]?.[m] ?? LaneEdit.Done;
            const label = `${kindName(kindOf(info, lanes.approach, target), t)} (${roadName(info, target)})`;
            return (
              <Hint key={m} text={edit !== LaneEdit.Done ? laneEditReason(edit, t) : null}>
                <Button
                  variant="flat"
                  className={classNames(styles.direction, on && styles.directionOn, edit !== LaneEdit.Done && styles.toolDisabled)}
                  disabled={edit !== LaneEdit.Done}
                  onSelect={() => planner.toggleLane(lanes.approach, selected, m)}
                >
                  {label}
                </Button>
              </Hint>
            );
          })}
        </div>
      ) : (
        <div className={styles.faintRow}>{t("Planner.PickLane", "Click a lane to change where it leads.")}</div>
      )}
      {lanes.suggestion && (
        <div className={styles.row}>
          <div className={styles.faint}>{`${t("Planner.Suggested", "For the traffic")}:`}</div>
          <LaneStrip info={info} lanes={lanes} uses={lanes.suggestion} selected={-1} small />
          <div className={styles.faint}>{`${t("Planner.BusiestLane", "busiest lane")} −${Math.round(lanes.gain * 100)} %`}</div>
          <div className={styles.spacer} />
          <LinkButton label={t("Planner.UseSuggestion", "Use")} hint={t("Planner.SuggestionHint", "")} onSelect={() => planner.useLaneSuggestion(lanes.approach)} />
        </div>
      )}
    </div>
  );
};

const SolidRow = ({ info, solid, t }: { info: PlannerInfo; solid: Solid; t: Translate }) => {
  const format = (pieces: number) =>
    pieces <= 0 ? t("Planner.SolidOff", "off") : `${Math.round(solid.lengths[Math.min(pieces, solid.lengths.length) - 1] ?? 0)} m`;
  return (
    <div className={styles.row}>
      <div className={styles.grow}>{roadName(info, solid.approach)}</div>
      <Stepper
        label=""
        value={solid.pieces}
        min={0}
        max={solid.lengths.length}
        step={1}
        format={format}
        hint={t("Planner.SolidStepHint", "")}
        onChange={(v) => planner.setSolid(solid.approach, v)}
      />
    </div>
  );
};

const turnText: Record<TurnState, string> = {
  [TurnState.Allowed]: "allowed",
  [TurnState.ForbiddenByAutopilot]: "forbidden · autopilot",
  [TurnState.ForbiddenByPlayer]: "forbidden · you",
  [TurnState.AllowedByPlayer]: "allowed · you",
  [TurnState.ForbiddenByGame]: "forbidden · road",
};

export const LanesStep = ({ info, t }: { info: PlannerInfo; t: Translate }) => {
  const junction: JunctionInfo | null = useValue(selected$);
  const laneTool = useValue(laneToolActive$);
  const turns: Turn[] = junction?.turns ?? [];
  const lanes = info.lanes ?? [];
  const solid = (info.solid ?? []).filter((s) => s.lengths.length > 0);
  return (
    <>
      <Hint text={t("Planner.LanesHint", "")}>
        <div className={styles.label}>{t("Planner.LaneArrows", "Lane arrows")}</div>
      </Hint>
      {lanes.length === 0 && <div className={styles.muted}>{t("Planner.NoLaneChoice", "No road here has more than one lane leading in, so there are no arrows to choose.")}</div>}
      {lanes.map((l) => (
        <RoadLanes key={l.approach} info={info} lanes={l} t={t} />
      ))}

      {solid.length > 0 && (
        <>
          <Hint text={t("Planner.SolidHint", "")}>
            <div className={styles.label}>{t("Planner.Solid", "Solid lines before the junction")}</div>
          </Hint>
          <div className={styles.muted}>{t("Planner.SolidNote", "Drivers choose their lane before the solid lines and keep it there.")}</div>
          {solid.map((s) => (
            <SolidRow key={s.approach} info={info} solid={s} t={t} />
          ))}
        </>
      )}

      <Hint text={t("Panel.TurnsHint", "")}>
        <div className={styles.label}>{t("Panel.Turns", "Turns")}</div>
      </Hint>
      <div className={styles.muted}>{t("Planner.TurnsNote", "Allowing or forbidding a turn changes the junction's lanes at once. A plan you have not applied is loaded again then.")}</div>
      {turns.map((turn) => (
        <div key={`${turn.source}-${turn.target}`} className={styles.timeRow}>
          <JunctionDiagram
            approaches={info.approaches}
            movements={[{ kind: turn.kind, source: turn.source, target: turn.target, volume: turn.volume }]}
            green={[0]}
            permitted={[]}
            leftHandTraffic={info.leftHandTraffic}
            cameraYaw={info.cameraYaw}
            className={styles.timeDiagram}
          />
          <div className={styles.grow}>{`${roadName(info, turn.source)} → ${roadName(info, turn.target)}`}</div>
          <Hint text={t("TurnStateHint." + TurnState[turn.state], "")}>
            <Button variant="flat" className={styles.link} disabled={turn.state === TurnState.ForbiddenByGame} onSelect={() => actions.cycleTurn(turn.source, turn.target)}>
              {t("TurnState." + TurnState[turn.state], turnText[turn.state])}
            </Button>
          </Hint>
        </div>
      ))}
      <div className={styles.row}>
        <Tool
          label={laneTool ? t("Panel.LaneToolActive", "Click a lane leading in, then one leading out…") : t("Planner.ExactLanes", "Connect single lanes on the map")}
          hint={t("Panel.LaneToolHint", "")}
          on={laneTool}
          onSelect={actions.toggleLaneTool}
        />
      </div>
    </>
  );
};
