import { Approach, Movement, MovementKind } from "bindings";
import styles from "junction-diagram.module.scss";

interface Props {
  approaches: Approach[];
  movements: Movement[];
  /** Indices of the movements to draw. */
  green: number[];
  /** Subset of green that has to give way. */
  permitted: number[];
  leftHandTraffic: boolean;
  cameraYaw: number;
  /** Replaces the default size, e.g. for small diagrams in a list. */
  className?: string;
}

type Vec = { x: number; y: number };

const size = 120;
const centre = size / 2;
/** Where movement arrows start and end, from the centre. */
const mouth = 34;
/** Where a crosswalk crosses its road. */
const crosswalk = 44;
/** Half the distance between the two directions of a road. */
const laneOffset = 6;

/**
 * A small plan of the junction with one arrow per movement that has green.
 * It is turned to match the camera, so "left" in the diagram is left on
 * screen. Arrows keep to the side of the road the city drives on.
 */
export const JunctionDiagram = ({ approaches, movements, green, permitted, leftHandTraffic, cameraYaw, className }: Props) => {
  const yaw = (cameraYaw * Math.PI) / 180;
  // World direction to screen direction (x right, y up). The camera looks
  // along (sin yaw, cos yaw) in the world's x-z plane.
  const onScreen = (a: Approach): Vec => ({
    x: a.x * Math.cos(yaw) - a.z * Math.sin(yaw),
    y: a.x * Math.sin(yaw) + a.z * Math.cos(yaw),
  });
  const dirs = approaches.map(onScreen);
  // Side of the travel direction on which traffic drives.
  const side = (v: Vec): Vec => (leftHandTraffic ? { x: -v.y, y: v.x } : { x: v.y, y: -v.x });
  const at = (v: Vec, r: number, s: Vec = { x: 0, y: 0 }, w = 0) => ({ x: centre + v.x * r + s.x * w, y: centre - (v.y * r + s.y * w) });
  const pt = (p: Vec) => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`;

  const arrows = green.map((m) => {
    const mv = movements[m];
    if (!mv || mv.source < 0 || mv.source >= dirs.length) return null;
    const u = dirs[mv.source];
    const colour = permitted.includes(m) ? styles.permitted : mv.kind === MovementKind.Track ? styles.track : styles.protected;

    if (mv.kind === MovementKind.Pedestrian) {
      const across = { x: u.y, y: -u.x };
      const a = at(u, crosswalk, across, 9);
      const b = at(u, crosswalk, across, -9);
      return <line key={m} x1={a.x} y1={a.y} x2={b.x} y2={b.y} className={`${styles.crosswalk} ${colour}`} />;
    }
    if (mv.target < 0 || mv.target >= dirs.length) return null;
    const v = dirs[mv.target];
    const entry = at(u, mouth, side({ x: -u.x, y: -u.y }), laneOffset);
    const exit = at(v, mouth, side(v), laneOffset);
    const tip = { x: exit.x + v.x * 7, y: exit.y - v.y * 7 };
    const wing = { x: v.y * 3.5, y: v.x * 3.5 };
    const head = `M ${pt(tip)} L ${pt({ x: exit.x + wing.x, y: exit.y + wing.y })} L ${pt({ x: exit.x - wing.x, y: exit.y - wing.y })} Z`;
    return (
      <g key={m} className={colour}>
        <path d={`M ${pt(entry)} Q ${centre} ${centre} ${pt(exit)}`} className={styles.arrow} />
        <path d={head} className={styles.head} />
      </g>
    );
  });

  return (
    <svg viewBox={`0 0 ${size} ${size}`} className={className ?? styles.diagram}>
      {dirs.map((d, i) => {
        const end = at(d, 58);
        return <line key={`road${i}`} x1={centre} y1={centre} x2={end.x} y2={end.y} className={styles.road} />;
      })}
      <circle cx={centre} cy={centre} r={14} className={styles.junction} />
      {arrows}
    </svg>
  );
};
