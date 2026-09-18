# ERCTelemetry — Status & Remaining Work

> Handoff document for future sessions. Read this first; read `docs/RELEASE.md` before any
> publish. **No git repo** — this file (plus `UPDATELOG.md`) is the only history record.
> **Nächste Ideen:** `docs/IDEAS.md` — Brainstorm/Roadmap für die Zeit nach Phase 6.
> **Mobile App:** `docs/MOBILE.md` — Bauplan für den Mobile Companion (PC- + Konsolen-Spieler).
> **Clip-Pipeline:** `docs/CLIPS-PIPELINE.md` — Arbeitsstand der neuen HDR/Audio/Disk-Video-Pipeline
> (implementiert, noch nicht live getestet — nächste Session dort weiterarbeiten).

## What this app is

Desktop app for EA F1 26 (also F1 25 with the "2026 Season Pack" UDP format) that:
1. Reads ALL game UDP telemetry → live standings/results for every player (incl. online lobbies)
2. Player telemetry dashboard with a "team telemetry" player-vs-rival comparison
3. Stream overlays: OBS browser source (local web server) AND transparent WPF overlay window
4. SQLite session history + JSON/CSV export + shareable session pages + collision clips

## Current state (last verified: 2026-09-17)

**All phases 0–6 complete.** `dotnet build` clean (0 warnings), `dotnet test` green
(801 Core + 79 ShareServer tests). **S2 Login-Härtung (2026-09-17):** beide Twitch-/Discord-Logins sind
an einen Per-Install-Possession-Nachweis gebunden (eigener Abschnitt unten). Beide Reviews
(Code + Security) grün — deren Findings umgesetzt: Onboarding-Race geschlossen (GetOrAdd statt
Last-Writer-Wins), installSecrets-Eviction (1-h-Idle-Sweep), App-Self-Heal bei verlorenem Secret
(Start-403 → frische Install-Id + einmaliger Retry), `AppSettingsService.Update` nun thread-safe,
`Cache-Control: no-store` auf den Status-Antworten mit Token. **Punkt-4-Rest:** der
InGameOverlay-Timer stoppt bei Close; die Update-Log-Markierung liegt jetzt unter der IO-Sicherung.
**Clip-Pipeline E2E-verifiziert (2026-09-16):** der komplette
Encode-Pfad ist headless gegen das gebündelte `ffmpeg.exe` getestet (`EncodePipelineTests`) —
„gesunder Clip" enkodiert zur Echtzeit-Dauer (~20 s Fenster → ~20 s MP4, h264+aac, schlägt fehl
wenn der 2x-Speed-Bug zurückkehrt) und „Audio kürzer als Video" übersteht den ‑shortest-
Early-Exit über den neuen `FfmpegCommand.IsRegularEarlyExitAsync`-Guard (Output bleibt, statt
gelöscht zu werden). Dazu robustness fixes im Capture-Pfad: Auto-Restart nach 3 Frame-Fehlern
(Device-lost/RDP), Polling der Item-Breite für Auflösungswechsel (kein `SizeChanged`-Event in der
.NET-Projection), Kill/Delete-Race beim ffmpeg-Cancel geschlossen, WAV-Fehler räumen das Temp.
App-Start-Smoke-Test ok (einzig erwartbare Twitch-Token-Ablehnung im Log). **Offen:** Live-Test im
echten Online-Rennen (Checkliste in `docs/CLIPS-PIPELINE.md`).
**S2 OAuth-Possession-Härtung (2026-09-17):** beide Login-Handshakes (Twitch, Discord)
hängen jetzt an einer Per-Install-Possession-Bindung. Die App erzeugt beim ersten Login eine
32-hex `InstallId`, der Server onboardet sie beim ersten `/…/login/start` mit einem 64-hex
`installSecret` (256 Bit), das die App DPAPI-verschlüsselt persistiert. Jeder `start`/`status`/
`delete` muss dieselbe Install-Id **und** das registrierte Secret (FixedTimeEquals) präsentieren —
ein Skript mit nur dem öffentlichen Upload-Token kann seine eigene frische Install onboarden,
aber nie für eine Fremd-Install handeln (dokumentierter Residual). Die Server-Registry ist
in-memory (Restart → App re-onboardet beim nächsten Login) und wird per 1-h-Idle-Sweep evictet.
Die Onboarding-Race bei parallelen Erst-Kontakten derselben frischen Id ist via GetOrAdd
geschlossen; verliert die App ihr Secret, wirft sie die Bindung beim nächsten Start-403
self-healend weg (frische Id + einmaliger Retry). Security-Review: kein CRITICAL; restliche
Hinweise sind operational (hinter Cloudflare-Tunnel sollte `Share__ForwardedHeaders` an sein,
sonst teilen sich alle echten Nutzer EIN Rate-Limit-Budget) oder dokumentierte Residuals
(Start-Endpoint verrät Id-Bekanntheit; DELETE bleibt idempotent-204).
**Auto-Token Session-Teilen (2026-09-11):** Enduser
teilen Sessions ohne Token-Einrichtung — die App holt den aktuellen Server-Token über den
neuen öffentlichen `GET /api/config` (Fallback: eingebauter `ShareConstants.DefaultToken`);
ein selbst eingetragener Token gewinnt weiterhin (Rotation). **ERC-Integration (2026-09-11, 0.6.3):** Ergebnis nach
einem Liga-Rennen an erdi-erc.de senden („Ergebnis an erdi-erc.de senden?"-Dialog nach der
Zielflagge — `ErcRacePromptService`/`ErcSendRaceDialog`/`ErcRaceSender`, Liga aus
`GET /api/telemetry/leagues`, `POST /api/telemetry/race` → Entwurf für den Admin; persönlicher
API-Key unter Einstellungen → ERC-Ergebnis als `X-Api-Key`); **Discord-Login** (OAuth via
ShareServer: `/discord/login/start` + `/callback` + `/status` + `/{state}`,
`DiscordLoginHost` hält den Token nur im RAM — nach App-Neustart neu einloggen; Scopes
`identify`+`guilds`+`guilds.members.read` für den Setup-Zugriff via ERC-Discord-Gilde);
**Track-Setups (Setups-Tab)** (`SetupsViewModel`/`ErcSetupsClient` auf `GET /api/setups`,
Filter nach Strecke/Spieljahr, `ErcSetupMapper`→`CarSetupSnapshot`→`SetupCodec`→ERC1-Code zum
Einfügen in F1 26); **Sprachausgabe-Timing** (VoiceAlertService: `_pending`/`_audio` als
bounded DropOldest — veraltete Ansagen werden verworfen statt verspätet gesprochen; nach der
Zielflagge keine Live-Analyse/Proximity mehr). **Live AI-Analyse (2026-09-08):** vier neue
Renn-Assistenten im VoiceAlertService-Live-Loop (1-s-Poll auf dem `LiveAnalysisSnapshots`-Kanal):
(1) **Live-Analyse** — Digest (Rolling-Window der letzten 5 Runden, Reifen-Wear, Tank-Prognose)
+ Trigger (Reifen ≥ 60 % einmal pro Stint, Tank-Defizit, Tempo-Trend, Status alle 5 Runden),
Layer-1-Template (frei, ohne Key) oder LLM-Politur (Key); (2) **Boxenstopp-Timing** —
`PitStopAdvisor` (deterministisch, baut auf StrategyAdvisor/FuelCalculator auf): „Box in
Runde X" / „weiter fahren" / „jetzt rein", Tank-Notfall überstimmt, Undercut-Fenster bei
nahem Hintermann; (3) **Rivalen-Trends** — `RivalDigestBuilder` verfolgt den Rivalen
(Auto vor/hinter dem Spieler): Boxenstopp, Reifenwechsel, schnelle Runde (Cooldown 3 Runden),
Layer-1-Template oder LLM; (4) **Reifen-Temperatur** — `TyreTempMonitor` (deterministisch):
Vorderreifen > 105 °C / Bremsen > 800 °C mit 20-s-Cooldown. Alle vier laufen unter
`LiveAnalysisEnabled` (Default aus) + `VoiceAlertsEnabled`. **Trackmap-Rework (2026-09-08):** die Overlay-Map
(`map.html`) und das HUD-Map-Widget zeigen jetzt **echte Strecken-Layouts** mit Dots
exakt auf der Linie — `Tracks/TrackFitter` (Core) fittet das eingebettete Layout per
Ähnlichkeitstransformation (PCA-Seed → getrimmter 2-Stufen-ICP: Punkt-zu-Polylinie für
Form/Skala, Punkt-zu-Vertex für die Rotation, da radiale Projektion auf glatten Ringen
rotationsblind ist; Spiegel-Hypothese explizit, Lock-Gate RMS/Scale/Coverage, frozen
nach Lock, eine Instanz pro Konsument). Layouts stammen aus `tools/TrackLayoutGen`
(bacinger/f1-circuits, NICHT in der slnx) → `src/ERCTelemetry.Core/Tracks/track-layouts.json`
(PascalCase-Keys `Version`/`Tracks`/`Points`/`LengthM`, eingebettete Resource, Lookup mit
`F1_`-Strip + Aliase). Browser bekommt das Layout als neue Wire-Message `tracklayout`
(schon in Weltkoordinaten transformiert) — `map`-Message unverändert. Unbekannte Strecke
oder nie gelockt → bisheriges Fallback-Verhalten. **QOL-Runde:** Reconnect-Hinweis,
`?w=`/`&h=`-Canvas-Größe, Streckenname im Map-Titel, `_maptest.html` entfernt,
Select-Highlight, Label-Überlappungs-Unterdrückung, DNF ausgeblendet, Block-Toggles auf
den 7 Mehr-Block-Seiten (die 5 Einzel-Widget-Seiten player-card/commentator/map/relative/
timing-tower haben keine passenden Block-Keys), DE-Farbschema via CSS-Vars überall,
Race-Control robust bei Reconnect, Copy-URL mit Größen-Parametern, HUD-Widget-Hotkey
Ctrl+Shift+M (Karte) und Ctrl+Shift+D (Debug-Tab). **Collision clips are a real video pipeline**
(HDR-correct Windows.Graphics.Capture + WASAPI loopback audio + disk buffer + ffmpeg mux,
see below). IDEAS.md: **Voice-Alerts (TTS)**, **Rennzusammenfassung
(Layer 1)**, **Setup-Sharing**, **Wetter-Radar**, **Undercut/Overcut-Rechner**,
**Sektor-Deltas live**, **Karriere-/Saison-Tracking**, **Twitch-Chat-Commands**,
**Live-AI-Kommentator (Layer 1)**, **AI-Coach (Layer 1)**, **Driver-Rating / ELO (lokal)**
und **LLM-Layer 2 (Ollama)** umgesetzt (leichteste zuerst).

- **Core (`src/ERCTelemetry.Core`, net10.0):** `Telemetry/UdpListener` (UDP :20777, format-2026
  validation, raw tap), `Telemetry/UdpForwarder` (re-sends raw datagrams to up to 8 local targets,
  e.g. RaceLab/SimHub), `Telemetry/PacketRecorder` (.f1rec gzip) + `ReplayPlayer`,
  `Session/SessionStateStore` (single-writer aggregator: session lifecycle, standings, lap-completion
  with flashback suppression, race events, player/rival frames, empty-slot filtering),
  `StandingsCalculator`, `RaceEventMapper`, `TelemetryComparer`, `EventFeed`, `OverlayProtocol/`
  (wire records, `OverlayThrottle` 30/5 Hz + change detection, `OverlayMessageFactory`,
  `OverlayClientParser`, `OverlayBlocks`), `Persistence/` (`TelemetryDb` SQLite + `ResultsExporter`),
  `Analysis/` (PaceAnalyzer, StrategyAdvisor, OvertakeDetector, FuelCalculator, LapTraceComparer,
  BlindSpotCalculator, RaceReportBuilder, DriverDuel, CoachReport/CoachReportBuilder — Layer-1
  AI-Coach: DuelReport-Findings → deutsche Ratschläge, DriverRatingCalculator — lokales ELO-Rating
  über alle Race-Sessions; Live-Analyse: LiveAnalysisDigest/LiveAnalysisDigestBuilder (Rolling-Window
  der letzten 5 Runden, Trend, Reifen-Wear, Tank-Prognose), LiveAnalysisTrigger (Reifen ≥ 60 % /
  Tank-Defizit / Tempo-Trend / Status alle 5 Runden), PitStopAdvisor (Boxenrunde aus Reifenabbau +
  Tank + Undercut-Fenster, baut auf StrategyAdvisor/FuelCalculator auf), RivalDigestBuilder
  (Rivalen-Aktionen: Boxenstopp/Reifenwechsel/schnelle Runde, Cooldown 3 Runden), TyreTempMonitor
  (Vorderreifen > 105 °C / Bremsen > 800 °C, 20-s-Cooldown)), `Commentary/` (CommentaryPlanner —
  Layer-1-Regeln des Live-Kommentators, läuft im Overlay-Pump), `TwitchChat/` (ChatCommandParser,
  DriverNameMatcher, ChatAnswerBuilder, TwitchChatService, TwitchIrcClient, TwitchOAuth —
  Twitch unterstützt KEIN PKCE, der Code-Tausch braucht das `client_secret` aus der
  Server-Config; Client-ID `8cekqatgkipstivsw63q2b44hati81` in `TwitchOAuth.ClientId`,
  TwitchLoginClient — App-seitiger Client für den Server-Handshake: start/status/delete),
  `Share/` (manifest builder, page renderer, paths), `Llm/` (OllamaClient — dünner
  HTTP-Client auf `{baseUrl}/api/chat` + `{baseUrl}/api/tags` für die Modell-Liste, null bei
  jedem Fehler; `IRaceCommentator`/`IRaceCoach`/`IRaceSummarizer` +
  `LlmRaceCommentator`/`LlmRaceCoach`/`LlmRaceSummarizer` — deutsche System-Prompts, bündeln
  die Layer-1-Ergebnisse; Live-Analyse: `ILiveAnalyst`/`LiveAnalysisTemplate` (Layer 1, frei) +
  `LlmLiveAnalyst` (Layer 2), `IRivalAnalyst`/`RivalTemplate` (Layer 1) + `LlmRivalAnalyst`
  (Layer 2) — beide null bei Fehler → Fallback auf das Template), `Tts/` (`EdgeTtsClient` — dünner `ClientWebSocket`-Client auf dem
  Edge-readaloud-Endpoint, `SynthesizeAsync` → MP3-Bytes, null bei jedem Fehler; `EdgeTtsAuth`
  — Sec-MS-GEC-Token + Endpoint-URL, pure statische Funktionen; `EdgeTtsVoices` — kuratierte
  deutsche Neural-Stimmen, Default `de-DE-KatjaNeural`), `Settings/` (`AppSettings` record +
  `AppSettingsStore`), `Tracks/` (`TrackLayoutCatalog` — Embedded-Resource-Loader für
  `track-layouts.json` mit `F1_`-Strip + Alias-Lookup; `TrackFitter` — Layout→Welt-Fit
  für die Trackmap, siehe oben), `Networking/LocalIpProvider`,
  `Update/` (manifest + evaluator, `UpdateFileManifest`/`UpdateFileComparer`/`UpdateDownloader`
  for the delta update, `UpdateLogSection`).
- **App (`src/ERCTelemetry.App`, net10.0-windows, WPF, AssemblyName=ERCTelemetry):**
  `Composition/AppServices` (channels: Packets 1024 DropOldest → aggregator task → Snapshots 64 /
  Events 1024 for the dashboard + dedicated `OverlaySnapshots`/`OverlayEvents`/`OverlayWindowSnapshots`/
  `OverlayWindowEvents`/`ClipEvents`/`VoiceEvents`/`TwitchSnapshots` pairs — **never share a
  channel between consumers**),
  `OverlayServer/` (Kestrel on 127.0.0.1:8090, static wwwroot, `/ws` WebSocket hub with per-client
  bounded channels + config fanout), `Dashboard/` (Setup / Dashboard / Race Control / History /
  Settings / Debug / Report panels), `OverlayWindow/` (transparent click-through HUD, 10 draggable
  widgets, hotkeys Ctrl+Shift+O/R/D/M), `Composition/` (PersistencePump, AppSettingsService,
  TrayIconService, ScreenCaptureService, ClipRecorderService, ShareService, VoiceAlertService,
  LlmService — liest Settings live, baut den OllamaClient bei Key/Modell/URL-Änderung neu,
  `IsConfigured`/`Status`/`TestAsync`/`GetModelsAsync`; gedrosselte LLM-Kommentar-Schleife im
  OverlayWebHost (45 s, SemaphoreSlim, letzte 20 Layer-1-Zeilen als Kontext);
  `LlmSummary`/`LlmCoach`-Zeilen im Rennreport via fire-and-forget + `_rebuildVersion`-Guard;
  Settings-Tab: Verbindungstest lädt die Modell-Liste in ein editierbares Dropdown),
  `VoiceAlerts/` (Core `VoiceAlertPlanner` — German TTS alerts; Ausgabe via
  Microsoft-Neural-Stimme (Edge-TTS, `Core/Tts/EdgeTtsClient`), serielle Playback-Queue
  (NAudio + NLayer, MP3), pro-Ansage-Fallback auf `System.Speech` bei Offline/Fehler; opt-in
  in Settings → Verhalten, „STIMME"-Dropdown editierbar, Default de-DE-KatjaNeural;
  Live-Loop `RunLiveAnalysisAsync` auf dem `LiveAnalysisSnapshots`-Kanal: Live-Analyse +
  PitStopAdvisor + RivalDigestBuilder + TyreTempMonitor, alle unter `LiveAnalysisEnabled`),
  `Update/` `Update/`
  (UpdateService, UpdateLogWindow, `apply-update.ps1` delta apply script), `Composition/FirewallService`
  (app creates the UDP firewall rule on first start — installer runs without admin).
- **Clips (`Clips/`):** HDR-correct video pipeline replacing the old GDI frame-by-frame
  capture. `HdrFrameSource` (Windows.Graphics.Capture via `IGraphicsCaptureItemInterop`
  COM interop — `DisplayArea` is NOT projected into .NET; requests SDR `B8G8R8A8UIntNormalized`
  so the system tone-maps HDR content, no more overexposed clips; D3D11 device with WARP
  fallback for RDP/VM), `AudioLoopbackSource` (NAudio 3.1 `WasapiRecorderBuilder` loopback —
  `WasapiLoopbackCapture` is deprecated; feeds a rolling `AudioRingBuffer`), `RollingFrameStore`
  (disk buffer of JPEG frames in `seg-{UtcTicks}.bin` segment files, ~1s each, only the last
  window kept — RAM stays flat; moved to Core for unit tests), `ScreenCaptureService`
  (orchestrates sources + store, throttles frames to the configured FPS, `SaveClipAsync`
  slices the collision window, writes a temp WAV and muxes via `FfmpegCommand.BuildWithAudio`
  → H.264/AAC MP4 with `-shortest`). **Speed-Fix (2026-09-11):** Clips liefen in doppelter/
  erhöhter Geschwindigkeit bei normalem Ton — die reale Aufnahmerate fiel unter die
  eingestellten FPS (1-Buffer-Pool + JPEG-Encode blockierte den Frame-Pool), ffmpeg enkodierte
  trotzdem mit den eingestellten FPS. ffmpeg bekommt jetzt die real gemessene Framerate
  (`FfmpegCommand.EffectiveFps`); `HdrFrameSource` nutzt 2 Pool-Buffers und gibt den Frame vor
  dem Encode frei; Throttle akzeptiert nur streng neuere Frames, das Clip-Fenster wird vor dem
  Encode chronologisch sortiert. Settings: FPS 1–60 + audio-device dropdown.
- **Robustheit (2026-09-16, Review-Fixes + E2E):** `FfmpegCommand.IsRegularEarlyExitAsync`
  erkennt den regulären ‑shortest-Early-Exit (Audio kürzer als Video) und behält die MP4 statt
  sie zu löschen + bis 5 min Save-Gate-Blockade; `HdrFrameSource` stoppt nach 3 on-
  einanderfolgenden Frame-Fehlern (Device-lost/RDP), der ManageLoop erstellt Pool+Device neu;
  Auflösungswechsel wird per Item-Breite gepollt (`SizeChanged` ist NICHT in der .NET-Projection)
  → Pool-Neustart + Store-Clear; Kill-Fehlerpfad killt den ffmpeg-Baum und wartet bounded
  (2 s), bevor die Part-Datei gelöscht wird; `EncodePipelineTests` beweisen headless die
  Echtzeit-Dauer ("gesunder Clip") und den Early-Exit-Guard gegen das echte ffmpeg.exe.
- **Overlay pages (`wwwroot/overlay/`):** standings, player-card, race-control, broadcast, h2h,
  commentary, commentator (AI-Kommentator-Feed), tyres, timing-tower, relative, map +
  `overlay.js`/`overlay.css` (CSS-var + DOM-id contract in `overlay/README.md`); `map` rendert
  das gefittete Streckenlayout (Wire-Message `tracklayout`), Block-Toggles auf den 7
  Mehr-Block-Seiten (standings/race-control/broadcast/h2h/commentary/telemetry/tyres) —
  player-card, commentator, map, relative und timing-tower haben keine Block-Keys.
- **ShareServer (`src/ERCTelemetry.ShareServer`, net10.0, minimal API):** session pages + clip
  uploads under `https://telemetrie.erdi-erc.de/s/{uid}`; token auth, retention cleanup;
  Twitch-Login-Handshake (server owns the code exchange with the client secret from its
  config `Share__TwitchClientSecret` — Twitch unterstützt kein PKCE; `POST /twitch/login/start`,
  public `GET /twitch/callback` mit Doppel-Redirect-Guard, `GET /twitch/login/status`,
  `DELETE /twitch/login/{state}`, 15-min-Expiry).
- **Chunked clip upload (2026-09-11):** Cloudflare Free caps request bodies at **100 MB** —
  verified empirically (95 MB OK, 105 MB→413, 150 MB→502). Proxy stack is Cloudflare →
  cloudflared tunnel → NPM (nginx `client_max_body_size 200M` is NOT the bottleneck) →
  ShareServer. Clips >100 MB (`.NET StreamContent` gets `IOException: Error while copying to
  a stream` when Cloudflare aborts mid-stream) are therefore split client-side into ≤80 MB
  parts; `PUT /api/sessions/{uid}/clips/{file}?part=N&parts=M`, server writes `*.part{N}`
  and assembles on the last part (rejects incomplete sets). Protocol constants/partition math
  live in `ERCTelemetry.Core/Share/ClipUpload.cs` so app + server stay in sync; client side is
  `ERCTelemetry.App/Share/ClipSubStream.cs` (bounded stream so `Content-Length` = part size) +
  `ShareService.UploadClipAsync`. **Note:** the running server accepts chunks, but the installed
  app 0.6.3 client cannot split yet — a client rebuild/release is still needed for end-to-end.
- **Auto-Token Session-Teilen (2026-09-11):** Enduser brauchen keinen Token mehr — `ShareService`,
  `TwitchLoginClient`, `DiscordLoginClient` bekommen den Server-Token über den neuen **öffentlichen**
  `GET /api/config`-Endpoint (`ShareTokenResolver`: Settings-Override → /api/config mit 5-min-TTL-Cache
  → Fallback `ShareConstants.DefaultToken`; Fetch-Timeout 10 s, damit ein toter Server nicht den
  Upload blockiert; bei einem Ausfall nach TTL-Ablauf wird der letzte bekannte Token weiterverwendet
  statt auf den Default zu degradieren, und eine Caller-Cancellation wird nie geschluckt). Der Token
  ist damit aus App + Endpoint extrahierbar — der `X-Share-Token`-Gate
  schützt nur gegen Uploads ohne die App (wie gewählt). `ShareConstants.DefaultToken` ist auf den
  echten `Share__Token` der Server-Umgebung gesetzt (Stand 2026-09-11) — bei einer Server-Token-
  Rotation diesen Wert mitsynchronisieren, sonst greift der Fallback nur, solange beide übereinstimmen.
  `GET /api/config` **deployed & live** (2026-09-11). **ShareServer-Deploy (bewährt, kein Git):**
  `systemctl stop erctelemetry-share` → `scp -r installer/shareserver-stage/* root@192.168.100.73:/opt/erctelemetry-share/`
  → `systemctl start erctelemetry-share` → `curl https://telemetrie.erdi-erc.de/api/config`. Wichtig: den
  Dienst **vor** dem scp stoppen — beim laufenden Dienst schlägt das Überschreiben des Haupt-Binary mit
  `ETXTBSY` ("text file busy") fehl. Config kommt per systemd-Env (`Share__Token`,
  `ASPNETCORE_URLS=http://0.0.0.0:5000`, `ASPNETCORE_ENVIRONMENT=Production`, `Share__RootPath`, Retention,
  Twitch/Discord-Secrets); der `appsettings.json`-Token im Build bleibt bewusst leerer Platzhalter.
- **Tests:** `tests/ERCTelemetry.Core.Tests` (785, davon 8 `RollingFrameStore` + 2 neue
  `EncodePipelineTests` gegen das echte ffmpeg.exe), `tests/ERCTelemetry.ShareServer.Tests` (63).
  (Chunk tests use per-session uids 42_001–42_004 — the shared fixture root + shared uid 42000
  polluted `Assert.False(File.Exists(...))` when the assembly test created the file first.)
- **Port note:** the user runs the game on UDP 20778 (changed in-game deliberately); settings.json
  reflects that — do NOT "fix" it back to 20777.

## Hard-won facts about F1Game.UDP 26.0.0 (do not re-learn these)

- Packet arrays are **`Array24<T>` (24 slots)** although the game has max 22 cars — always guard
  loops with `Math.Min(span.Length, TelemetryConstants.MaxCars)`; state arrays in the store are 22.
- `CarStatusData.ErsStoreEnergy` is a **float (Joules)**, `DrsAllowed` is **bool**,
  `LapData.Penalties` and `FinalClassificationData.PenaltiesTime` are **byte**.
- Team enum member is `RedBullRacing` (no `RedBull`). Enum members are NOT in the NuGet XML docs —
  check https://github.com/volodymyr-fed/F1Game.UDP (branch `master`, tag `v26.0.0`) when unsure.
- `UnionPacket` is an explicit-layout union: construct from any typed packet via implicit
  conversion; `TryGetXxxPacket(out var)` guards with PacketType.
- Packet records are `readonly record struct` with init properties — tests build them in code
  (object initializers + `T[]` → `Array24<T>` implicit conversion, short arrays are zero-padded).
- `Array24<T>.AsSpan()` returns the full 24-slot span even if fewer were set — empty slots arrive
  zeroed from the game (online lobbies); the store filters them (all-zero LapData ⇒ `_timings[i]=null`).
- **CarMotionData g-forces are quantised int16 — ÷1000 for g** (2026 format, verified against the spec).
- **Radar orientation:** in this game's world, `up × forward` is the driver's **LEFT** (it mirrored
  the radar); the blind-spot calculator uses `right = forward × up = (−fz, fx)`.
- **Microsoft.Data.Sqlite pitfall**: `SqliteParameter` requires `ParameterName` when built with
  `CreateParameter()`; named parameters only bind to `@pN` placeholders — a `?` matches **unnamed**
  parameters only. Mixing named params with `?` SQL → "Must add values for the following parameters".
- **WPF `Run.Text` binding defaults to TwoWay** — binding it to a get-only property
  (`LapCount`) throws `InvalidOperationException` at first layout and crashes startup
  (the exception-logging hook in `App.xaml.cs` captures these to `error.log`).
- **F1Game.UDP parser throws `NotEnoughBytesException`** (e.g. "not enough bytes to parse
  MotionDataPacket") on truncated/corrupt data — it is NOT an `ArgumentException`; anything
  parsing untrusted bytes (replayer, future parsers) must catch it explicitly.
- **C# raw-string literals cut the newline before the closing `"""`** — concatenating literals glued
  `@p1ORDER BY seq` → "near BY"; use one interpolated literal instead.
- **`AppSettings` `[JsonConstructor]` requires the first 7 parameters** (`udpPort`…`autoStartListening`)
  without defaults — test calls with only named later args need all 7 explicitly.
- **XAML gotcha:** German quotes „…" in attributes must use the closing U+201D (") — ASCII `"` breaks
  the XML (MC3000).
- **Windows SDK projections (`Windows.Graphics.*`) need BOTH `TargetPlatformIdentifier` AND
  `TargetPlatformVersion` in the csproj.** The SDK's `Microsoft.NET.TargetFrameworkInference.targets`
  re-derives TargetPlatformVersion from the TFM when the identifier is empty, resetting it to 7.0
  and silently dropping `Microsoft.Windows.SDK.NET.Ref` — `Windows.Graphics.Capture` then doesn't
  compile. Set `<TargetPlatformIdentifier>Windows</TargetPlatformIdentifier>` +
  `<TargetPlatformVersion>10.0.26100.0</TargetPlatformVersion>` explicitly.
- **`DisplayArea` / `GraphicsCaptureItem.TryCreateFromDisplayId` are NOT in the .NET Windows SDK
  projections.** Only `GraphicsCaptureItem`/`GraphicsCaptureSession`/`GraphicsCapturePicker` are
  projected. To capture a monitor without the picker, go through the `IGraphicsCaptureItemInterop`
  COM interface (GUID `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356`, `CreateForMonitor(HMONITOR, REFIID, void**)`)
  via `WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", iid)` +
  `Marshal.GetObjectForIUnknown(factory.GetRef())` — `Marshal.GetActivationFactory` is gone in .NET 10.
- **NAudio 3.1 deprecates `WasapiLoopbackCapture`** — use `new WasapiRecorderBuilder().WithDevice(d)
  .WithLoopbackCapture().Build()` (`WasapiRecorder`); the data handler is
  `void Invoke(ReadOnlySpan<byte>, AudioClientBufferFlags, long, long)`.
- **File-sharing gotcha (RollingFrameStore):** a reader opening a segment the capture loop still
  writes must use `FileShare.ReadWrite`, not `FileShare.Read` — the writer's `FileAccess.Write`
  handle clashes with a plain Read share (IOException).
- **Twitch OAuth: NO PKCE.** Twitch does not support PKCE — the authorization-code token
  exchange REQUIRES the `client_secret` (missing → `{"status":400,"message":"Invalid client
  credentials"}`). A valid client_id + fake code returns `"Invalid authorization code"`; an
  unknown client_id returns `"invalid client"` (lowercase). The secret lives in the
  ShareServer config (`Share__TwitchClientSecret`), never in the app.
- **Edge-TTS (readaloud) — inoffizieller Dienst, kein API-Key, aber Risiko:** Endpoint
  `wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1`, Query braucht
  `TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4` (öffentliches Token, kein Secret),
  `Sec-MS-GEC`, `Sec-MS-GEC-Version=1-130.0.2849.68` und eine dash-lose `ConnectionId`.
  **Sec-MS-GEC:** SHA-256 (HEX, uppercase) über `"<WindowsFileTime-100ns, auf 5 min abgerundet><Token>"`,
  wobei WindowsFileTime = `(unixSeconds + 11644473600)` → floor 300 s → ×10.000.000. Nach dem
  `ClientWebSocket.Connect` zwei Text-Frames (speech.config mit `outputFormat`, dann ssml mit
  `<voice name='..'><prosody rate='+10%'>TEXT</prosody>`; X-RequestId = ConnectionId; Trailing-Z
  auf X-Timestamp ist Absicht), Antwort = Binär-Frames (2-Byte-Big-Endian-Headerlänge →
  Header → Audio) bis Text-Frame `Path:turn.end`, Audio-Bytes konkatenieren. **Ausgabeformat ist
  MP3** (`audio-24khz-48kbitrate-mono-mp3`), nicht WAV — Wiedergabe via NAudio 3.1 +
  NLayer.NAudioSupport 3.0 (`Mp3FileReaderBase` + genested `FrameDecompressorBuilder`, NAudio 3
  hat den 2-Argument-`Mp3FileReader`-Ctor entfernt; `MemoryStream` braucht `using System.IO;`,
  WPF/WinForms-ImplicitUsings reichen nicht). Referenz: rany2/edge-tts (drm.py). 2025-08-… galt
  GEC als Pflicht (403 ohne). Darum pro-Ansage-Fallback auf `System.Speech` — ein Fehler darf
  die Alerts nie verstummen lassen.

## Remaining work (in order)

### Small deferred items (from the Phase 2 code review)
- `AppServices.Dispose` blocks up to 2s worst case on window close (acceptable; could observe
  cancellation instead of catching AggregateException).
- `ApplySession` still only updates SessionTimeLeft/Weather/PlayerCarIndex on same-UID refresh; if the
  game ever changes Track/TotalLaps mid-session they'd be ignored (rare).
- `.f1rec` replay fixture: ✅ mechanism is in place (PacketReplayer + 4 tests incl. an optional
  fixtures test). Only the real recording itself is still open — record a session via the Debug tab
  (Record button), drop it at `tests/Fixtures/*.f1rec`, run `dotnet test`.

### Open user verification (needs a live game / the user's eyes)
- Live session: race → restart app → session present with results; export opens.
- Trackmap live: nach ~1 Runde zeigt `map.html` (und das HUD-Map-Widget) die echte
  Strecken-Outline mit Dots **auf** der Linie; Session-Restart → Fitter resettet; neuer
  Browser-Client mid-Session → Outline sofort; unbekannter Track → Fallback ohne
  `error.log`-Einträge. HUD-Widget-Hotkey Ctrl+Shift+M toggelt die Karte.
- Setup-Tab zeigt die echte LAN-IP (nicht 169.254.x.x); Konsole im selben Netzwerk → grüne Lampe
  „F1 26 sendet Telemetrie" + Pakete im Debug-Tab.
- Theme swap on every surface, resize/maximize/drag, close→tray→restore, Ctrl+Shift+O/R, standings
  right-click rename, replay combo, copy buttons, settings save, DPI 125/150%.
- HUD German-Schema-RPM-Gold, Tabs (0.4.0 visual sign-off).
- UDP-Forwarding: Ziel `127.0.0.1:20779` → Datagramme kommen bei einem UDP-Empfänger an.

## Release history

| Version | Date | Inhalt |
|---|---|---|
| 0.7.0 | 2026-09-18 | **F1-Broadcast-Paket** (Kommentatoren-Stream): 4 neue Overlay-Seiten — `grid.html` (Startaufstellung im F1-TV-Look, Teamfarben, auto gridPosition), `championship.html` (Fahrer- + Teamwertung der ERC-Saison), `lower-thirds.html` (TV-Banner unten links: FL/Strafe/Ausfall/Box/Angriff-auf-P1 automatisch + manuell per URL-Parameter), Kommentar-Popup in `alerts.html` („ERC · Mikrofon", onCommentary, statt TTS — TTS-Sprecher-Prototyp verworfen). Fahrerdaten von erdi-erc.de: `tools/erc-drivers.js` (Scraper: Teams → /Profile/<discord-id>, 74 Profile → `wwwroot/overlay/data/erc-drivers.json`, Cache 6 h, `--frisch`) + `erc-names.js` Matcher (`window.ercNames.zuFahrer()`). Fix: telemetry.html `trackTemperature` („undefined °C"). Zusätzlich außerhalb der App: `tools/regie.js` Auto-Regie (SLOBS-Szenenschaltung, Protokoll aus app.asar verifiziert), F1-Themepack (9 Szenen). Release-Gate: 0 Warnungen, 801 Tests grün, Upload verifiziert. **Offen:** neue Overlays in Streamlabs-Szenen einbinden (f1-themepack-JSON), `erc-drivers.js --frisch` vor Liga-Rennen |
| 0.6.4.2 | 2026-09-17 | **Login-Härtung + Stabilitätspaket**: Twitch/Discord-Logins gegen fremde Zugriffe abgesichert (S2 — Per-Install-Nachweis: geräteeigener Install-Schlüssel für Start/Status/Löschen, Onboarding-Race via GetOrAdd geschlossen, 1-h-Idle-Eviction der Secret-Registry, App-Self-Heal bei verlorenem Secret, `Cache-Control: no-store` auf Token-Antworten); Overlay-XSS-Härtung (Escaping + CSP); Stabilitäts-Fixes (TelemetryDb-Concurrency, Clip-Capture-Neustart nach Geräteverlust, JPEG-Throttle, sauberer Teardown inkl. Overlay-Timer, Task-Fault/Update-Log-IO-Logging). Release-Gate: 0 Warnungen, 801 Core + 79 ShareServer-Tests grün, Upload verifiziert. **Offen:** Live-Test Clip-Pipeline im echten Rennen |
| 0.6.4.1 | 2026-09-11 | **Session teilen ohne Token-Einrichtung**: Enduser teilen Aufnahmen ohne Konfiguration — die App holt den Server-Schlüssel automatisch über den öffentlichen ShareServer-Endpoint `GET /api/config` (Settings-Override behält Vorrang, Fallback = eingebauter `ShareConstants.DefaultToken`, jetzt auf den echten Server-`Share__Token` gesetzt); robustere Token-Auflösung (letzter gültiger Schlüssel bei Server-Ausfall statt Default-Degradierung, Cancellation wird nie geschluckt, ein Resolver/Host statt pro Login). Server-Build mit `/api/config` **deployed & live** (ShareServer-Deploy siehe Abschnitt oben) |
| 0.6.4 | 2026-09-11 | **Session teilen mit großen Clips** (Fix): Cloudflare-Free-Limit (100 MB pro Request) umgangen — Clips >100 MB lädt die App jetzt in ≤80-MB-Teilen hoch (`PUT ?part=N&parts=M`), der ShareServer setzt sie serverseitig zusammen; „Teilen fehlgeschlagen: Error while copying to a stream" bei großen Clips behoben. Server-Build mit Chunk-Protokoll bereits deployed (siehe ShareServer-Abschnitt) |
| 0.6.3 | 2026-09-11 | **ERC-Integration**: Ergebnis nach Liga-Rennen an erdi-erc.de senden („Ergebnis an erdi-erc.de senden?“-Dialog, Liga wählen → Entwurf für den Admin; persönlicher API-Key), Discord-Login (OAuth via ShareServer, Token nur im RAM), Track-Setups (Setups-Tab → ERC1-Share-Code für F1 26, Zugriff über Discord-Gilde); **Sprachausgabe-Timing** — Ansagen im richtigen Moment, veraltete werden verworfen, nach Zielflagge keine veraltete Live-Analyse |
| 0.6.2 | 2026-09-08 | **Live AI-Analyse** (Sprachausgabe): vier Renn-Assistenten im VoiceAlertService-Live-Loop (1-s-Poll auf `LiveAnalysisSnapshots`): Live-Analyse (Digest Rolling-Window 5 Runden + Trigger Reifen ≥ 60 % / Tank-Defizit / Tempo-Trend / Status alle 5 Runden, Layer-1-Template frei oder LLM-Politur), Boxenstopp-Timing (PitStopAdvisor, deterministisch), Rivalen-Trends (RivalDigestBuilder, Cooldown 3 Runden), Reifen-Temperatur (TyreTempMonitor > 105 °C / Bremsen > 800 °C, 20-s-Cooldown). Alle unter `LiveAnalysisEnabled` (Default aus) + `VoiceAlertsEnabled` |
| 0.6.1 | 2026-09-07 | Kollisions-Clips als echte Video-Pipeline: HDR-korrekte Aufnahme (Windows.Graphics.Capture), WASAPI-Loopback-Ton, Disk-Puffer (RollingFrameStore), ffmpeg-Mux → H.264/AAC-MP4; FPS 1–60 + Audio-Gerät einstellbar. **Noch nicht live im echten Rennen getestet** (siehe CLIPS-PIPELINE.md) |
| 0.6.0 | 2026-09-07 | Sprach-Alerts auf Microsoft-Neural-Stimmen (Edge-TTS) umgestellt: natürliche Katja/Conrad-Stimme, wählbar in Settings → Verhalten („STIMME"-Dropdown, editierbar), serielle MP3-Wiedergabe (NAudio+NLayer), pro-Ansage-Fallback auf `System.Speech` bei Offline/Fehler |
| 0.5.2 | 2026-09-07 | Twitch-Login über den ShareServer (kein localhost mehr): Server macht PKCE + Code-Tausch, App pollt Status; Doppel-Redirect-Guard behebt „Invalid code" endgültig |
| 0.5.1 | 2026-09-07 | Twitch-Login-Fix: `state`-Token gegen verbrauchte Codes aus alten Browser-Tabs („Invalid code"), ShareServer reicht `state` durch |
| 0.5.0 | 2026-09-07 | Twitch-Login vereinfacht: „Mit Twitch verbinden" (PKCE, eingebettete Client-ID, Kanal-Auto-Erkennung), ShareServer-`/twitch/callback`-Endpoint deployed |
| 0.4.2.1 | 2026-09-07 | Delta-Updates (nur geänderte Dateien, kein UAC, Auto-Neustart), Nutzerordner-Installation, Firewall durch App, Update-Log-Button, Robustheits-Fixes |
| 0.4.2 | 2026-09-07 | UDP-Weiterleitung an andere Apps (bis zu 8 Ziele, Settings-Expander „WEITERLEITUNG") |
| 0.4.1 | 2026-09-07 | History-Fixes: nur eigene Events, echte Rundenlänge, Teamnamen ohne „26" |
| 0.4.0 | 2026-09-07 | F1-TV-UI-Rework, Dark-Mode-Lesbarkeit, HUD-Farbkorrekturen |
| 0.3.0 | 2026-09-07 | Session teilen (ShareServer, `telemetrie.erdi-erc.de/s/{uid}`) |
| 0.2.1 | 2026-09-05 | History-Tab-Rework „Deine Sessions", Strafen/Verwarnungen pro Runde/Kurve |
| 0.2.0 | 2026-09-05 | History-Tab-Rework (gleicher Tag, von 0.2.1 abgelöst) |
| 0.1.0 | 2026-09-03 | Beta-Start: Installer + In-App-Update-Pipeline, Release-Checkliste |

## Open question for the user

None. The dead classic .NET Framework folder `ERCTelemetry/` was deleted (user confirmed, 2026-08-29).

## Verification checklist (every phase)

1. `dotnet build` + `dotnet test` green.
2. Live: F1 26 → Telemetry Settings: UDP on, IP 127.0.0.1 (or 255.255.255.255), port 20777,
   format = 2026 Season Pack, all packet types enabled. Windows Firewall is handled by the
   installer (rule „ERCTelemetry Telemetrie (UDP)", inbound UDP 20777) — no in-app prompt.
3. Phase 3+: OBS browser source as above.
4. Phase 6: Settings tab — theme preview + Save persists to `%LOCALAPPDATA%\ERCTelemetry\settings.json`
   and survives an app restart; port changes restart listener/overlay live; tray icon visible
   (double-click opens, Exit closes — also when close-to-tray is on); balloon on session finish;
   closing with close-to-tray on hides instead of exiting; launching the exe a second time is
   blocked with a message.
