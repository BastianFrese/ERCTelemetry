# Kollisions-Clips: Video-Pipeline (HDR + Audio + Disk-Buffer)

> **Status: als 0.6.1 veröffentlicht (2026-09-07) — aber NOCH NICHT live im echten Rennen
> getestet.** Dieses Dokument ist der Arbeitsstand für die nächste Session.
> Stand: 2026-09-16.
>
> **Live-Test-Feedback gefixt (2026-09-11):** Clips liefen in doppelter/erhöhter Geschwindigkeit
> bei normalem Ton. Ursache: der Frame-Pool hatte nur 1 Buffer und der JPEG-Encode blockierte
> ihn — die echte Aufnahmerate fiel unter die eingestellten FPS, ffmpeg enkodierte trotzdem mit
> den eingestellten FPS → Video kürzer als real → spielte schneller als das (korrekt getaktete)
> Audio. **Fix:** ffmpeg bekommt jetzt die *tatsächlich gemessene* Framerate des Clips
> (`FfmpegCommand.EffectiveFps`, aus den Frame-Timestamps), und `HdrFrameSource` gibt den
> Pool-Frame direkt nach dem Surface-Copy frei (Encode erst danach) + 2 Buffers → Rate und
> Takt erreichen wieder die eingestellten FPS. Damit 2-Buffer-Out-of-Order-Frames weder
> Store-Reihenfolge noch ffmpeg-Feed brechen: die Throttle akzeptiert nur noch streng neuere
> Frames, `SaveClipAsync` sortiert das Fenster chronologisch; `EffectiveFps` rundet
> AwayFromZero. Siehe „Zwei echte Bugs" unten für die Details.
>
> **E2E-Verifikation abgeschlossen (2026-09-16):** Der komplette Encode-Pfad ist jetzt headless
> mit dem gebündelten `ffmpeg.exe` gegen echte JPEG-Frames + WAV getestet
> (`EncodePipelineTests`) — inkl. simulierter Frame-Drops (echte Rate < eingestellte FPS) und
> Audio-kürzer-als-Video (‑shortest-Early-Exit-Guard). Siehe „End-to-End verifiziert" unten.

## Was umgesetzt wurde

Die alte GDI-Aufnahme (Frame-für-Frame, überbelichtet bei HDR, kein Ton) ist ersetzt durch
eine echte Video-Pipeline:

| Komponente | Datei | Zweck |
|---|---|---|
| `HdrFrameSource` | `src/ERCTelemetry.App/Clips/HdrFrameSource.cs` | Windows.Graphics.Capture, fragt SDR-Format an → System tonemappt HDR, keine Überbelichtung. D3D11-Device mit WARP-Fallback (RDP/VM). |
| `AudioLoopbackSource` | `src/ERCTelemetry.App/Clips/AudioLoopbackSource.cs` | WASAPI-Loopback (NAudio 3.1 `WasapiRecorderBuilder`), speist rolling `AudioRingBuffer`. |
| `RollingFrameStore` | `src/ERCTelemetry.Core/Clips/RollingFrameStore.cs` | **Disk-Puffer** statt RAM: JPEG-Frames in `seg-{UtcTicks}.bin`-Segmenten (~1s), nur letztes Fenster bleibt. |
| `ScreenCaptureService` | `src/ERCTelemetry.App/Clips/ScreenCaptureService.cs` | Orchestriert Quellen + Store, throttelt auf FPS, muxed Kollisionsfenster via ffmpeg → H.264/AAC-MP4. |
| `FfmpegCommand.BuildWithAudio` | `src/ERCTelemetry.Core/Clips/FfmpegCommand.cs` | ffmpeg-Args: JPEGs per stdin + WAV-Datei → MP4, `-shortest`. |
| Settings-UI | `SettingsViewModel.cs` + `MainWindow.xaml` | FPS 1–60 (Standard 60) + Audio-Gerät-Dropdown. |

## Verifiziert

- `dotnet build` sauber (0 Warnungen/0 Fehler), **848 Tests grün** (785 Core + 63 ShareServer).
- **End-to-End-Encode-Tests gegen das echte `ffmpeg.exe`** (`tests/.../Clips/EncodePipelineTests.cs`,
  läuft gegen die gebündelten `src/ERCTelemetry.App/ffmpeg/ffmpeg.exe`, sonst still übersprungen):
  - **„Gesunder Clip":** 20-s-Window (12 s Pre-Roll + 8 s Post-Roll), Frames mit realen
    Captured-Timestamps inkl. simulierter Drops (~30 statt 60 FPS) + 440-Hz-WAV → MP4 via den
    echten `SaveClipAsync`-Schritten. Assertions: **Dauer ≈ 20 s** (hätte der 2x-Speed-Bug die
    Frames mit 60 statt ~30 FPS enkodiert, wäre sie ~10 s — der Test schlägt dann fehl),
    `Video: h264` + `Audio: aac`, Guard nicht gefeuert.
  - **„Audio kürzer als Video":** Ring deckt nur die ersten 2 s des Fensters (Kollision kurz
    nach Sessionstart) → ffmpeg beendet die Mux mit ‑shortest, der C#-Write-Loop trifft die
    tote Pipe → der Guard erkennt den **regulären Early-Exit** und BEHÄLT die valide MP4
    (~2 s) statt sie zu löschen.
- **App-Start-Smoke-Test:** gestartetes `ERCTelemetry.exe` lief stabil, `error.log` wuchs nur um
  die erwartbare Twitch-Token-Ablehnung (kein Capture-Fehler), sauber beendet.

## Zwei echte Bugs, die die Tests gefunden haben (gefixt)

plus der Speed-Bug aus dem Live-Test (2026-09-11, siehe oben)

1. **`TakeSince` las das offene Segment nicht** — das letzte ~1s Video (inkl. Kollisionsmoment)
   hätte in *jedem* Clip gefehlt. Fix: aktuelles Segment wird mitgelesen.
2. **`FileShare.ReadWrite`** nötig beim Lesen des aktuellen Segments (Writer hält `FileAccess.Write`
   offen) — `FileShare.Read` warf IOException.

## Was noch offen ist / ggf. nachgearbeitet werden muss

### 1. Live-Test im echten Rennen (wichtigster Punkt, jetzt mit E2E-untermauertem Encode-Pfad)

Voraussetzungen:
- **Netzwerk-Session nötig:** Aufnahme läuft nur bei `IsNetworkGame`-Sessions (Online-Rennen). Ein
  Solo-/Lokaltest fängt mangels Session-Status nichts auf — der Live-Test braucht ein echtes
  Online-Rennen.
- **`OnlyPlayerCollisions` beachten:** wenn aktiv, entstehen nur bei eigener/ausgewählter Kollision
  Clips; für Testzwecke ggf. deaktivieren, damit jede Kollision einen Clip erzeugt.
- **Exclusive Fullscreen:** in diesem Modus liefert WGC auf manchen GPUs kein Bild. Für den ersten
  Live-Test **Borderless** wählen; wenn es im echten Fullscreen leer bleibt, ist es diese
  bekannte Einschränkung, kein Pipeline-Bug.
- Firewall-UAC-Prompt beim ersten Start: einmalig, erlaubn.

Schritte:
- App starten → Settings → Kollisions-Clips → FPS 60, Audio-Gerät wählen → speichern.
- Erwartet vor dem Test: im `%LOCALAPPDATA%\ERCTelemetry\clips\_buffer` liegen Segment-Dateien
  (Aufnahme läuft nur bei aktiver Netzwerk-Session).
- Kollision provozieren → ~Pre+Post-Roll abwarten → Clip im Clips-Ordner prüfen.

Prüfpunkte im Clip (in dieser Reihenfolge):
1. **Dauer:** Pre-Roll + Post-Roll (±~5 %) — NICHT halb/doppelt so lang.
2. **Geschwindigkeit:** normal (kein Zeitraffer).
3. **Ton:** synchron zur Spur, lautstärke passt.
4. **HDR:** Highlights nicht überstrahlt, Farben wie im Spiel.
5. Datei spielt im Browser (faststart) und im Windows-Player (h264/aac).
- Fehler landen im Debug-Tab / `error.log` (App-Log).

Sollte der Clip doch fehlschlagen: `error.log` prüfen — „Clip encode failed" mit EPIPE/IOException
ist der (jetzt gefixte) ‑shortest-Early-Exit-Pfad; „Screen capture failed" eine Capture-Serie
(jetzt mit Auto-Restart nach 3 Frame-Fehlern).

### 2. Bekannte Schwachstellen / mögliche Nacharbeit
- ~~**Frame-Pool mit 1 Buffer**~~ — **gefixt (2026-09-11):** der Pool hat jetzt 2 Buffers, und
  der JPEG-Encode passiert nicht mehr, solange der Pool-Frame gehalten wird — der Frame wird
  direkt nach `CreateCopyFromSurfaceAsync` freigegeben und erst danach enkodiert (parallel;
  `EncoderParameters` darum pro Aufruf). Das 1-Buffer-Encode-Blocking war zugleich die Ursache
  für „Clip läuft doppelt so schnell" (echte Framerate < eingestellte FPS, siehe oben).
- ~~**Device-Lost / RDP-Disconnect** — **gefixt (2026-09-16):** WGC wirft nach einem Fehler
  (GPU-TDR, RDP-Disconnect, Monitor-Wechsel) für *jeden* Frame; die Aufnahme blieb sonst für
  die ganze Session stumm. Jetzt stoppt `HdrFrameSource` nach 3 aufeinanderfolgenden Frame-Fehlern
  und der nächste ManageLoop-Tick (500 ms) erstellt Pool + Device idempotent neu.~~
- ~~**Auflösungswechsel mitten in der Session** — **gefixt (2026-09-16):** der Frame-Pool hätte
  skaliert/gepaddete Frames weiter geliefert und ein Clip über die Grenze zwei Größen gemischt
  (ffmpeg-Mux-Fehler). `ScreenCaptureService` pollt die Item-Breite (500 ms) und startet bei
  Änderung die Capture neu + leert den Store. (Kein `SizeChanged`-Event in der .NET-Projection;
  darum Polling.)~~
- ~~**ffmpeg-Early-Exit durch ‑shortest** — **gefixt (2026-09-16):** endet die Audio-Spur vor dem
  Video (Ring-Lücke beim Sessionstart), schließt ffmpeg stdin früh → der Write-Loop traf eine
  tote Pipe, der äußere catch löschte den eigentlich gültigen Output und blockierte bis zum
  5-min-EncodeTimeout. `FfmpegCommand.IsRegularEarlyExitAsync` erkennt den regulären Abschluss
  (Exit 0 + nicht-leere Datei + 3-s-Grace) und behält den Clip (E2E-getestet).~~
- ~~**Kill/Delete-Race:** ffmpeg hielt nach timeout/cancel die Output-Datei offen → `TryDelete`
  scheiterte und ließ eine verwaiste Partial-MP4 zurück. `KillProcessTreeAndWaitForExit` killt
  jetzt die Prozess-Bäume und wartet (bounded 2 s), bevor gelöscht wird.~~
- **Audio-Ring-Neustart bei Settings-Änderung mitten in der Session**: wenn das Fenster
  (Pre+Post-Roll) größer wird als der Ring, wird `AudioLoopbackSource` neu gestartet → Puffer
  verliert Audio. Nur bei Fenster-Vergrößerung, selten — aber bewusst.
- **`EnumerateDeviceNames`** wird nur beim Settings-ViewModel-Aufbau gelistet — ein neu
  angeschlossenes USB-Headset erscheint erst nach App-Neustart.
- **`_buffer`-Ordner** (`%LOCALAPPDATA%\ERCTelemetry\clips\_buffer`): `CleanupOldClips` löscht
  nur `*.mp4` — nach einem Absturz bleiben Segment-Dateien liegen, bis der nächste
  `RollingFrameStore`-Konstruktor sie aufräumt. (Klein, aber könnte beim Start bereinigt werden.)
- **Display-Rate < FPS**: bei einem 30-Hz-Display kommen physikalisch max. 30 Frames/s an, egal
  was eingestellt ist. Kein Bug, nur eine Grenze.

### 3. Release-Vorbereitung ✅ (veröffentlicht als 0.6.1, 2026-09-07)
- Version in `ERCTelemetry.App.csproj` auf 0.6.1 erhöht, `UPDATELOG.md`-Eintrag geschrieben,
  `installer\build-installer.ps1` ausgeführt → Upload nach `T:\downloads` verifiziert.
- **Wichtig:** Der Release erfolgte auf Nutzer-Entscheidung, obwohl der Live-Test noch aussteht.
  Falls der Live-Test Fehler zeigt, in 0.6.2 fixen (nicht die Version senken).

## Geänderte/neue Dateien (Überblick)

- Neu: `src/ERCTelemetry.App/Clips/HdrFrameSource.cs`, `AudioLoopbackSource.cs`,
  `src/ERCTelemetry.Core/Clips/RollingFrameStore.cs`, `AudioRingBuffer.cs` (Properties ergänzt),
  `tests/ERCTelemetry.Core.Tests/Clips/RollingFrameStoreTests.cs`
- Neu (2026-09-16): `tests/ERCTelemetry.Core.Tests/Clips/EncodePipelineTests.cs` — E2E-Encode
  gegen das echte `ffmpeg.exe` (normale Geschwindigkeit + ‑shortest-Early-Exit).
- Rewrite: `src/ERCTelemetry.App/Clips/ScreenCaptureService.cs`
- Geändert (2026-09-16): `src/ERCTelemetry.Core/Clips/FfmpegCommand.cs` (Early-Exit-Guard),
  `HdrFrameSource.cs` (Auto-Restart nach Frame-Fehler-Serie),
  `ERCTelemetry.App.csproj` (TargetPlatformIdentifier/Version für Windows-SDK-Projections),
  `SettingsViewModel.cs`, `MainWindow.xaml`, `docs/HANDOFF.md`
- Gelöscht: `FrameRingBuffer.cs` + `FrameRingBufferTests.cs` (toter Code, durch RollingFrameStore ersetzt)
