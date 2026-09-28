# Metrics log

The metrics log records what the traffic lights of a city do: traffic and
waiting per junction, every green and why it ended, and every decision of the
autopilot. Loaded into SQLite, it answers questions such as which greens are
too short, whether a green wave helps, or whether one build of the mod does
better than another.

## Recording

Switch on **Record metrics** in the mod's options, in the debug section. From
then on, every city you load starts a session: a folder named after the time
it started, under

```
%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\TLL\Metrics
```

A session takes a few MB per hour of play.

## Seeing the standard report

With Python 3.11 or later, no packages needed:

```
cd tools/metrics
python -m tll_metrics report
```

This loads what is new into `metrics.sqlite` in the metrics folder and answers
the standard questions for the latest session. It can run while the game is
still recording. `--session all` covers every session, `--session NAME` one of
them; `import` only loads.

## Asking your own questions

```
python -m tll_metrics sql "SELECT node, SUM(failures) FROM phase_rounds GROUP BY node ORDER BY 2 DESC LIMIT 5"
```

or open `metrics.sqlite` in any SQLite tool. The tables are described below.

## Tables

Every record has:

| field | meaning |
|---|---|
| `session` | the session's folder name |
| `frame` | simulation frame; 60 make one second of the vehicles' time |

Records about a junction also have:

| field | meaning |
|---|---|
| `node` | the junction's entity index, stable within a session |
| `round` | the autopilot round: 4096 frames, 22.5 minutes of game clock |
| `window` | time-of-day window, 0 to 7, of 3 game hours each |
| `minute` | minute of the game day, 0 to 1439 |
| `origin` | `Auto` or `Manual` |
| `mode`, `layout`, `wave_group` | control mode, phase layout, green wave number (0 for none) |

### sessions

One record per session.

| field | meaning |
|---|---|
| `started` | real time the session started, UTC |
| `commit`, `version` | the build: git commit (with `-dirty` for uncommitted changes) and mod version |
| `auto_manage`, `auto_mode`, `auto_layout`, `auto_green_waves`, `auto_flash`, `turn_on_red`, `keep_clear` | the settings at the start |

### rounds

One record per junction and autopilot round.

| field | meaning |
|---|---|
| `elapsed_s` | seconds of vehicle time the round covers |
| `vehicles`, `vehicles_per_h` | vehicles that entered the junction |
| `flowing` | of those, vehicles that entered at speed, without stopping at the line |
| `wait_s` | vehicle-seconds spent standing in the queues |
| `free_wait_s` | the part of `wait_s` while the movement's exit was free |
| `wait_per_vehicle_s` | `wait_s` over `vehicles` |
| `blocked_share_max`, `blocked_share_mean` | share of the round in which a movement's exit was backed up to the junction: the worst movement, and the mean over movements with traffic |
| `pedestrians_per_h` | people crossing |
| `worst_free_queue` | mean vehicles waiting per lane with the exit free, on the worst approach |
| `backlog` | `worst_free_queue` reached 6 |
| `turn_on_red`, `scramble_on_demand`, `diverting` | options set, and whether pedestrians were diverted into the scramble |

### phase_rounds

One record per phase and autopilot round.

| field | meaning |
|---|---|
| `phase`, `flags` | the phase's index and flags (`Pedestrian`, `Scramble`, `Coordinated`) |
| `min_green_s`, `max_green_s`, `planned_green_s`, `walk_green_s` | its configured greens |
| `greens`, `green_s` | greens started, and seconds of green |
| `served` | vehicles that entered on the phase's movements during its green |
| `failures` | greens that ended with vehicles standing while their exits were free: too short for the queue |
| `residual_queue` | vehicles left standing, summed over the greens that ended |
| `wait_at_start_s` | seconds the phase had waited, summed over its greens |
| `end_empty` | greens ended because the queue had left, or nobody asked any more |
| `end_maximum` | ended at the maximum green |
| `end_starved` | ended for another phase that had waited too long |
| `end_outweighed` | ended with only stragglers left, for a far longer queue elsewhere |
| `end_blocked` | ended because the queue could not leave: its exits were backed up |
| `end_emergency` | ended for an emergency vehicle |
| `end_schedule` | ended by the fixed schedule or order of the timed and actuated modes |

### reviews

One record per layout review of an automatic junction.

| field | meaning |
|---|---|
| `wave` | the junction ran in a green wave |
| `choice` | the layout the review recommends |
| `jammed` | the running layout counts as jammed in the layout memory |
| `change` | the layout changes now |
| `pending`, `age` | reviews in a row recommending the change, and reviews since the last change |
| `recorded` | the measurement period was recorded in the layout memory |
| `period_rounds`, `period_vehicles`, `period_backlog` | the measurement period |
| `measured_wait_s`, `modelled_wait_s` | the vehicles' mean wait as measured over the period and as the delay model expected it |
| `est_delay_L`, `est_load_L` | per layout `L` (`permissive`, `protected`, `split`, `scramble`): the model's mean delay and worst load |
| `cor_delay_L`, `cor_load_L` | the same, corrected by the layout memory |
| `factor_L`, `samples_L`, `backlog_L` | the layout memory: measured over modelled, periods measured, share with backlog |

### decisions

One record per change at a junction. `kind` says which, and the fields vary with it.

| kind | fields |
|---|---|
| `layout` | `from`, `to`, `jammed`, `tried` (measured before), `expected_delay_s`, `current_delay_s`, `window` |
| `flash` | `to` (flashing or not), `reason` (`traffic`, `backlog`, `setting`), `major_per_h`, `minor_per_h`, `minor_total_per_h`, `side_load`, `worst_free_queue` |
| `turn_on_red` | `to`, `layout` |
| `wave` | `action`: `start`, `replan`, `keep` or `reject` a corridor, `end` one that did not help (all at the corridor's first junction, with `group`, `members`, `junctions`, `cycle_s`, `band_a_s`, `band_b_s`); `leave` for a junction taken out of a wave. For `keep`, the figures are those of the plan it was compared with. |
| `rebuild` | `trigger` (`game` or `mod`), `carries_on` (same plan, the controller went on), `layout`, `mode`, `approaches`, `movements`, `phases` |
| `user` | `action` in the panel (`mode`, `layout`, `scramble_on_demand`, `turn_on_red`, `make_automatic`, `manage`, `release`, `reset_all`) and its `value` |

## Tests

```
docker compose -f tools/metrics/docker/compose.yml run --rm tests
```
