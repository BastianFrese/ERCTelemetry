# Bug-Fix-Liste App — Composition / Concurrency / UI-Thread (Stand 2026-09-16)

Neu-Jagd des Bereichs, dessen Agent beim Bug-Hunt am 2026-09-15 unterbrochen wurde (siehe
`docs/BUGFIXES.md` Zeilen 10 + 87). Alle Funde sind am aktuellen Code verifiziert und
zitieren die fehlerhafte Zeile. Die in `docs/BUGFIXES.md` gemeldeten High/Medium-Funde
(ffmpeg-Pfad-Quoting, ffmpeg-Kill/Timeout, RollingFrameStore-Disk-Fehler,
AudioLoopbackSource-Format/Device-Leak, UpdateService-URL-Escaping, ShareService-OCE-Cleanup)
sind im aktuellen Code bereits FIXT — sie stehen weiter unten unter „Sauber geprüft".
Seit 2026-09-16 sind auch alle Funde dieser Datei behoben — siehe Fix-Status-Tabelle.

---

## Fix-Status (2026-09-16)

| # | Fund | Status |
|---|------|--------|
| MEDIUM | `DiscordLoginHost.cs:117-118` — Login-Events vom Background-Thread mutieren gebundene Collections | **FIXT** — `DiscordLoginHost.cs:165-176`: `StatusChanged`/`UserChanged` werden über `Application.Current?.Dispatcher` auf den UI-Thread marshalled. |
| MEDIUM | `MainWindow.xaml.cs:1013` — `LlmTest_Click` ohne try/catch; OllamaClient fängt Timeout nicht | **FIXT** — `OllamaClient.cs:72/77/80`: `OperationCanceledException` nur durchgereicht, wenn `ct.IsCancellationRequested`; `TaskCanceledException` (HttpClient-Timeout) fällt in den generischen Catch. |
| MEDIUM | `OverlayWebHost.cs:496-539` — LLM-Commentary-Loop stirbt still bei Timeout-OCE | **FIXT** — Innerer Catch `catch (OperationCanceledException) when (ct.IsCancellationRequested)` (Z. 524) + Kommentar (Z. 531); ein Timeout überspringt den Tick statt die Loop zu beenden. |
| LOW | `MemoryProbe.cs:25-52` — TEMP-Hook mit Endlos-Loop + unbegrenzter memory.log | **FIXT** — Selbstbegrenzt (maximale Zeilenzahl, `MemoryProbe.cs:14`) + `CancellationToken` vom DebugViewModel (`DebugViewModel.cs:58`). |
| LOW | `VoiceAlertService.cs:509-511` — Release nach Dispose des SemaphoreSlim | **FIXT** — `_llmGate` wird nicht mehr disposet (app-Lifetime-Semaphore, GC räumt auf); `Release()` im `finally` kann nicht mehr auf ein disposetes Gate treffen. |
| LOW | `TwitchChatHost.cs:146` — `_latest` ohne Lock + `TwitchLoginClient`/`DiscordLoginClient` nie disposet | **FIXT** — Beide Login-Clients sind `IDisposable` und werden mit `using var …` pro Attempt disposet (`TwitchChatHost.cs:83/220`, `DiscordLoginHost` analog). `_latest` bleibt bewusst referenz-atomic (Doku: benigne, kein Crash-Risiko). |

Verifiziert: Build (0 Warnungen/0 Fehler) + 745 Core-Tests am 2026-09-16.

---

## MEDIUM

### MEDIUM — `src/ERCTelemetry.App/Composition/DiscordLoginHost.cs:117-118` — Login-Events feuern vom Background-Thread → `SetupsViewModel` mutiert gebundene ObservableCollections

StartLoginAsync läuft nach `await … .ConfigureAwait(false)` (Z. 116) auf einem
Thread-Pool-Thread weiter und ruft dort `StatusChanged?.Invoke(); UserChanged?.Invoke();`
(Z. 117–118) synchron auf. Der Subscriber `SetupsViewModel` ist an beide Events gebunden
(`SetupsViewModel.cs:34-35`) und macht in `RefreshLoginState()` (`SetupsViewModel.cs:164-181`)
`Tracks.Clear(); GameYears.Clear(); Setups.Clear();` (Z. 174–176) — **ObservableCollection**,
die an ComboBox/ItemsControl gebunden sind. Sobald der Setups-Tab einmal realisiert wurde
(CollectionView existiert), wirft Clear auf einem Nicht-UI-Thread `NotSupportedException`
(„CollectionView does not support changes … from a thread different from the Dispatcher
thread"). Die Exception wird von `catch (Exception ex)` in `DiscordLoginHost.cs:138`
verschluckt → StartLoginAsync gibt fälschlich `Login fehlgeschlagen: …` zurück, obwohl der
Login erfolgreich war (der Nutzer sieht nach einem erfolgreichen Discord-Login eine Fehlermeldung).

**Fix:** Die Events auf den UI-Thread marshallen, bevor Collections angefasst werden — z. B. im
Host `Application.Current.Dispatcher.Invoke(StatusChanged)` oder im Subscriber
(`SetupsViewModel.RefreshLoginState` auf den Dispatcher heben). Alternativ nur die
Scalar-Properties vom Background-Thread setzen und die Collection-Änderungen dispatchbar machen.

### MEDIUM — `src/ERCTelemetry.App/Dashboard/MainWindow.xaml.cs:1013` — `LlmTest_Click` (async void) ohne try/catch; OllamaClient fängt den Timeout nicht

`OllamaClient.CompleteAsync` schluckt nur `ex is not OperationCanceledException`
(`src/ERCTelemetry.Core/Llm/OllamaClient.cs:72`). Der `HttpClient.Timeout` von 30 s
(`OllamaClient.cs:25`) wirft bei einer hängenden Verbindung (Black-Hole, DNS-Filter) aber eine
**TaskCanceledException** — eine Unterklasse von `OperationCanceledException` → der Filter
greift NICHT, die Exception propagiert. `LlmService.TestAsync` hat keinen try/catch
(`LlmService.cs:81-95`), und `LlmTest_Click` (async void) awaited ohne try/catch
(`MainWindow.xaml.cs:1013-1016`) mit `CancellationToken.None`. Ergebnis: eine einzige
hängende Ollama-Anfrage >30 s → unbehandelte Exception auf dem Dispatcher → die WPF-App crasht
am „Verbindung testen"-Button. Alle anderen Fehler (HttpRequestException) sind korrekt
abgefangen — nur der Timeout-Pfad ist offen.

**Fix:** Den Catch-Filter in `OllamaClient` so erweitern, dass nur echte User-Cancellations
durchgereicht werden (`when (ex is not OperationCanceledException || ex is not TaskCanceledException)`),
oder in `LlmTest_Click` einen try/catch um den await legen (Status statt Crash).

### MEDIUM — `src/ERCTelemetry.App/OverlayServer/OverlayWebHost.cs:496-539` — LLM-Commentary-Loop stirbt still bei einem OCE, das kein Shutdown ist (HttpClient-Timeout)

Der innere `catch (Exception ex) when (ex is not OperationCanceledException)` (Z. 524–529)
fängt die LLM-Call-Fehler. Wirft der Ollama-Aufruf (HttpClient-Timeout → TaskCanceledException,
durch den `OllamaClient`-Filter ungefangen) eine OperationCanceledException, wird sie weder hier
noch im `while` abgefangen, sondern läuft in den äußeren `catch (OperationCanceledException)`
(Z. 536–538), der für den Shutdown gedacht ist. `ct` war dabei NICHT gecancelt → die 45-s-Loop
`LlmCommentaryLoopAsync` beendet sich dauerhaft, der Layer-2-Kommentator schweigt für den Rest
der Session, ohne dass der Fehler sichtbar wäre.

**Fix:** Nur echte Shutdown-OCEs durchreichen; im inneren catch auch `OperationCanceledException`
abfangen, wenn `ct.IsCancellationRequested == false`, und den Tick überspringen statt die Loop zu
beenden.

---

## LOW

### LOW — `src/ERCTelemetry.App/Dashboard/MemoryProbe.cs:25-52` — TEMP-Diagnose-Hook mit Endlos-Loop ohne CancellationToken und unbegrenzt wachsender memory.log

`_ = Task.Run(() => { while (true) { … File.AppendAllText(path, line); Thread.Sleep(1000); } });`
(Z. 25–51) — die Schleife beobachtet kein CancellationToken, beendet nie, hält dauerhaft einen
Thread + Process-Handle und schreibt 1 Zeile/Sekunde in `%LOCALAPPDATA%\ERCTelemetry\memory.log`
(unbegrenztes Wachstum). Klassendokument sagt selbst „TEMP: … **Nach der Messung entfernen**." — der
Hook ist aber fest im Produktcode (`DebugViewModel.StartListening` ruft ihn).

**Fix:** Ganz entfernen oder — falls tatsächlich noch Messung läuft — mit CancellationToken,
begrenztem Log (Ring/Größenlimit) und Dispose/Stop in DebugViewModel versehen.

### LOW — `src/ERCTelemetry.App/Composition/VoiceAlertService.cs:509-511` — Dispose-Race: fire-and-forget-LLM-Tasks releasen ein bereits disposetes SemaphoreSlim

`AnalyzeAndSpeakAsync`/`RivalAndSpeakAsync` sind fire-and-forget (`_ = …` an Z. 238/246) und
führen `_llmGate.Release()` im `finally` aus (Z. 326/347). `Dispose()` wartet höchstens 1 s pro
Task (Z. 465–506), cancelt dann und macht `_stop.Dispose(); _llmGate.Dispose(); _synth.Dispose();`
(Z. 508–511). Läuft ein in-flight-LLM-Aufruf länger als das 1-s-Fenster, wirft `_llmGate.Release()`
nach dem Dispose eine `ObjectDisposedException` im finally → faulted/unobserved Task. Gleiche
Struktur in `ClipRecorderService.SaveAsync` (`ClipRecorderService.cs:150-153`): nach
`_clipRecorder.Dispose()` (Z. 178 `_saveGate.Dispose()`) kann ein noch laufender Clip-Encode
(Timeout bis 5 min) im finally `_saveGate.Release()` auf das disposete SemaphoreSlim ausführen —
wieder nur unobserved, kein Crash (kein Exception-Handler im Caller).

**Fix:** Die fire-and-forget-Tasks tracken (List<Task>) und in `Dispose()` wirklich abwarten,
bevor das Gate/disponiert wird — oder das SemaphoreSlim schlicht nicht disposen (GC räumt auf;
bei einem Objekt mit app-Lifetime ist das vertretbar).

### LOW — `src/ERCTelemetry.App/Composition/TwitchChatHost.cs:146` — `_latest` read/write ohne Lock (benigne Race) + `TwitchLoginClient`/`DiscordLoginClient`-Instanzen nie disposet

`_latest = snapshot;` (Z. 146) im Background-`SnapshotLoopAsync` vs. `() => _latest`
(Z. 163, vom Chat-Service auf dessen Thread gelesen) — ohne Lock, aber Referenz-atomic und das
Snapshot-Objekt ist immutable, daher faktisch harmlos. Gleicher Typ wie
`ReplayPlayer.cs:189` (`Progress = …` ohne Lock, Referenz-atomic). Kein Crash. — Zweiter Punkt:
`TwitchChatHost.StartLoginAsync` legt pro Login ein `new TwitchLoginClient(...)` an
(`TwitchChatHost.cs:83`), das intern `new HttpClient()` erzeugt (`TwitchLoginClient.cs:40/49`)
und nie disposet (Klasse nicht IDisposable); identisch `DiscordLoginClient` pro Discord-Login
(`DiscordLoginHost.cs:88`, `DiscordLoginClient.cs:40/49`). Pro Login-Versuch bleibt ein
HttpClient/Socket-Handler bis zum GC hängen — bei häufigen Logins ein kleiner Ressourcen-Leak.

**Fix:** Login-Clients wiederverwenden oder `IDisposable` machen und pro Attempt disposen;
`_latest` ggf. unter das vorhandene `_lock` legen (Doku-Wert, kein akuter Fehler).

---

## Sauber geprüft (keine Bugs — nicht nochmal untersuchen)

- **Channels: exakt ein Consumer pro Channel.** Vollständig verifiziert anhand des Reader-
  Inventars: `Packets`←Aggregator, `Snapshots`←DashboardViewModel,
  `Events`←PersistencePump, `ClipEvents`←ClipRecorderService,
  `OverlaySnapshots/OverlayEvents`←OverlayWebHost-Pump, `OverlayWindowSnapshots/Events`←
  InGameOverlayWindow, `VoiceEvents/VoiceSnapshots/LiveAnalysisSnapshots`←VoiceAlertService,
  `ErcRaceEnded`←ErcRacePromptService, `TwitchSnapshots`←TwitchChatHost. `Packets` hat zwei
  Writer (UdpListener + ReplayPlayer), aber `SingleWriter` ist nicht gesetzt und
  `StartReplay` stoppt vorher den Listener (`AppServices.cs:347-355`) — sequenziell, kein Konflikt.
- **`ClipRecorderService._saveGate` (SemaphoreSlim 1/1): jeder Pfad released.** `finally {
  _saveGate.Release(); }` (`ClipRecorderService.cs:150-153`) deckt auch den neuen ffmpeg-
  Timeout/Kill-Pfad ab (`ScreenCaptureService.cs:136-197` — EncodeTimeout, Kill(entireProcessTree),
  ArgumentList). Die in BUGFIXES.md gemeldeten High-Bugs ffmpeg-Quoting/Timeout/Kill sind fix vert.
- **Dispose-Reihenfolge** in `MainWindow.OnClosing` (`MainWindow.xaml.cs:1321-1360`):
  Overlay→VoiceAlerts→Twitch→Discord→ErcPrompts jeweils VOR `_services.Dispose()`, dann Tray.
  `AppServices.Dispose()` (`AppServices.cs:385-439`): Stopp-Logik (Recording→Forwarder→Listener→
  Replay)→Packets.Complete→Aggregator-Wait(2s)→Clip-Recorder→Events.Complete→Persistence→DB→
  übrige Channels. Korrekt.
- **Async-void-Handler ohne Crash-Risiko:** `TwitchLogin_Click`/`ErcTest_Click`/
  `DiscordLogin_Click`/`SetupsLoad_Click` (MainWindow) awaiten nur „never-throws"-Kontrakte
  (StartLoginAsync/GetLeaguesAsync/GetOwnerAsync/GetSetupsAsync fangen intern). `ShareSession_Click`
  (SessionDetailView) awaitet `HistoryViewModel.ShareSessionAsync`, das intern alle Exceptions
  abfängt. `UpdateCheck_Click`/`UpdateInstall_Click` mit try/catch + `_updateBusy`-Reentry-Guard.
  **Ausnahme:** `LlmTest_Click` (s. MEDIUM oben).
- **UI-Thread-Affinität:** DashboardViewModel (DispatcherTimer 33 ms), InGameOverlayWindow
  (DispatcherTimer 30 fps) und DebugViewModel (1 s) drainen ihre Channels rein auf dem UI-Thread.
  `RaceReportViewModel.SummarizeAsync`/`CoachAsync` sind bewusst ohne ConfigureAwait(false)
  (Continuation auf UI-Thread für PropertyChanged; `_rebuildVersion`-Guard wirft Stale-Ergebnisse
  weg). `ErcRacePromptService.RaceEnded` wird in MainWindow via `Dispatcher.Invoke` auf den
  UI-Thread gehoben (`MainWindow.xaml.cs:112`). WPF-Scalar-Binding marshallt die
  `PropertyChanged` der Settings-Statusfelder automatisch — Twitch-Statuswechsel vom
  Background-Thread sind unkritisch.
- **HdrFrameSource.FrameCaptured**: der bekannte bewachte async-void-Pfad (try/catch in
  `OnFrameArrived`); `OnFrameCaptured` (ScreenCaptureService) schreibt unter `_gate` mit
  min-Intervall-Throttle.
- **UdpListener** (`UdpListener.cs:55-79`): Loop beobachtet ct, filtert OCE/ObjectDisposed,
  fängt auch Raw-Tap-Fehler über die Catch-Filter — überlebt jede Fehlerquelle.
- **AudioLoopbackSource**: PCM16/24/32-Konvertierung (`AudioLoopbackSource.cs:138-198`) und
  MMDevice-Dispose in `ResolveDevice`/`EnumerateDeviceNames` sind fix vert.
- **OverlayWebHost**: `_startGate` + Rollback bei Bind-Fehler/Dispose-Gewinn (`OverlayWebHost.cs:96-159`);
  `Dispose` blockiert zwar die UI kurz auf `Task.Run(StopClientsAsync).GetAwaiter().GetResult()`
  (Z. 591), aber die Arbeit läuft auf dem Thread-Pool — kein Deadlock, nur kurzzeitiger Freeze (bewusst).
- **UpdateService**: Delta-URLs percent-encoden (`UpdateService.cs:150-155`) — BUGFIXES.md-Fix vert.
- **ShareService**: OCE-Catch macht Best-Effort-`DeleteSessionAsync` vor dem rethrow
  (`ShareService.cs:113-127`) — BUGFIXES.md-Fix vert; „no half-uploaded session stays public" gilt.
- **TwitchIrcClient/Disconnect**: alle awaits mit ConfigureAwait(false) → das
  sync-over-async `GetAwaiter().GetResult()` in `TwitchChatHost.Disconnect` kann nicht deadlocken.

---

**Zusammenfassung:** 0 HIGH · 3 MEDIUM · 3 LOW (plus 1 benigne/low-Race-Notiz), verifiziert am
Stand 2026-09-16, `docs/BUGFIXES-App.md` (diese Datei). **Alle 6 Funde FIXT** (Fix-Runde
2026-09-16, siehe Fix-Status-Tabelle oben).
