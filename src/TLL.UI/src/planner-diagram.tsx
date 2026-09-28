import classNames from "classnames";
import { MouseEvent, useRef, useState } from "react";
import { MovementKind } from "bindings";
import {
  along,
  Arrow,
  arrow,
  arrowHead,
  arrowPath,
  at,
  centre,
  crosswalkEnds,
  Frame,
  frame,
  laneOffset,
  roadEnd,
  size,
  toSegment,
  Vec,
} from "diagram-geometry";
import { PlannerInfo } from "planner-bindings";
import styles from "planner.module.scss";

interface Props {
  info: PlannerInfo;
  /** The movements with green, and those of them giving way; empty to draw only the roads. */
  green: number[];
  permitted: number[];
  /** Movements a click would add; the others not in green are refused, and drawn fainter. */
  addable: number[];
  /** A refused movement and what stood against it, flashed red. */
  refused: number;
  blocking: number[];
  /** Movements to draw strong whatever their state, e.g. the ones a finding is about. */
  marked?: number[];
  /** Called with the movement under the pointer, -1 for none. */
  onHover: (movement: number) => void;
  onClick: (movement: number) => void;
}

/** How close the pointer must come to an arrow, in viewBox units, to pick it. */
const pickRadius = 7;

/**
 * Where each vehicle movement's arrow leaves its road: spread across the
 * road like lanes, from the kerb (the turn on the driving side) to the
 * middle (the turn across oncoming traffic), so the arrows of one road can
 * be told apart and clicked.
 */
function offsets(info: PlannerInfo, f: Frame): number[] {
  const result = info.movements.map(() => laneOffset);
  for (let s = 0; s < info.approaches.length; s++) {
    const own = info.movements
      .map((m, i) => ({ m, i }))
      .filter(({ m }) => m.source === s && m.kind !== MovementKind.Pedestrian && m.target >= 0 && m.target < f.dirs.length);
    if (own.length < 2) continue;
    const u = f.dirs[s];
    // Turn of each movement as seen by the driver, positive to the left.
    const turn = (target: number) => {
      const v = f.dirs[target];
      const heading = { x: -u.x, y: -u.y };
      return Math.atan2(heading.x * v.y - heading.y * v.x, heading.x * v.x + heading.y * v.y);
    };
    const kerbFirst = own.sort((a, b) => (info.leftHandTraffic ? turn(b.m.target) - turn(a.m.target) : turn(a.m.target) - turn(b.m.target)));
    // From the kerb (far from the middle) to the middle of the road.
    const outer = 10.5;
    const inner = 2.5;
    kerbFirst.forEach(({ i }, k) => {
      result[i] = outer - ((outer - inner) * (k + 0.5)) / kerbFirst.length;
    });
  }
  return result;
}

/**
 * The planner's plan of the junction: every movement as an arrow, the ones
 * with green in the selected phase strong, those giving way with a yield
 * triangle, the others faint. The arrow under the pointer is highlighted and
 * shown on the road; a click on it gives it green or takes it away.
 *
 * The pointer is matched to the arrows by distance to the drawn curves,
 * not by the SVG's own hit testing, which the game's UI engine does not
 * offer for the parts of an inline SVG.
 */
export const PlannerDiagram = ({ info, green, permitted, addable, refused, blocking, marked, onHover, onClick }: Props) => {
  const f = frame(info.approaches, info.cameraYaw, info.leftHandTraffic);
  const box = useRef<HTMLDivElement>(null);
  const [hovered, setHovered] = useState(-1);
  const lanes = offsets(info, f);

  const arrows: (Arrow | null)[] = info.movements.map((m, i) =>
    m.kind === MovementKind.Pedestrian ? null : arrow(f, m.source, m.target, lanes[i]),
  );
  const crosswalks: ([Vec, Vec] | null)[] = info.movements.map((m) => (m.kind === MovementKind.Pedestrian ? crosswalkEnds(f, m.source, 11) : null));

  const pick = (p: Vec) => {
    let best = -1;
    let bestDistance = pickRadius;
    info.movements.forEach((_, i) => {
      let d = Infinity;
      const a = arrows[i];
      if (a) {
        // The start is shared with the road's other arrows; the part that
        // tells them apart is further in.
        for (let t = 0.2; t <= 1.001; t += 0.05) {
          const q = along(a, t);
          d = Math.min(d, Math.hypot(q.x - p.x, q.y - p.y));
        }
      }
      const c = crosswalks[i];
      if (c) d = toSegment(p, c[0], c[1]);
      if (d < bestDistance) {
        bestDistance = d;
        best = i;
      }
    });
    return best;
  };

  const pointer = (e: MouseEvent<HTMLDivElement>): Vec | null => {
    const r = box.current?.getBoundingClientRect();
    if (!r || r.width <= 0 || r.height <= 0) return null;
    return { x: ((e.clientX - r.left) * size) / r.width, y: ((e.clientY - r.top) * size) / r.height };
  };

  const move = (e: MouseEvent<HTMLDivElement>) => {
    const p = pointer(e);
    const m = p ? pick(p) : -1;
    if (m !== hovered) {
      setHovered(m);
      onHover(m);
    }
  };

  const leave = () => {
    if (hovered !== -1) {
      setHovered(-1);
      onHover(-1);
    }
  };

  const click = (e: MouseEvent<HTMLDivElement>) => {
    const p = pointer(e);
    const m = p ? pick(p) : -1;
    if (m >= 0) onClick(m);
  };

  const state = (i: number) => {
    const on = green.includes(i);
    return {
      on,
      yields: on && permitted.includes(i),
      free: !on && addable.includes(i),
      refused: i === refused,
      blocking: blocking.includes(i),
      hovered: i === hovered,
      marked: marked?.includes(i) ?? false,
      partner: hovered >= 0 && info.movements[hovered]?.partners.includes(i),
    };
  };

  // Faint arrows first, strong ones over them, the hovered one on top.
  const order = info.movements.map((_, i) => i).sort((a, b) => rank(state(a)) - rank(state(b)));

  return (
    <div ref={box} className={styles.diagramBox} onMouseMove={move} onMouseLeave={leave} onClick={click}>
      <svg viewBox={`0 0 ${size} ${size}`} className={styles.diagramSvg}>
        {f.dirs.map((d, i) => {
          const end = at(d, roadEnd + 4);
          return <line key={`road${i}`} x1={centre} y1={centre} x2={end.x} y2={end.y} className={classNames(styles.road, info.mainRoad.includes(i) && styles.roadMain)} />;
        })}
        <circle cx={centre} cy={centre} r={15} className={styles.junctionFill} />
        {order.map((i) => {
          const s = state(i);
          const tone = classNames(
            s.on ? (info.movements[i].kind === MovementKind.Track ? styles.onTrack : styles.on) : s.free ? styles.free : styles.off,
            s.partner && styles.partner,
            s.marked && styles.marked,
            s.blocking && styles.blocking,
            s.refused && styles.refused,
            s.hovered && styles.hovered,
          );
          const c = crosswalks[i];
          if (c) return <line key={i} x1={c[0].x} y1={c[0].y} x2={c[1].x} y2={c[1].y} className={classNames(styles.zebra, tone)} />;
          const a = arrows[i];
          if (!a) return null;
          const yieldAt = along(a, 0.72);
          return (
            <g key={i} className={tone}>
              <path d={arrowPath(a)} className={classNames(styles.arrowLine, info.movements[i].kind === MovementKind.Track && styles.rails)} />
              <path d={arrowHead(a, 1.1)} className={styles.arrowHead} />
              {s.yields && (
                <path
                  d={`M ${(yieldAt.x - 3.4).toFixed(1)} ${(yieldAt.y - 2.4).toFixed(1)} L ${(yieldAt.x + 3.4).toFixed(1)} ${(yieldAt.y - 2.4).toFixed(1)} L ${yieldAt.x.toFixed(1)} ${(yieldAt.y + 3.4).toFixed(1)} Z`}
                  className={styles.yieldSign}
                />
              )}
            </g>
          );
        })}
      </svg>
      {f.dirs.map((d, i) => {
        // Road names at the ends of the roads, outside the arrows.
        const p = at(d, roadEnd - 2);
        const name = info.approaches[i]?.name || `${i + 1}`;
        return (
          <div
            key={`name${i}`}
            className={classNames(styles.roadName, info.mainRoad.includes(i) && styles.roadNameMain)}
            style={{ left: `${(100 * p.x) / size}%`, top: `${(100 * p.y) / size}%` }}
          >
            {info.mainRoad.includes(i) ? `★ ${name}` : name}
          </div>
        );
      })}
    </div>
  );
};

/** Drawing order: faint below, green above, what the player points at on top. */
function rank(s: { on: boolean; free: boolean; hovered: boolean; refused: boolean; blocking: boolean; marked: boolean }): number {
  if (s.hovered) return 5;
  if (s.refused || s.blocking) return 4;
  if (s.marked) return 3;
  if (s.on) return 2;
  return s.free ? 1 : 0;
}
