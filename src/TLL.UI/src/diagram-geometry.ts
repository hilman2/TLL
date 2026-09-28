// Where the junction diagrams draw roads, arrows and crosswalks, in the
// units of their viewBox (0 to 120, y down). The small diagram of the panel
// (junction-diagram.tsx) and the planner's large one (planner-diagram.tsx)
// share it, and the planner finds the arrow under the pointer with the same
// curves it draws.
import { Approach, MovementKind } from "bindings";

export type Vec = { x: number; y: number };

export const size = 120;
export const centre = size / 2;
/** Where movement arrows start and end, from the centre. */
export const mouth = 34;
/** Where a crosswalk crosses its road. */
export const crosswalk = 44;
/** Half the distance between the two directions of a road. */
export const laneOffset = 6;
/** Where a road ends, from the centre. */
export const roadEnd = 58;

/** The approaches' directions on screen, turned to match the camera, and the side traffic keeps to. */
export interface Frame {
  dirs: Vec[];
  leftHandTraffic: boolean;
}

/**
 * The diagram is turned to match the camera, so "left" in the diagram is
 * left on screen. The camera looks along (sin yaw, cos yaw) in the world's
 * x-z plane; screen x is right, y up.
 */
export function frame(approaches: Approach[], cameraYaw: number, leftHandTraffic: boolean): Frame {
  const yaw = (cameraYaw * Math.PI) / 180;
  const dirs = approaches.map((a) => ({
    x: a.x * Math.cos(yaw) - a.z * Math.sin(yaw),
    y: a.x * Math.sin(yaw) + a.z * Math.cos(yaw),
  }));
  return { dirs, leftHandTraffic };
}

/** The side of travel direction <v> on which traffic drives. */
export function side(f: Frame, v: Vec): Vec {
  return f.leftHandTraffic ? { x: -v.y, y: v.x } : { x: v.y, y: -v.x };
}

/** The point <r> out along direction <v>, moved <w> towards <s>, in viewBox units. */
export function at(v: Vec, r: number, s: Vec = { x: 0, y: 0 }, w = 0): Vec {
  return { x: centre + v.x * r + s.x * w, y: centre - (v.y * r + s.y * w) };
}

export const pt = (p: Vec) => `${p.x.toFixed(1)} ${p.y.toFixed(1)}`;

/** A movement's arrow: a quadratic curve through the junction. <dir> is the exit road's direction, for the head. */
export interface Arrow {
  entry: Vec;
  control: Vec;
  exit: Vec;
  dir: Vec;
}

/**
 * The arrow of a movement from <source> to <target>. <offset> is how far
 * from the road's middle it starts, in viewBox units; the small diagram
 * starts every arrow of a road at the same point, the planner spreads them
 * like lanes. Null where a road is missing.
 */
export function arrow(f: Frame, source: number, target: number, offset = laneOffset): Arrow | null {
  if (source < 0 || source >= f.dirs.length || target < 0 || target >= f.dirs.length) return null;
  const u = f.dirs[source];
  const v = f.dirs[target];
  return {
    entry: at(u, mouth, side(f, { x: -u.x, y: -u.y }), offset),
    control: { x: centre, y: centre },
    exit: at(v, mouth, side(f, v), laneOffset),
    dir: v,
  };
}

export function arrowPath(a: Arrow): string {
  return `M ${pt(a.entry)} Q ${pt(a.control)} ${pt(a.exit)}`;
}

/** The triangle at the end of an arrow, pointing out along its exit road. */
export function arrowHead(a: Arrow, scale = 1): string {
  const v = a.dir;
  const tip = { x: a.exit.x + v.x * 7 * scale, y: a.exit.y - v.y * 7 * scale };
  const wing = { x: v.y * 3.5 * scale, y: v.x * 3.5 * scale };
  return `M ${pt(tip)} L ${pt({ x: a.exit.x + wing.x, y: a.exit.y + wing.y })} L ${pt({ x: a.exit.x - wing.x, y: a.exit.y - wing.y })} Z`;
}

/** The point at <t> (0 to 1) along an arrow's curve. */
export function along(a: Arrow, t: number): Vec {
  const s = 1 - t;
  return {
    x: s * s * a.entry.x + 2 * s * t * a.control.x + t * t * a.exit.x,
    y: s * s * a.entry.y + 2 * s * t * a.control.y + t * t * a.exit.y,
  };
}

/** The two ends of the crosswalk across road <approach>. */
export function crosswalkEnds(f: Frame, approach: number, half = 9): [Vec, Vec] | null {
  if (approach < 0 || approach >= f.dirs.length) return null;
  const u = f.dirs[approach];
  const across = { x: u.y, y: -u.x };
  return [at(u, crosswalk, across, half), at(u, crosswalk, across, -half)];
}

/** Distance from <p> to the segment from <a> to <b>. */
export function toSegment(p: Vec, a: Vec, b: Vec): number {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const length = dx * dx + dy * dy;
  const t = length > 0 ? Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / length)) : 0;
  return Math.hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy));
}

export const isVehicle = (kind: MovementKind) => kind !== MovementKind.Pedestrian;
