// Building blocks the planner's panel shares between its steps
// (planner.tsx, planner-lanes.tsx).
import { bindLocalValue } from "cs2/api";
import { Button, Tooltip } from "cs2/ui";
import classNames from "classnames";
import { ReactElement } from "react";
import { MovementKind } from "bindings";
import { useTranslate } from "localization";
import { PlannerInfo } from "planner-bindings";
import styles from "planner.module.scss";

export type Translate = ReturnType<typeof useTranslate>;
export type Step = "lanes" | "phases" | "times";
export type Sheet = "templates" | "presets" | "none";

/** The open step and sheet, kept while the planner is closed, so it opens where the player left it. */
export const step$ = bindLocalValue<Step>("phases");
export const sheet$ = bindLocalValue<Sheet>("none");

/** Wraps an element in the game's tooltip, or leaves it alone without text. */
export const Hint = ({ text, children }: { text: string | null; children: ReactElement }) =>
  text ? <Tooltip tooltip={<div className={styles.tooltip}>{text}</div>}>{children as any}</Tooltip> : children;

export const seconds = (s: number) => `${Math.round(s)} s`;

export function rows<T>(items: T[], size: number): T[][] {
  const result: T[][] = [];
  for (let i = 0; i < items.length; i += size) result.push(items.slice(i, i + size));
  return result;
}

export const roadName = (info: PlannerInfo, approach: number) => info.approaches[approach]?.name || `${approach + 1}`;

const kindText: Record<MovementKind, string> = {
  [MovementKind.Straight]: "straight on",
  [MovementKind.Left]: "left",
  [MovementKind.Right]: "right",
  [MovementKind.UTurn]: "U-turn",
  [MovementKind.Pedestrian]: "crosswalk",
  [MovementKind.Track]: "tram",
};

export const kindName = (kind: MovementKind, t: Translate) => t("Planner.Kind." + MovementKind[kind], kindText[kind]);

/** A movement as a short text: "Main Street → Oak Avenue, left", "Crosswalk over Main Street". */
export function movementName(info: PlannerInfo, m: number, t: Translate): string {
  const mv = info.movements[m];
  if (!mv) return "";
  if (mv.kind === MovementKind.Pedestrian) return `${t("Planner.CrosswalkOver", "Crosswalk over")} ${roadName(info, mv.source)}`;
  return `${roadName(info, mv.source)} → ${roadName(info, mv.target)}, ${kindName(mv.kind, t)}`;
}

export const Tool = ({ label, hint, on, disabled, onSelect }: { label: string; hint?: string; on?: boolean; disabled?: boolean; onSelect: () => void }) => (
  <Hint text={hint ?? null}>
    <Button variant="flat" className={classNames(styles.tool, on && styles.toolOn, disabled && styles.toolDisabled)} disabled={disabled} onSelect={onSelect}>
      {label}
    </Button>
  </Hint>
);

export const LinkButton = ({ label, hint, onSelect }: { label: string; hint?: string; onSelect: () => void }) => (
  <Hint text={hint ?? null}>
    <Button variant="flat" className={styles.link} onSelect={onSelect}>
      {label}
    </Button>
  </Hint>
);

export const Switch = ({ label, hint, on, onSelect }: { label: string; hint: string; on: boolean; onSelect: () => void }) => (
  <div className={styles.row}>
    <Hint text={hint || null}>
      <div className={styles.grow}>{label}</div>
    </Hint>
    <Button variant="flat" className={styles.switchButton} onSelect={onSelect}>
      <div className={classNames(styles.switch, on && styles.switchOn)}>
        <div className={classNames(styles.knob, on && styles.knobOn)} />
      </div>
    </Button>
  </div>
);

export function Segments<T extends number>({ items, value, label, hint, onSelect }: {
  items: T[];
  value: T;
  label: (item: T) => string;
  hint: (item: T) => string;
  onSelect: (item: T) => void;
}) {
  return (
    <>
      {rows(items, 3).map((row, i) => (
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

/**
 * A number set by buttons: the game offers no slider. For times the step
 * grows with the value, so short times are set to the second and long ones
 * quickly.
 */
export const Stepper = ({ label, value, min, max, onChange, hint, format, step }: {
  label: string;
  value: number;
  min: number;
  max?: number;
  onChange: (v: number) => void;
  hint?: string;
  format?: (v: number) => string;
  step?: number;
}) => {
  const by = step ?? (value < 10 ? 1 : value < 30 ? 2 : 5);
  const text = format ? format(value) : `${Math.round(value * 10) / 10} s`;
  // The game's tooltip holds one focusable element, so it goes on the label
  // and the value, not around the two buttons.
  return (
    <div className={styles.stepper}>
      {label && (
        <Hint text={hint ?? null}>
          <div className={styles.stepperLabel}>{label}</div>
        </Hint>
      )}
      <Button variant="flat" className={styles.stepperButton} disabled={value <= min} onSelect={() => onChange(Math.max(min, value - by))}>
        −
      </Button>
      <Hint text={hint ?? null}>
        <div className={classNames(styles.stepperValue, format && styles.stepperWide)}>{text}</div>
      </Hint>
      <Button variant="flat" className={styles.stepperButton} disabled={max !== undefined && value >= max} onSelect={() => onChange(max !== undefined ? Math.min(max, value + by) : value + by)}>
        +
      </Button>
    </div>
  );
};
