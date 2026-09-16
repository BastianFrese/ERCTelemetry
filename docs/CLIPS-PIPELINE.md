# Kollisions-Clips: Video-Pipeline (HDR + Audio + Disk-Buffer)

> **Status: als 0.6.1 veröffentlicht (2026-09-07) — aber NOCH NICHT live im echten Rennen
> getestet.** Dieses Dokument ist der Arbeitsstand für die nächste Session.
> Stand: 2026-09-07 (abends).
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

- `dotnet build` sauber (0 Warnungen), **569 Tests grün** (8 neue `RollingFrameStore`-Tests).
- **ffmpeg-Smoke-Test end-to-end:** 30 synthetische JPEG-Frames + 1s-WAV → MP4 mit exakt den
  `BuildWithAudio`-Argumenten → **Video h264 30fps + Audio aac, 1.00s** ✓
- `docs/HANDOFF.md` aktualisiert (Pipeline + neue Hard-won-Facts).

## Zwei echte Bugs, die die Tests gefunden haben (gefixt)

plus der Speed-Bug aus dem Live-Test (2026-09-11, siehe oben)

1. **`TakeSince` las das offene Segment nicht** — das letzte ~1s Video (inkl. Kollisionsmoment)
   hätte in *jedem* Clip gefehlt. Fix: aktuelles Segment wird mitgelesen.
2. **`FileShare.ReadWrite`** nötig beim Lesen des aktuellen Segments (Writer hält `FileAccess.Write`
   offen) — `FileShare.Read` warf IOException.

## Was noch offen ist / ggf. nachgearbeitet werden muss

### 1. Live-Test im echten Rennen (wichtigster Punkt)
- App neu starten → Settings → Kollisions-Clips → FPS 60, Audio-Gerät wählen.
- Kollision provozieren → Clip prüfen: flüssig? Ton? HDR nicht überbelichtet? Dauer = Pre+Post-Roll?
- Fehler landen im Debug-Tab / `error.log` (App-Log).

### 2. Bekannte Schwachstellen / mögliche Nacharbeit
- ~~**Frame-Pool mit 1 Buffer**~~ — **gefixt (2026-09-11):** der Pool hat jetzt 2 Buffers, und
  der JPEG-Encode passiert nicht mehr, solange der Pool-Frame gehalten wird — der Frame wird
  direkt nach `CreateCopyFromSurfaceAsync` freigegeben und erst danach enkodiert (parallel;
  `EncoderParameters` darum pro Aufruf). Das 1-Buffer-Encode-Blocking war zugleich die Ursache
  für „Clip läuft doppelt so schnell" (echte Framerate < eingestellte FPS, siehe oben).
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
- Rewrite: `src/ERCTelemetry.App/Clips/ScreenCaptureService.cs`
- Geändert: `ERCTelemetry.App.csproj` (TargetPlatformIdentifier/Version für Windows-SDK-Projections),
  `SettingsViewModel.cs`, `MainWindow.xaml`, `docs/HANDOFF.md`
- Gelöscht: `FrameRingBuffer.cs` + `FrameRingBufferTests.cs` (toter Code, durch RollingFrameStore ersetzt)
