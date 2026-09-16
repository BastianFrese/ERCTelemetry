# Plan: Custom Installer/Uninstaller + Discord-artiges Update-Lade-Fenster

> Implementierungsplan für die nächste Runde. Ziel: Update-Ablauf wie bei Discord/Steam —
> ein **sichtbares Lade-Fenster, während das Update installiert wird**, plus custom
> Installer- und Uninstaller-Fenster statt des Inno-Wizards. Stand: 2026-09-08,
> `dotnet build`/`dotnet test` grün (667 + 13).

## Kontext — was schon da ist (nicht neu bauen!)

- **Delta-Updates** (seit 0.4.2.1): App lädt über `ERCTelemetry.files.json` nur geänderte
  Dateien in ein Staging-Verzeichnis, schreibt `apply.json`, kopiert `apply-update.ps1`
  nach `%LOCALAPPDATA%\ERCTelemetry\update\` und startet es beim Beenden.
- **`apply-update.ps1`** (PowerShell, `-WindowStyle Hidden`): wartet auf App-Exit, kopiert
  gestagte Dateien über das Install-Verzeichnis, löscht veraltete (Safety-Liste), startet
  die App neu, schreibt `apply-result.json`. **Läuft unsichtbar — kein Feedback.**
- **Inno Setup** (`installer/ERCTelemetry.iss` + `build-installer.ps1` mit ISCC): baut die
  Setup.exe (95 MB, solid LZMA2), installiert nach `%LOCALAPPDATA%\Programs\ERCTelemetry`
  (kein UAC), Shortcuts, Registry-Uninstaller.
- **App-Runtime:** self-contained (gebündeltes .NET 10) im Install-Verzeichnis.

## Lehren aus dem zurückgerollten Versuch (2026-09-08)

Der erste Anlauf (WPF-Updater, DeltaApplier, ArchiveBuilder, Setup.exe = self-contained
Updater) wurde komplett zurückgerollt. Drei harte Fakten:

1. **Setup.exe wurde 277 MB statt ~120 MB.** Ursache: die self-contained Updater-Runtime
   (~171 MB) lag **unkomprimiert** im Single-File-Bundle, plus app-files.zip (119 MB).
   - `EnableCompressionInSingleFile=true` → Runtime ~70 MB → Setup.exe ~190 MB.
   - Solid-7z (7z.exe/SevenZipSharp im Build) → app-files.zip ~90 MB → Setup.exe ~160 MB.
   - AOT-Bootstrap (Setup.exe = nativer Stub, keine doppelte Runtime) → ~100–130 MB.
2. **SharpCompress kann kein solid 7z erzeugen** (nur per-file LZMA im Zip). Lesen kann es.
3. **Scope zu groß** — 5 Phasen, ~30 Dateien auf einmal. Diesmal schlanker, Kern zuerst.

## Architektur

**Eine kleine WPF-App `src/ERCTelemetry.Updater`**, drei Modi per Kommandozeilen-Argument:

| Modus | Argument | Fenster |
|---|---|---|
| Update anwenden | `--apply-update` | **Lade-Fenster wie Discord**: Fortschrittsbalken + Status („Datei 12/34 wird kopiert…") + Version „0.6.2.135 → 0.6.3" |
| Installieren | `--install <sourceDir>` | **Custom Installer**: Willkommen (Desktop-Verknüpfung opt-in) → Fortschritt → Fertig |
| Deinstallieren | `--uninstall` | **Custom Uninstaller**: Bestätigen („Einstellungen/Verlauf bleiben") → Fortschritt → Fertig |

**Entkopplung:** Das Lade-Fenster (`--apply-update`) braucht **keine** Setup.exe-Änderung —
es läuft mit der bestehenden Inno-Installation. Nur der Custom-Installer erfordert, dass die
Setup.exe zum self-contained Updater wird (Größen-Tradeoff). Der Uninstaller läuft wieder
framework-dependent.

## ⚠️ Zuerst klären: Runtime für den Updater (DOTNET_ROOT)

Die App ist self-contained → **flaches Runtime-Layout** (coreclr.dll etc. im Install-Root,
**kein** `shared\Microsoft.NETCore.App\`-Unterordner). Ein framework-dependent gestarteter
Updater findet die Runtime über `DOTNET_ROOT=<installDir>` **nur**, wenn die Runtime im
Standard-Layout liegt. **Muss morgen empirisch getestet werden** (Updater aus dem Update-Root
starten, `DOTNET_ROOT` auf das Install-Verzeichnis zeigen).

- **Wenn es funktioniert:** Updater bleibt framework-dependent (~1 MB im Install-Verzeichnis).
- **Wenn nicht:** Updater als self-contained single-file (~70 MB, `EnableCompressionInSingleFile=true`)
  im Install-Verzeichnis — läuft ohne DOTNET_ROOT, kostet aber ~70 MB Install-Größe.

## Phasen

### Phase 1 — Lade-Fenster beim Delta-Update (Kern-Wunsch, kein Größen-Effekt)
- `src/ERCTelemetry.Updater` (WPF): `--apply-update`-Modus mit Discord-artigem Lade-Fenster.
- `src/ERCTelemetry.Core/Update/DeltaApplier.cs` — Port von `apply-update.ps1` nach C#
  (testbar): wartet auf App-Exit (bis 60 s), kopiert gestagte Dateien (per-Datei-Fortschritt),
  löscht veraltete (Safety-Liste), startet App neu, schreibt `apply-result.json`.
- `src/ERCTelemetry.Core/Update/FileOps.cs` — `CopyDirectory`/`DeleteDirectory` mit Fortschritt.
- `UpdateService.cs`: `CopyApplyScript()` → `CopyUpdater()` (kopiert die 6 Updater-Dateien),
  `StartDeltaUpdater`/`LaunchDeltaUpdater` starten `ERCTelemetry.Updater.exe --apply-update`.
- `apply-update.ps1` löschen, `<Content Include>` aus dem csproj.
- Tests: `DeltaApplierTests` + `FileOpsTests`.

### Phase 2 — Custom Installer
- `--install <sourceDir>`-Modus: Willkommen → Fortschritt → Fertig.
- `InstallerService` (Dateien kopieren), `ShortcutService` (Startmenü + Desktop, COM),
  `RegistryService` (HKCU-Uninstall-Eintrag).
- Setup.exe = self-contained single-file Updater mit eingebettetem app-files.zip
  (LZMA via `tools/ArchiveBuilder`), **`EnableCompressionInSingleFile=true`**.
- `build-installer.ps1` umschreiben (Inno raus), `ERCTelemetry.iss` löschen.

### Phase 3 — Custom Uninstaller
- `--uninstall`-Modus: Bestätigen → Fortschritt → Fertig.
- Self-Delete-Pattern (Temp-Kopie, `cmd /c del`), Registry + Shortcuts entfernen,
  `%LOCALAPPDATA%\ERCTelemetry` (Einstellungen/Verlauf) bleibt.

### Phase 4 — Release + Doku
- `docs/RELEASE.md`, `docs/HANDOFF.md`, `UPDATELOG.md` aktualisieren.
- Release bauen + publizieren (Gate: 0 Warnungen, Tests grün).

## Offene Entscheidungen (für morgen)

1. **Setup.exe-Größe:** A (~190 MB, Compression-Fix) als Start ok? Oder direkt B (solid 7z,
   ~160 MB) / C (AOT-Bootstrap, ~130 MB)?
2. **Updater-Runtime:** DOTNET_ROOT-Test entscheidet (FD ~1 MB vs. self-contained ~70 MB).
3. **Inno komplett ersetzen?** Ja — Setup.exe wird der Updater (wie besprochen).

## Verifikation

1. `dotnet build` + `dotnet test` grün.
2. **Update-Test:** Delta stagen (Test-Server) → App beenden → Lade-Fenster erscheint →
   Dateien ersetzt → App startet neu. `apply-result.json` = ok.
3. **Install-Test:** Setup.exe auf frischem Pfad → custom Installer-Fenster → Dateien in
   `%LOCALAPPDATA%\Programs\ERCTelemetry`, Shortcuts, Registry, App startet.
4. **Uninstall-Test:** Registry-Eintrag → custom Uninstaller-Fenster → Dateien/Shortcuts/
   Registry entfernt, `%LOCALAPPDATA%\ERCTelemetry` bleibt.
