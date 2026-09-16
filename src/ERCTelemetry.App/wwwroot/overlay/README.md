# ERCTelemetry — Overlay pages

Local browser-source overlays for OBS / Streamlabs. The app hosts them at
`http://127.0.0.1:8090` (loopback only — nothing is exposed to your network).

## Pages

| URL | Content | Update rate |
|---|---|---|
| `/overlay/broadcast.html` | **Main overlay** (1920×1080): session strip (session · track · laps/time left · weather/temps · rain forecast · pit window), timing tower with sector mark chips (green/purple) + FL badge, player strip (speed/gear/RPM, pedals %, timing, fuel, tyre, DRS, LAP INVALID chip), minimap, race-control feed | mixed (≤5 Hz state / ~30 Hz player / ~10 Hz map) |
| `/overlay/standings.html` | Live leaderboard: position, number, name, lap, last/best lap, gap, tyre | ≤5 Hz |
| `/overlay/player-card.html` | Speed, gear, RPM bar, throttle/brake/ERS bars, fuel, tyre + rival row | ~30 Hz |
| `/overlay/telemetry.html` | Full panel: session/weather/forecast, player (pos, timing, pedals %, ERS mode, fuel, tyre wear/damage) + rival | ≤5 Hz + ~30 Hz |
| `/overlay/commentary.html` | Broadcast assistant: session header, battles (<1 s), movers vs grid, fastest laps, pits & trouble, oldest tyres, active marshal flags, weather + forecast | ≤5 Hz |
| `/overlay/commentator.html` | **AI-Kommentator** (Layer 1): scrolling feed of German commentary lines — Druck von hinten (<0,5 s), Boxenfenster (Reifen >80 %), Regen-Vorhersage, Überholmanöver, Safety Car u. a. | immediate |
| `/overlay/h2h.html` | Head-to-head duel of two pickable drivers: position/name/gap, last/best lap (green = faster), sectors S1–S3 with delta vs. eigene Bestzeit, tyre chip + wear, pit status, tally (laps/positions won) | ≤5 Hz |
| `/overlay/race-control.html` | Fastest laps, penalties, retirements, session start/end | immediate |
| `/overlay/tyres.html` | Tyre heatmap: wear + damage per car (worst wheel), ordered by standings | ≤5 Hz |
| `/overlay/timing-tower.html` | Classic TV timing tower: position, driver, interval to leader | ≤5 Hz |
| `/overlay/relative.html` | Relative window: the cars ±5 positions around the PLAYER with gaps relative to the player, tyre chip, pit badge, lap advantage/deficit | ≤5 Hz |
| `/overlay/map.html` | Minimap: self-learning track outline (built from car positions), live car positions with race numbers, player highlighted | ~10 Hz |

## Add to OBS

1. Sources → + → **Browser**.
2. URL = one of the overlay pages above.
3. Width/Height: standings ≈ 700×400, player card ≈ 400×230, race control ≈ 420×500.
4. Keep "Shutdown source when not visible" and "Refresh browser when scene becomes active"
   **off** — the pages manage themselves and reconnect after app restarts.

Data appears as soon as F1 26 streams telemetry and the app shows a session.

## Personalization (no build step)

Everything renders through plain HTML/CSS/JS. Two contract surfaces:

### CSS custom properties

`overlay.css` gates all styling behind custom properties on `:root`:

- `--overlay-widget-bg` (rgba — lower alpha = more transparency in OBS)
- `--overlay-widget-border`, `--overlay-widget-radius`
- `--overlay-text`, `--overlay-muted`, `--overlay-accent`, `--overlay-track` (minimap
  track outline), `--overlay-mono`
- `--overlay-throttle`, `--overlay-brake`, `--overlay-ers`
- `--overlay-tyre-soft|medium|hard|inter|wet`

Override them in an edited copy of `overlay.css`, or append your own `<style>` after the
`<link>` in a copy of a page. **Body background stays `transparent`** so the game shows
through; only `.widget` paints.

### DOM ids

Pages mark every live element with a stable id — restyle or relocate freely:

- standings: `#standings-widget`, `#session-title`, `#standings-body`
  (rows `tr.is-player`, `tr.is-out`; cells `td.pos/.num/.name/.lap/.mono/.gap/.sec/.pen` —
  S1/S2/S3 sector chips, `td.mono.gap` also carries the +interval column);
  `#forecast` — weather lookahead chip list (`.sample`, `.sample.rainy` when rain > 50%)
- telemetry panel: `#session-widget`, `#session-title`, `#cond-row`, `#forecast`, `#drs`,
  `#player-panel` / `#rival-panel` (`#p-*` / `#r-*` live ids for pos, speed, gear, rpm,
  pedals `#p-thr-pct` etc., ERS mode, fuel, tyre chip + age, wear/damage per side)
- player card: `#player-name`, `#drs`, `#pos` (P-label), `#speed`, `#gear`, `#rpm-bar`,
  `#throttle-bar`, `#brake-bar`, `#ers-bar`, `#thr-pct`, `#brk-pct`, `#ers-pct` — pedal
  column percent readouts, `#last-lap`, `#best-lap`, `#lap-num`, `#gap`, `#tyre`,
  `#tyre-age`, `#fuel-kg`, `#fuel-laps`, `#ers-mode`, `#rival-name`, `#rival-speed`,
  `#rival-gear`, `#rival-best`
- race control: `#session-title`, `#event-list` (`li.event-<type>`)
- tyres: `#tyres-widget`, `#session-title`, `#car-count`, `#tyres-body`
  (rows `tr.is-player`; heat bars `.heat.heat-wear` / `.heat.heat-damage`)
- timing tower: `#tower-widget`, `#session-title`, `#driver-count`, `#tower-body`
  (rows `tr.is-player`, `tr.is-out`, `tr.leader`, `tr.battle` (gap-to-front < 1 s);
  cells `td.pos/.name/.gap`; FL badge `.fl` on the session-best driver)
- commentary: `#header` (`#h-session`, `#h-lap`, `#h-clock`, `#h-cond`, `#h-meta` —
  game mode + total laps), `#battles-widget` (`#battles`),
  `#movers-widget` (`#movers` — `.chip-up` gained / `.chip-down` lost vs grid),
  `#fl-widget` (`#fastest`), `#pits-widget` (`#pits`), `#tyres-widget` (`#tyres`),
  `#flags-widget` (`#flags` — one `.flag-chip.flag-green|blue|yellow` per active zone),
  `#weather-widget` (`#w-now`, `#w-forecast` — `.sample.rainy` when rain ≥ 50 %)
- h2h: `#h2h-header` (`#h2h-pick-a`, `#h2h-pick-b`, `#h2h-note`, sides `#a-pos/#b-pos`,
  `#a-name/#b-name`, `#a-gap/#b-gap`), `#h2h-laps` (`#a-col/#b-col` headers,
  `#a-last/#b-last`, `#a-best/#b-best` — `.win` marks the faster side),
  `#h2h-sectors` (per side + sector: `#a-s1-mark`, `#a-s1-time`, `#a-s1-delta` —
  `.mark.green/.purple` status chips, `.delta.gain/.loss`),
  `#h2h-tyres` (`#a-tyre/#b-tyre` chips, `#a-tyre-age/#b-tyre-age`, `#a-wear/#b-wear`),
  `#h2h-pits` (`#a-pit/#b-pit`, `#a-stops/#b-stops`), `#h2h-tally` (`#t-laps`, `#t-pos`)
- broadcast: `#strip` (`#s-title`, `#s-clock`, `#s-weather`, `#s-forecast` —
  `.rain-wet` at ≥50 % rain, `#s-pit`, `#s-strategy` tyre/pit advice,
  `#s-fuel` fuel-to-finish chip `.fuel-short`/`.fuel-ok`), tower `#tower-widget`, `#tower-body`
  (rows `tr.is-player/.is-out/.leader/.battle`; sector mark chips `.marks i.green/.purple`;
  cells `td.pos/.name/.laptime/.gap/.st`; FL badge `.fl`), player strip
  `#player-widget` (`#p-*` live ids: `#p-pos/#p-speed/#p-gear/#p-rpm-bar`, pedals
  `#p-thr-bar/#p-thr-pct` etc., `#p-invalid` LAP-INVALID chip, `#p-drs` DRS chip,
  tyre chip `#p-tyre` + `#p-tyre-age`, fuel `#p-fuel/#p-fuel-laps`), map
  `#map-canvas` (340×200, player dot `--overlay-accent`), feed `#event-list`
  (newest on top, max 8)
- map: `#map-widget`, `#map-title`, `#map-canvas` (`<canvas>` 340×200). The map is
  **self-learning**: it accumulates the cars' world positions into a metre-grid trail and
  draws the track outline from it (the game sends no track layout), so the layout appears
  after a lap or two and works for any track. Cars are dots with their race number; the
  player dot uses `--overlay-accent` with a ring + name label, others `--overlay-muted`.
  The title shows the player's position/lap once standings arrive
- commentator: `#commentator-widget`, `#feed` (`.line` per commentary line, newest on top,
  `.line .when` timestamp, max 8 lines)

`body` gets class `overlay-connected` while the socket is live and `drs-on` when the
selected car can use DRS.

## Wire protocol (v1)

Shared helper `overlay.js` exposes `connectOverlay({ onHello, onState, onPlayer, onEvent, onMap, onConfig, onLayout })`
(JSON, camelCase) and `overlayFormat.lapTime(ms) / gap(ms) / pct(v)`. Every `/ws` connection also
gets a `scheme` on `hello` and `state` (`"de"` = schwarz-rot-gold, absent = classic): `overlay.js`
toggles `body.scheme-de`, and pages restyle purely via the CSS variables above. Add page-level
colors to `body.scheme-de` in `overlay.css` or your page copy.

Every connection also receives a `{"t":"config","blocks":{...}}` push (block visibility from the
app's settings, re-sent on reconnect). Keys are the stable block keys (`com.session`,
`com.battles`, `com.movers`, `com.fastest`, `com.pits`, `com.tyres`, `com.flags`, `com.weather`,
`h2h.header`, `h2h.laps`, `h2h.sectors`, `h2h.tyres`, `h2h.pits`, `h2h.tally`), values are
`true`/`false`; **missing keys default to visible**. `overlay.js` mirrors the dict on
`window.overlayBlocks` so pages can re-apply visibility idempotently at the end of every state
render (a config push and a state message have no guaranteed ordering).

Server → client:

```jsonc
{"t":"hello","protocol":1}
{"t":"config","blocks":{"com.battles":false,"h2h.tally":true}}
{"t":"state","session":{"sessionUid":0,"sessionType":"Race","track":"Spa","totalLaps":44,
  "weather":"LightRain","gameMode":"OnlineCustom","isNetworkGame":true,"playerCarIndex":3,
  "airTemperature":19,"trackTemperature":27,"sessionTimeLeft":0,
  "forecast":[{"timeOffsetMinutes":15,"weather":"LightRain","rainPercent":65,
               "trackTemperature":26,"airTemperature":21}],
  "marshalZones":[{"zoneStart":0.0,"flag":3}]},
 "standings":[{"position":1,"carIndex":3,"name":"Example","team":"McLaren","raceNumber":81,
   "currentLapNum":12,"lastLapTimeMs":91089,"bestLapTimeMs":90123,"gapToLeaderMs":0,
   "gapToCarInFrontMs":0,"pitStatus":"None","penalties":0,"resultStatus":"Active",
   "tyreCompound":"F1C3","tyreAgeLaps":12,"isPlayer":true,
   "sector1TimeMs":28456,"sector2TimeMs":31022,"sector3TimeMs":28111,
   "gridPosition":4,"pitStops":1,"currentLapTimeMs":45210,
   "s1Status":"Green","s2Status":"Purple","s3Status":"None",
   "bestSector1TimeMs":28400,"bestSector2TimeMs":31000,"bestSector3TimeMs":28100}],
 "results":[ ... end-of-session rows, same numeric fields ... ],
 "tyres":[{"carIndex":3,"wearPercent":22.4,"damagePercent":0.0}],
 "h2h":[{"carIndex":3,"lapsWon":4,"positionWins":2},
       {"carIndex":9,"lapsWon":5,"positionWins":3}]}
{"t":"player","player":{"carIndex":3,"name":"Example","speed":271,"gear":6,"throttle":0.95,
  "brake":0.0,"engineRpm":11050,"ersStoreEnergy":3200000.0,"ersPercent":80.0,
  "ersDeployMode":"Hotlap","fuelInTank":4.2,"fuelRemainingLaps":3.1,
  "tyreCompound":"F1C3","tyreAgeLaps":12,"drsAllowed":true,"drsOn":false},
 "rival":{ "same shape" }}
{"t":"event","type":"fastest-lap","text":"...","carIndex":9,
 "utc":"2026-03-29T14:30:15+00:00","seq":7}
{"t":"map","positions":[{"carIndex":3,"isPlayer":true,"x":12.5,"z":-30.2}]}
{"t":"tracklayout","track":"Spa","sessionUid":0,
 "points":[[1056231.5,-23.4], …],"startX":1056100.0,"startZ":-12.0}
```

Times/laps arrive **raw in milliseconds** — format them in the page (the `overlayFormat`
helpers do). Enums are F1Game.UDP member names (`Team.RedBullRacing` → `"RedBullRacing"`,
`ActualCompound.F1C3` → `"F1C3"`, `ResultStatus` → `"Active"|"Finished"|"Retired"|…`).
`map` positions are track world coordinates (single-precision meters). The minimap page
builds a **self-learning track outline** from them: every reported (x, z) is quantized onto
a 3 m grid and remembered per session (reset on `sessionUid` change in `state`); bounds come
from that trail, so the map stops rescaling once the track is traced and the cars sit on the
drawn line. The game sends no track layout packet, so the outline appears after a lap or two.

For most circuits this outline is **replaced by the real one** as soon as the app's Core-side
fit locks (~1 lap of driving): the server then pushes `{"t":"tracklayout", …}` with the
circuit centerline **already transformed into world metres** (plus `startX`/`startZ` when the
lap line was located). The page draws it as a closed polyline; `map` dots and outline share
one coordinate frame, so cars read on the racing line. Sent once per connect + when the fit
locks; unknown circuits keep the self-learning trail. The map title shows the track name
(`state.session.track`) plus position/lap; cars with `resultStatus` `Retired`/`DidNotFinish`
leave the map, and overlapping race-number labels are suppressed. The canvas size follows
the page's CSS size or `?w=<px>&h=<px>` URL params (e.g. `map.html?w=480&h=300`).

Client → server:

```jsonc
{"t":"select","carIndex":9}   // this page shows car 9 in the "player" slot
{"t":"select"}                // reset to the broadcast player
{"t":"ping"}                  // keepalive
```

`select` switches between the two cars that carry 60 Hz telemetry (the broadcast player and
the global rival chosen in the app); other cars stream only in `state` at 5 Hz.

## Troubleshooting

- **Nothing appears**: start the app (it listens by default), check the Debug tab's
  "Overlay:" line for port + connected clients; the game must be sending telemetry.
- **Stale content after editing a page**: restart the browser source once — the server
  sends `Cache-Control: no-store`, but OBS keeps a source's DOM alive while hidden.
- **OBS shows a white/colored box**: page backgrounds are intentionally transparent;
  set `--overlay-widget-bg` opaque if you want a solid card.