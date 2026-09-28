import { Approach, Movement, MovementKind } from "bindings";
import { arrow, arrowHead, arrowPath, at, centre, crosswalkEnds, frame, roadEnd, size } from "diagram-geometry";
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

/**
 * A small plan of the junction with one arrow per movement that has green.
 * It is turned to match the camera, so "left" in the diagram is left on
 * screen. Arrows keep to the side of the road the city drives on.
 */
export const JunctionDiagram = ({ approaches, movements, green, permitted, leftHandTraffic, cameraYaw, className }: Props) => {
  const f = frame(approaches, cameraYaw, leftHandTraffic);

  const arrows = green.map((m) => {
    const mv = movements[m];
    if (!mv) return null;
    const colour = permitted.includes(m) ? styles.permitted : mv.kind === MovementKind.Track ? styles.track : styles.protected;

    if (mv.kind === MovementKind.Pedestrian) {
      const ends = crosswalkEnds(f, mv.source);
      if (!ends) return null;
      return <line key={m} x1={ends[0].x} y1={ends[0].y} x2={ends[1].x} y2={ends[1].y} className={`${styles.crosswalk} ${colour}`} />;
    }
    const a = arrow(f, mv.source, mv.target);
    if (!a) return null;
    return (
      <g key={m} className={colour}>
        <path d={arrowPath(a)} className={styles.arrow} />
        <path d={arrowHead(a)} className={styles.head} />
      </g>
    );
  });

  return (
    <svg viewBox={`0 0 ${size} ${size}`} className={className ?? styles.diagram}>
      {f.dirs.map((d, i) => {
        const end = at(d, roadEnd);
        return <line key={`road${i}`} x1={centre} y1={centre} x2={end.x} y2={end.y} className={styles.road} />;
      })}
      <circle cx={centre} cy={centre} r={14} className={styles.junction} />
      {arrows}
    </svg>
  );
};
