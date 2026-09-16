# Bug-Fix-Liste (Stand 2026-09-15)

Ergebnis des 4-Agenten-Bug-Hunts vom 2026-09-15. Alle Funde unten sind **am Code verifiziert**.

**Vorgehen beim Fixen:** Nach jeder Änderung `dotnet build` und `dotnet test` (Stand: 754 Tests) grün halten. Keine Refactors nebenher — nur die Bugs. Vor einem Release `docs/RELEASE.md` lesen.

**Status der 4 Agenten:**
- ✅ Clips/Share/Update (Dateien+HTTP) — fertig + verifiziert → unten
- ✅ Core Telemetrie/Session/Analyse — fertig + verifiziert → unten
- ✅ App Composition/Concurrency/UI — Re-Hunt 2026-09-16 abgeschlossen → `docs/BUGFIXES-App.md`
- ✅ ShareServer/OAuth/Security — Re-Hunt 2026-09-16 abgeschlossen → `docs/BUGFIXES-Share.md`

**Fix-Stand 2026-09-16:** Alle Bugs der beiden verifizierten Sektionen unten sind **angewendet und grün**
(`dotnet build` 0 Warnungen/0 Fehler, `dotnet test` 757 = 754 Baseline + 3 neue Regressionstests).
Dazu drei Abschluss-Fixes aus dem Code-Review (Kill-vor-Delete im ffmpeg-Pfad, Overtake-Dedup innerhalb
der Events-Tabelle, Session-Cleanup auch bei HttpClient-Timeout).

**Fix-Stand 2026-09-16 (abends, vollständig):** Auch die Re-Hunt-Funde beider Unterlagen sind jetzt
**angewendet und grün** — `dotnet build` 0 Warnungen/0 Fehler, `dotnet test` **791** (46 ShareServer,
745 Core):
- `docs/BUGFIXES-App.md`: **alle 6 Funde FIXT** (Fix-Status-Tabelle oben in der Datei).
- `docs/BUGFIXES-Share.md`: HIGH 1/2/3, MEDIUM 1/2, MEDIUM 3 (Minimal-Pfad), LOW 1 (serverseitig),
  LOW 2/3 **FIXT** (Fix-Status-Tabelle oben in der Datei — inkl. der zuvor gemeldeten `X-Login-Nonce`-
  Bindung, Only-once-Token-Delivery, 5-min-TTL und des Parallel-Callback-Guards).

**Bewusst offen / extern (nicht zurückgenommen):**
- **Discord-Client-Secret-Rotation** (HIGH 1-Teil) — vom Auftraggeber ausdrücklich gestrichen:
  „müssen wir nicht machen". Die Secret-Auslagerung aus Quellcode/Build ist erfolgt und erfolgt über
  Env-Var `Share__DiscordClientSecret`.
- **Cloudflare-Edge-Konfiguration** (LOW 1-Teil: HSTS/Redirect, direkte Port-Exposition) — Operator-Aufgabe.
- **OAuth-Refresh-/Revoke-Endpoint** (MEDIUM 3-Teil) — separates Feature, hier nur die geforderte
  „Token abgelaufen → neu einloggen"-Meldung umgesetzt.

---

## Clips-Pipeline (verifiziert)

### HIGH — `src/ERCTelemetry.App/Clips/ScreenCaptureService.cs:110` — ffmpeg-Pfade werden unquotiert übergeben
`new ProcessStartInfo(ffmpegPath, string.Join(" ", args))` — `outputPath` (`%LOCALAPPDATA%`) und `audioPath` (`%TEMP%`) enthalten bei Benutzernamen mit Leerzeichen (z. B. „Max Mustermann“) Leerzeichen → ffmpeg bekommt abgeschnittene/garbage Pfade → jeder Clip-Encode schlägt fehl.

**Fix:** `ProcessStartInfo.ArgumentList` verwenden (jedes `args`-Element via `startInfo.ArgumentList.Add`), statt `string.Join(" ", args)`. `FfmpegCommand.Build`/`BuildWithAudio` (Zeilen 33–70) können `string[]` bleiben.

### HIGH — `src/ERCTelemetry.App/Clips/ScreenCaptureService.cs:118-147` — ffmpeg wird nie gekillt / kein Timeout
Bei Abbruch (`ct`) wirft `WaitForExitAsync(ct)` nur — der beendete ffmpeg-Prozess bleibt am Leben, hält die Ausgabedatei offen (`TryDelete` schlägt fehl), schreibt eine MP4 **ohne `ClipSaved`-DB-Eintrag**. Kein Encode-Timeout: hängt ffmpeg (korruptes Frame, D3D/Encode-Stall), bleibt `_saveGate` (1/1) in `ClipRecorderService` für immer belegt → ab dann wird **nie wieder** ein Clip erzeugt.

**Fix:** In `catch`/`finally`: `if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch { } }`; Encode-Zeitfenster (z. B. 5 min) mit `CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts)` + Kill bei Ablauf.

### MEDIUM — `src/ERCTelemetry.Core/Clips/RollingFrameStore.cs:45-63` — Disk-Fehler lässt `_current` offen und den Puffer stehen
Wirft `WriteFrame` (Disk voll), wird `_currentFrames++` nie erreicht → `CloseCurrent()` nie aufgerufen, jede weitere `Add` wirft gegen denselben kaputten `FileStream` → Handle-Leak, Capture-Loop droppt für den Rest der Session.

**Fix:** `WriteFrame` in try/catch; bei Fehler `_current.Dispose(); _current = null;` damit der nächste `Add` ein frisches Segment öffnet.

### MEDIUM — `src/ERCTelemetry.App/Clips/AudioLoopbackSource.cs:130-136` — `ConvertToFloat` interpretiert Loopback hart als float32
`sampleCount = buffer.Length / sizeof(float)` + roher MemoryMarshal-Copy ignoriert `_capture.WaveFormat.BitsPerSample/Encoding`. Ist in Windows als Geräteformat 16-Bit-PCM eingestellt, ist der Clip-Ton kompletter Rauschen.

**Fix:** `WaveFormat`-Encoding prüfen: float32 → direkt kopieren; PCM16 → int16 nach float konvertieren (dividieren durch 32768). (Alternativ explizites float-Format beim Recorder anfordern.)

### MEDIUM — `src/ERCTelemetry.App/Share/ShareService.cs:106-109` — abgebrochenes Teilen hinterlässt halbe öffentliche Session
`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` läuft an `DeleteSessionAsync` vorbei — der Doc-Kommentar (Z. 39–40) verspricht „no half-uploaded session stays public“, aber ein User-Cancel während eines großen Clip-Uploads lässt die leere/teilweise Session `/s/{uid}` bis zum Retention-Sweep stehen.

**Fix:** Im OCE-Catch best-effort `await DeleteSessionAsync(baseUrl, manifest.SessionUid, token, CancellationToken.None)` (nur wenn Session bereits angelegt), dann rethrow.

### LOW — `src/ERCTelemetry.App/Update/UpdateService.cs:149` — Delta-Download-URL nicht percent-encoded
`var url = $"{baseUrl}/{entry.Path.Replace('\\', '/')}";` — Sonderzeichen (`#`, Leerzeichen, Umlaute) im Manifest-Pfad → kaputte URL oder falsche Ressource → Hash-Mismatch → Fallback auf Vollinstaller.

**Fix:** `Uri.EscapeDataString` pro Pfadsegment (nicht über die Slashes).

### LOW — `src/ERCTelemetry.App/Clips/AudioLoopbackSource.cs:145-153` — `ResolveDevice` leakt `MMDevice`-COM-Refs
`EnumerateAudioEndPoints` gibt alle Device-Objekte zurück; nur der gematchte wird in `Stop` disposet, die übrigen nie (auch `EnumerateDeviceNames` Z. 159–166 disposet die Devices nicht). Com-Leak pro Start/Stop-Zyklus.

**Fix:** In der Schleife alle nicht-ausgegebenen Devices nach der Prüfung `device.Dispose()` aufrufen; in `EnumerateDeviceNames` alle nach dem `Select` disposen.

---

## Core Telemetrie/Session/Analyse (verifiziert)

### HIGH — `src/ERCTelemetry.Core/Analysis/RaceReportBuilder.cs:98` — `KeyNotFoundException` crasht den kompletten Rennreport
`allCars = byCar.Keys.Union(resultsByCar.Keys)` enthält auch Autos ohne Lap-Zeilen (DNS, DQ, …). Zeile 53 schützt korrekt mit `GetValueOrDefault`, aber Z. 98 macht `byCar[s.CarIndex]` direkt → Report-Tab **und** HTML-Export crashen.

**Fix:** `ToLapPoints(byCar.GetValueOrDefault(s.CarIndex) ?? [])`.

### MEDIUM — `src/ERCTelemetry.Core/Analysis/RivalDigestBuilder.cs:45-55` — Rival-State wird nicht beim Rivalen-Wechsel neu geseedet
Überholt der Spieler den Vordermann, wechselt der Rivale (Position−1 → neuer Fahrer). `_lastPitStops`/`_lastCompound`/`_lastBestLapMs`/`_lapTimes` stammen noch vom alten Rivalen → sofortiger **falscher** `TyreChange`-/`PitStop`-Alert wenn der neue Rivale andere Werte hat, dazu verfälschter Tempo-Trend; bei P5↔P4-Jitter Alerts-Spam.

**Fix:** Rivalen-CarIndex merken; bei Wechsel `_lapTimes.Clear()`, `_lastPitStops/_lastCompound/_lastBestLapMs/_lastRivalLap/_cooldown` vom neuen Rivalen übernehmen.

### MEDIUM — `src/ERCTelemetry.Core/Analysis/RaceReportBuilder.cs:232-233` — Best-Lap-Zeit wird als Lap-Nummer genutzt
`BestLapOf` liefert die Zeit in ms (Z. 165–166). `(byte)bestA` (z. B. 90.000 → 144) wird als Lap-Nummer an `GetLapTrace` gegeben → existiert nie → Fahrer-Duell-Vergleich lädt Traces **immer** null, Feature arbeitet stillschweigend nie.

**Fix:** Lap-**Nummer** des schnellsten Laps aus `lapsA`/`lapsB` ermitteln (kleinste `LapTimeMs>0` → deren `LapNumber`), nicht die Zeit casten.

### LOW — `src/ERCTelemetry.Core/Analysis/RaceSummaryBuilder.cs:132-135` — Überhol-Sätze parsen nur englische Store-Texte
`t.Text` im `"Overtake"`-Fall wird nur für das englische Format `"X passed Y"` geparst. Der Store erzeugt aber deutsche Texte (`SessionStateStore.cs:933`: `"OVERHAUL! P3 — du hast Y überholt"`), die in die Timeline übernommen werden (RaceReportBuilder.cs:195) → `IndexOf(" passed ")` = −1 → garbled Text (Z. 132) bzw. potenziell `ArgumentOutOfRangeException` (Z. 135 `[..-1]`). Zusätzlich landen Overtakes doppelt in der Timeline (deutsch aus `GetEvents`, englisch aus `GetOvertakes`).

**Fix:** Entweder Timeline-Texte vereinheitlichen oder in `PlayerEvents` auf das Vorhandensein von `" passed "` verzweigen und beide Formate unterstützen; Doppel-Quelle der Overtake-Einträge bereinigen.

### LOW — `src/ERCTelemetry.Core/Tracks/TrackFitter.cs:339-340` — ICP-Komposition nutzt überschriebenes `ftx`
`ftx = …` (Z. 339) überschreibt `ftx`; die Folgezeile `ftz = dtz + dscale·(dsin·ftx + dcos·ftz)` rechnet mit dem **neuen** statt alten `ftx` → Kreuzkopplung ∆·dscale·sin(angle), akkumuliert über bis zu 75 ICP-Iterationen, verfälscht den Fit/Qualitätsgate.

**Fix:** Alte `ftx`/`ftz` in Locals vorhalten und beide Zeilen mit dem alten Vektor berechnen (`var oldFtx = ftx; var oldFtz = ftz; ftx = … oldFtx … oldFtz; ftz = … oldFtx … oldFtz;` analog zur korrekten `fcos`/`fsin`-Zeile).

---

## Sauber geprüft (keine Bugs — nicht nochmal untersuchen)

- **Clips/File+HTTP:** Chunked-Upload-Part-Mathe (off-by-one ok), HTTP-Lebenszyklen (alles `using`), Update-Vollinstaller (SHA-256, atomarer Move, Traversal-Guards), `HdrFrameSource`-COM-Refcounting, `AudioRingBuffer`-Slice-Mathe.
- **Core:** Telemetrie (UDP/Packet), Session-State (Dedup/Flashback/Fuel+ERS), Standings, EventFeed, Fuel/Pace/Strategy, LapTrace, PitWindow, DriverRating, Championship, TTS-Voice-Kern, Persistence-SQL (überall benannte Parameter) & Connection-Disposal.
- **Hinweis:** Ergebnisse des App- (Bug-Hunt ⏳) und ShareServer-Agents (⏳) sind nur in den `docs/BUGFIXES-App.md` / `docs/BUGFIXES-Share.md` vorhanden, falls die Agents ihre Dateien vor dem Shutdown noch schreiben konnten — sonst müssen diese Bereiche (Channels/Concurrency/Dispose-Reihenfolge, ShareServer-OAuth/Security) morgen kurz nachgeprüft werden.
