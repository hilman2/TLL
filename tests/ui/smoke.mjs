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

const element = (tag) => (props) => React.createElement(tag, null, props.header, props.children);

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
    // The C# side says whether the panel is open; the one local value is the
    // tab, chosen by the scenario's "tab" entry.
    scenario = { ...data, "tll.panelOpen": open };
    for (const b of localBindings) b.value = data.tab ?? b.initial;
    for (const { target, component } of appended) {
      try {
        const html = renderToString(React.createElement(component));
        if (open && target === "Game") {
          preview.push(`<h3>${label}</h3><div class="stage">${html}</div>`);
          for (const text of data.expect ?? []) {
            if (!html.includes(text)) {
              failures++;
              console.error(`FAIL ${label}: the open panel does not show "${text}"`);
            }
          }
        }
      } catch (e) {
        failures++;
        console.error(`FAIL ${target}, ${label}, panel ${open ? "open" : "closed"}: ${e.message}`);
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
