// Loads the built panel (TLL.mjs) against stand-ins for the game's UI
// modules and renders every component TLL registers, in several states.
//
// The stand-ins offer exactly the exports the game has (cs2-exports.json,
// read from the game's own UI bundle). Touching anything else fails the run
// with the name of the missing export. The types shipped with the UI
// template list functions the game does not provide; this is where such a
// mismatch shows up, instead of in the game, where it takes down the whole
// interface.
//
//   node tests/ui/smoke.mjs <path to TLL.mjs>
// React and react-dom are resolved from the current directory's node_modules.
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const require = createRequire(join(process.cwd(), "package.json"));
const React = require("react");
const ReactDOM = require("react-dom");
const { renderToString } = require("react-dom/server");

const here = dirname(fileURLToPath(import.meta.url));
const exportsOf = JSON.parse(readFileSync(join(here, "cs2-exports.json"), "utf8"));

let scenario = {};
const localBindings = [];
const triggers = [];

function strictModule(name, impl) {
  const names = exportsOf[name];
  const target = {};
  for (const n of names) {
    target[n] = impl[n] ?? ((..._) => {
      throw new Error(`${name}.${n} exists in the game but the smoke test has no stand-in for it; add one.`);
    });
  }
  return new Proxy(target, {
    get(t, p) {
      if (p in t) return t[p];
      if (typeof p === "symbol" || p === "__esModule" || p === "default" || p === "then") return undefined;
      throw new Error(`The game's ${name} has no export "${String(p)}".`);
    },
  });
}

// The stand-ins keep the class, so the preview page shows buttons and
// panels styled as TLL styles them; the game adds its own look on top.
const element = (tag) => (props) => React.createElement(tag, { className: props.className, style: props.style }, props.header, props.children);

globalThis.window = globalThis;
window.React = React;
window.ReactDOM = ReactDOM;
window["cs2/api"] = strictModule("cs2/api", {
  bindValue: (group, name, fallback) => ({
    get value() {
      const key = `${group}.${name}`;
      return key in scenario ? scenario[key] : fallback;
    },
  }),
  bindLocalValue: (initial) => {
    const binding = { initial, value: initial, update(v) { this.value = v; } };
    localBindings.push(binding);
    return binding;
  },
  useValue: (binding) => binding.value,
  trigger: (...args) => triggers.push(args),
});
window["cs2/l10n"] = strictModule("cs2/l10n", {
  useLocalization: () => ({ translate: (id, fallback) => fallback }),
});
window["cs2/ui"] = strictModule("cs2/ui", {
  Button: element("button"),
  FloatingButton: element("button"),
  Panel: element("section"),
  Scrollable: element("div"),
  Tooltip: element("span"),
});
window["cs2/modding"] = strictModule("cs2/modding", {});

const bundle = process.argv[2];
const module = await import(pathToFileURL(bundle).href);
const appended = [];
module.default({ append: (target, component) => appended.push({ target, component }) });
if (appended.length === 0) throw new Error("TLL registered no components.");

const phase = (movements, permitted = []) => ({
  minGreen: 5, maxGreen: 45, green: 20, demand: 3, pressure: 2, wait: 12, busy: true, preempt: false, movements, permitted,
});
const managed = {
  index: 7, version: 1, name: "Main Street / Oak Avenue", managed: true, hasSignals: true,
  mode: 2, strategy: 0, group: 3, manual: false, stage: 0, phase: 1, next: 1, stageSeconds: 8.5, cycleSeconds: 64,
  intergreenSeconds: 4.3, cyclePosition: -1,
  leftHandTraffic: false, cameraYaw: 35,
  approaches: [
    { x: 1, z: 0, name: "Main Street" },
    { x: 0, z: 1, name: "Oak Avenue" },
    { x: -1, z: 0, name: "Main Street" },
  ],
  movements: [
    { kind: 0, source: 0, target: 2 },
    { kind: 1, source: 2, target: 1 },
    { kind: 4, source: 1, target: -1 },
    { kind: 5, source: 0, target: 2 },
    { kind: 0, source: 7, target: 9 },
  ],
  walk: true,
  scramble: false,
  turnOnRed: true,
  conflicts: 3,
  reviewMinutes: 44.6,
  phases: [phase([0, 1, 3], [1]), { ...phase([2, 4]), walkGreen: 19 }],
};
const summary = {
  available: true, conflict: "", automation: true, showProblems: false, showCongestion: true,
  managed: 12, greenWaves: 2, coordinated: 7, byMode: [1, 2, 7, 2, 0],
  problems: [
    { index: 7, version: 1, name: "Main Street / Oak Avenue", queue: 11.4 },
    { index: 8, version: 1, name: "Harbour Road / A very long street name that does not fit", queue: 5.2 },
  ],
};
// The planner open on a crossroads: two phases, the second selected, one
// left turn sharing a lane with straight traffic.
const crossroads = [
  { x: 1, z: 0, name: "Main Street" },
  { x: 0, z: 1, name: "Oak Avenue" },
  { x: -1, z: 0, name: "Main Street" },
  { x: 0, z: -1, name: "Oak Avenue" },
];
const mv = (kind, source, target, volume = 200, partners = []) => ({ kind, source, target, volume, peak: volume * 1.3, partners });
const plannerPhase = (movements, permitted = [], extra = {}) => ({
  movements, permitted, minGreen: 5, maxGreen: 45, green: 20, walk: 0, canHold: true, demand: 4, wait: 11, ...extra,
});
const plannerInfo = {
  open: true, tourSeen: true, index: 7, version: 1, name: "Main Street / Oak Avenue",
  leftHandTraffic: false, cameraYaw: 20, main: 0, mainRoad: [0, 2], approaches: crossroads,
  movements: [
    mv(0, 0, 2, 640, [1]), mv(1, 0, 3, 180, [0]), mv(2, 0, 1, 90),
    mv(0, 2, 0, 610), mv(1, 2, 1, 120), mv(2, 2, 3, 70),
    mv(0, 1, 3, 240), mv(1, 1, 0, 60), mv(0, 3, 1, 220), mv(1, 3, 2, 50),
    mv(4, 0, -1, 30), mv(5, 0, 2, 40),
  ],
  stage: 0, stageSeconds: 7.5, running: 0, next: 1, hold: -1,
  addable: [2, 5],
  phases: [plannerPhase([0, 1, 3, 4, 11], [1, 4]), plannerPhase([6, 7, 8, 9, 10], [7, 9], { walk: 14 })],
  selected: 1, owner: 1, mode: 2, turnOnRed: false, scramble: false, crosswalkPhases: false, hasCrosswalks: true,
  coordinated: false, yellow: 3, allRed: 1, prepare: 0.3, maxWait: 90, cycle: 48.6, cycleLongest: 98.6,
  live: false, dirty: true, canApply: false, canUndo: true, canRedo: false,
  findings: [
    { kind: 0, severity: 0, phase: -1, movement: 2, other: -1, fitsIn: [0, 1], value: 0 },
    { kind: 4, severity: 1, phase: 0, movement: 4, other: -1, fitsIn: [], value: 812 },
    { kind: 6, severity: 2, phase: 1, movement: -1, other: -1, fitsIn: [], value: 0 },
  ],
  refusal: { movement: 6, blocking: [0, 3], fitsIn: [1] },
  message: { key: "PresetLoadedTurnable", argument: "Crossroads, 3 phases" },
  templateScramble: false,
  templates: [
    { kind: 0, current: false, delay: 18.4, saturation: 0.8, phases: [{ movements: [0, 1, 3, 4], permitted: [1, 4] }, { movements: [6, 7, 8, 9], permitted: [7, 9] }] },
    { kind: 1, current: true, delay: 14.2, saturation: 0.7, phases: [{ movements: [1, 4], permitted: [] }, { movements: [0, 3], permitted: [] }, { movements: [6, 7, 8, 9], permitted: [7, 9] }] },
    { kind: 4, current: false, delay: 31.5, saturation: 1.2, phases: [{ movements: [0, 1, 2], permitted: [] }] },
  ],
  lanes: [
    {
      approach: 0, targets: [1, 2, 3], uses: [{ first: 0, last: 1 }, { first: 1, last: 2 }], changed: true, ruled: false,
      options: [[0, 0, 2], [3, 1, 4]], suggestion: [{ first: 0, last: 1 }, { first: 2, last: 2 }], gain: 0.31,
    },
    { approach: 2, targets: [3, 0], uses: [{ first: 0, last: 0 }, { first: 1, last: 1 }], changed: false, ruled: true, options: [[1, 0], [3, 1]], suggestion: null, gain: 0 },
  ],
  solid: [
    { approach: 0, pieces: 2, lengths: [96.4, 181.9, 260] },
    { approach: 2, pieces: 0, lengths: [120] },
    { approach: 1, pieces: 0, lengths: [] },
  ],
  presetTurns: 2, roads: 4,
  presets: [
    { id: "a", name: "Crossroads, 3 phases", phases: 3, arms: 4, fits: true },
    { id: "b", name: "T with a turn phase", phases: 3, arms: 3, fits: false },
  ],
};

const scenarios = {
  "empty city": {},
  "junction tab, picking": { "tll.toolActive": true },
  "city tab": { tab: "city", "tll.summary": { ...summary, showProblems: true } },
  "problems tab": { tab: "problems", "tll.summary": summary },
  "problems tab, none": { tab: "problems" },
  "managed junction": { "tll.selected": managed },
  "manual junction": { "tll.selected": { ...managed, manual: true } },
  "junction in a green wave": { "tll.selected": { ...managed, mode: 3, group: 2, cyclePosition: 41.5 } },
  "nothing selected": {},
  "junction in transition": { "tll.selected": { ...managed, stage: 1, phase: 0, next: 1 } },
  "flashing junction": { "tll.selected": { ...managed, mode: 4, stage: 4 } },
  "draining junction": { "tll.selected": { ...managed, mode: 5 }, expect: [">Drain<"] },
  "autopilot measuring": {
    "tll.selected": { ...managed, autopilot: { majorVolume: 0, minorVolume: 0, signalAdvice: 0, pending: -1, estimates: [] } },
  },
  "autopilot with estimates": {
    "tll.selected": {
      ...managed,
      movements: managed.movements.map((m, i) => ({ ...m, volume: i === 0 ? -1 : 120 * i })),
      autopilot: {
        majorVolume: 840, minorVolume: 210, signalAdvice: 2, pending: 1, pendingScramble: true,
        measuredWait: 12.4, modelledWait: 9.1, waveBanMinutes: 700,
        estimates: [
          { strategy: 0, delay: 18.4, saturation: 0.92, measured: true, jammed: false, scrambleDelay: 25.1, scrambleSaturation: 0.8, scrambleMeasured: false, scrambleJammed: false },
          { strategy: 1, delay: 14.2, saturation: 0.71, measured: false, jammed: false, scrambleDelay: 19.9, scrambleSaturation: 0.7, scrambleMeasured: true, scrambleJammed: false },
          { strategy: 2, delay: 31.0, saturation: 0.95, measured: true, jammed: true, scrambleDelay: -1, scrambleSaturation: -1, scrambleMeasured: false, scrambleJammed: false },
        ],
      },
    },
    // Texts the open panel must show; the stand-in translation gives the fallbacks.
    expect: [
      ">Next review in 45 min<", "Pedestrian scramble · turns waited 3/8<",
      "Measured wait Ø 12 s · model Ø 9 s", "Kept out of green waves for 12 h", " •<",
      ">ProtectedTurns + scramble •<", ">Next review changes to: ProtectedTurns + scramble<",
    ],
  },
  "junction with a scramble": {
    "tll.selected": { ...managed, scramble: true },
    expect: [">Pedestrian scramble<"],
  },
  "estimates from an older C# side": {
    "tll.selected": {
      ...managed,
      autopilot: {
        majorVolume: 840, minorVolume: 210, signalAdvice: 0, pending: -1,
        estimates: [{ strategy: 0, delay: 18.4, saturation: 0.92, measured: true, jammed: false }],
      },
    },
  },
  "managed junction with turns": {
    "tll.selected": {
      ...managed,
      turns: [
        { source: 0, target: 0, kind: 3, state: 1, volume: 0 },
        { source: 0, target: 1, kind: 1, state: 0, volume: 312 },
        { source: 1, target: 2, kind: 2, state: 2, volume: 12 },
        { source: 2, target: 0, kind: 0, state: 3, volume: -1 },
        { source: 2, target: 2, kind: 3, state: 4, volume: -1 },
      ],
    },
    expect: [">Turns<", ">forbidden · autopilot<", ">Main Street → Oak Avenue<", ">Left · 312/h<", ">allowed · you<", ">Connect lanes<"],
  },
  "lane tool open": {
    "tll.laneToolActive": true,
    "tll.selected": { ...managed, turns: [{ source: 0, target: 1, kind: 1, state: 0, volume: 40 }] },
    expect: [">Click a lane leading in, then one leading out…<", ">taken away; a click connects it again<", ">turn forbidden, see the list below<"],
  },
  "junction without signals, with turns": {
    "tll.selected": {
      index: 9, version: 1, name: "Elm Road", managed: false, hasSignals: false,
      approaches: managed.approaches, leftHandTraffic: false, cameraYaw: 0,
      turns: [{ source: 0, target: 2, kind: 0, state: 2, volume: -1 }],
      signs: [
        { approach: 0, sign: 1, showing: 1, auto: false },
        { approach: 1, sign: 0, showing: 2, auto: true },
        { approach: 2, sign: 0, showing: 0, auto: false },
      ],
    },
    expect: [">forbidden · you<", ">Right of way<", ">Now: Priority road<", ">Now: Give way · autopilot<", ">Now: right before left<", ">Stop<", ">Automatic<"],
  },
  "plan adapted after a road change": {
    "tll.selected": { ...managed, manual: true, notice: { kind: 1, added: 2, moved: 1, dropped: 0 } },
    expect: [">Your plan was adapted to the changed roads. Ways through the junction: 2 added, 1 moved to another phase.<", ">Got it<"],
  },
  "plan replaced after a road change": {
    "tll.selected": { ...managed, manual: true, notice: { kind: 2, added: 0, moved: 0, dropped: 0 } },
    expect: [">Your plan no longer fitted the changed roads and was replaced by a generated one.<"],
  },
  "problems tab with changed plans": {
    tab: "problems",
    "tll.summary": { ...summary, notices: [{ index: 11, version: 1, name: "Harbour Road / Pier Street", kind: 1 }, { index: 12, version: 1, name: "Elm Road", kind: 2 }] },
    expect: [">Plans TLL changed after a road changed<", ">adapted<", ">replaced<"],
  },
  "planner, phases": {
    "tll.planner": plannerInfo,
    "tll.selected": managed,
    expect: [
      ">Planner<", ">Main Street / Oak Avenue<", ">You: layout<", ">Phase 2<", ">Fill up<", ">Hold<",
      ">Oak Avenue → Oak Avenue, straight on: its path crosses Main Street → Main Street, straight on; Main Street → Main Street, straight on.<",
      ">No green yet<", ">Main Street → Oak Avenue, right<", ">Give it green<", ">Protect it<",
      ">Loaded. It fits more than one way; turn it if the roads are wrong: Crossroads, 3 phases<", ">Discard<", ">Apply<",
    ],
  },
  "planner, times": {
    "tll.planner": { ...plannerInfo, refusal: null, message: null, findings: [] },
    step: "times",
    expect: [">How the signals decide<", ">Adjust times automatically<", ">Cycle: up to 99 s<", ">Min<", ">Max<", ">crosswalk needs 14 s<", ">Every way through the junction gets green, and no paths cross.<"],
  },
  "planner, fixed time": {
    "tll.planner": { ...plannerInfo, mode: 0, owner: 2, findings: [], refusal: null },
    step: "times",
    expect: [">Cycle: 49 s<", ">Green<"],
  },
  "planner, lanes": {
    "tll.planner": { ...plannerInfo, refusal: null },
    "tll.selected": { ...managed, approaches: crossroads, turns: [{ source: 0, target: 3, kind: 1, state: 2, volume: 180 }] },
    step: "lanes",
    expect: [
      ">Connect single lanes on the map<", ">forbidden · you<", ">Lane arrows<", ">From Main Street<", ">changed<",
      ">The game&#x27;s lanes<", ">busiest lane −31 %<", ">Click a lane to change where it leads.<",
      ">Solid lines before the junction<", ">182 m<", ">off<",
    ],
  },
  "planner, templates": {
    "tll.planner": plannerInfo,
    sheet: "templates",
    expect: [">Protected turns first<", ">Each road alone<", ">Ø 14 s<", ">Main road: Main Street<", ">With a phase for pedestrians alone<"],
  },
  "planner, presets": {
    "tll.planner": plannerInfo,
    sheet: "presets",
    expect: [">Crossroads, 3 phases<", ">needs 3 roads<", ">Turn it<", ">Use<", ">Share<", ">Keep the times with it<", ">Keep the lanes with it<"],
  },
  "planner, live with an error": {
    "tll.planner": { ...plannerInfo, live: true },
    expect: [">Not live until fixed<"],
  },
  "planner, first opening": {
    "tll.planner": { ...plannerInfo, tourSeen: false },
    expect: [">1/3  Three steps<", ">Next<"],
  },
  "planner, autopilot": {
    "tll.planner": { ...plannerInfo, owner: 0, coordinated: true, refusal: null },
    expect: [">The autopilot plans this junction. Click an arrow or pick a template to take it over.<"],
  },
  "vanilla junction": { "tll.selected": { index: 9, version: 1, name: "Elm Road", managed: false, hasSignals: true } },
  "junction without signals": { "tll.selected": { index: 9, version: 1, name: "Elm Road", managed: false, hasSignals: false } },
  "roundabout": { "tll.selected": { index: 9, version: 1, name: "Elm Road", managed: false, hasSignals: false, roundabout: true } },
  "other traffic mod": { "tll.summary": { ...summary, available: false, conflict: "Traffic Lights Enhancement", problems: [] } },
  "unavailable": { "tll.summary": { ...summary, available: false, problems: [] } },
};

let failures = 0;
const preview = [];
for (const [label, data] of Object.entries(scenarios)) {
  for (const open of [false, true]) {
    // The C# side says whether the panel is open.
    scenario = { ...data, "tll.panelOpen": open };
    // Local values are told apart by their initial value: the panel's
    // tab starts at "junction", the planner's step at "phases", its sheet
    // at "none".
    for (const b of localBindings) {
      if (b.initial === "junction") b.value = data.tab ?? b.initial;
      else if (b.initial === "phases") b.value = data.step ?? b.initial;
      else if (b.initial === "none") b.value = data.sheet ?? b.initial;
      else b.value = b.initial;
    }
    // Everything drawn on the game's screen, the panel and the planner
    // alike; the expected texts may come from either.
    let screen = "";
    for (const { target, component } of appended) {
      try {
        const html = renderToString(React.createElement(component));
        if (open && target === "Game") screen += html;
      } catch (e) {
        failures++;
        console.error(`FAIL ${target}, ${label}, panel ${open ? "open" : "closed"}: ${e.message}`);
      }
    }
    if (open) {
      preview.push(`<h3>${label}</h3><div class="stage">${screen}</div>`);
      for (const text of data.expect ?? []) {
        if (!screen.includes(text)) {
          failures++;
          console.error(`FAIL ${label}: the open panel does not show "${text}"`);
        }
      }
    }
  }
}
if (failures > 0) {
  console.error(`${failures} render(s) failed.`);
  process.exit(1);
}

// With a second argument, write the open panel of every scenario to an HTML
// page for a look in a normal browser. Layout there differs from the game's
// engine, but drawings and texts can be checked.
if (process.argv[3]) {
  const { writeFileSync } = await import("node:fs");
  const css = readFileSync(join(dirname(bundle), "TLL.css"), "utf8");
  const page = `<!doctype html><meta charset="utf-8"><style>${css}</style>
<style>html{font-size:1px}body{font-size:16rem;background:#1b1f26;color:#e8e8e8;font-family:sans-serif}
.stage{position:relative;height:900px;margin-bottom:20px}section{position:absolute}</style>
${preview.join("\n")}`;
  writeFileSync(process.argv[3], page);
}
console.log(`Smoke test passed: ${appended.length} component(s), ${Object.keys(scenarios).length} scenarios.`);
