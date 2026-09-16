# Release-Checkliste — vor jedem Publish lesen

> Pflichtlektüre vor jedem Publish (verpflichtend über CLAUDE.md).

## 1. Versionierung

- Version steht ausschließlich in `src/ERCTelemetry.App/ERCTelemetry.App.csproj`
  (`<Version>x.y.z</Version>`); ProductVersion der exe, Setup.exe-Name, Inno-AppVersion,
  Manifest-`version` und Setup-Tab-Anzeige leiten sich daraus ab.
- **Keine Prerelease-Suffixe** (`-beta`, `-rc`) in `<Version>`: `System.Version` kann sie
  nicht parsen → Manifest ungültig → In-App-Updates tot. Beta-Kennzeichnung läuft nur
  über die Versionslinie selbst (0.x = Beta) oder den Verteilweg (
  `installer\build-installer.ps1 -Beta` lädt in einen `beta`-Unterordner).
- SemVer: **Patch** (x.y.z) = Bugfixes, **Minor** (x.y.0) = neue Features,
  **Major** (x.0.0) = Bruch (DB-Schema/Settings/Overlay-Kontrakt).
  Beta-Ära: Start 0.1.0 (2026-09-03); 0.1.x = Fixes, 0.2.0 = nächste Feature-Stufe.
- **Niemals eine Version senken** — UpdateEvaluator vergleicht strikt `>`; eine
  gesenkte Version würde installierten Nutzern kein Update anbieten, und eine
  veröffentlichte Version ist nicht mehr aus der Welt.
- Konsequenz des Neustarts auf 0.1.0: installierte 1.0.1-Instanzen bekommen 0.x nie
  als Update angeboten (1.0.1 > 0.1.0) — einmal manuell neu installieren.

## 2. Update-Log (PFLICHT)

**Jeder Release braucht einen Eintrag** in `UPDATELOG.md` (Repo-Root, Deutsch):

- Neuer Eintrag immer **oben**: `## x.y.z — <Beta|Stabil> (YYYY-MM-DD)`.
- Nutzer-Sprache (Deutsch), so dass ein Spieler versteht, was neu ist:
  neu / verbessert / Behoben / bekannte Grenzen.
- Sichtbar für den Nutzer **nach dem Update**: beim ersten Start der neuen Version zeigt
  die App den Abschnitt der eigenen Version automatisch als Dialog an (UPDATELOG.md wird
  vom Installer mit ins Install-Verzeichnis geliefert); zusätzlich jederzeit über den
  Button „Update-Log“ in der Updates-Karte (Setup-Tab) und online unter
  `https://erdi-erc.de/downloads/UPDATELOG.md`.
- Ohne UPDATELOG-Eintrag **nicht publizieren** — die Pipeline verweigert den Build.

## 3. Gate vor dem Publish

1. `dotnet build ERCTelemetry.slnx -c Release` → **0 Warnungen** (Release zählt!).
2. `dotnet test tests/ERCTelemetry.Core.Tests/ERCTelemetry.Core.Tests.csproj` → grün.
3. Version in der App-csproj erhöht + UPDATELOG-Eintrag vorhanden.
4. (Optional) Smoke: App starten, Renn-DB im Report-Tab ansehen.

## 4. Publish

Ein Befehl:

    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

publishet Release win-x64, kompiliert den Inno-Installer, erzeugt die versionierte
`ERCTelemetry-Setup-<Version>.exe` (+ `.sha256` — **der einzige Download**, ab 0.1.0 gibt
es keine stabile `ERCTelemetry-Setup.exe`-Kopie mehr; der feste Website-Link zeigt auf die
versionierte Datei), das Manifest `ERCTelemetry.version.json`
(`{ version, publishedUtc, sha256, fileName }`, fileName → versionierte Datei), das
**Delta-Manifest** `ERCTelemetry.files.json` (jede Datei mit `path`/`size`/`sha256` — die
App lädt nur geänderte Dateien) und lädt alles nach `T:\downloads`. Der Installer liefert
`UPDATELOG.md` mit ins Install-Verzeichnis (automatische Anzeige nach dem Update).

- **Post-Upload-Verifikation automatisiert:** das Skript liest das hochgeladene
  `ERCTelemetry.version.json` vom Server zurück und prüft `version` + `sha256` gegen den
  lokalen Build — schlägt fehl, bricht der Build ab (ersetzt die frühere manuelle Prüfung).
- **Alte Releases:** lokal in `installer\release\` bleiben nur die letzten 3 versionierten
  Setup-Dateien (+ `.sha256`); der Server behält seine Historie.
- **Lokaler Build ohne Server:** `installer\build-installer.ps1 -SkipUpload` erzeugt alles
  in `installer\release\` und überspringt Upload + Verifikation.
- Beta-Kanal: `installer\build-installer.ps1 -Beta` → Upload nach `T:\downloads\beta`;
  Main-Kanal bleibt unberührt.
- SmartScreen: nicht signiert → Nutzer wählen beim ersten Download „Behalten”.

## 5. Nach dem Publish

- HANDOFF.md-Sprint-Eintrag (Datum, Version, wesentliche Änderungen).
- Setup-Tab zeigt die neue Version (aus der Assembly) — mit der Download-Seite abgleichen.
- Auto-Update-Flow: Startup-Check (still) + „Nach Updates suchen“ + **Delta-Update** im
  Hintergrund (nur geänderte Dateien, kein UAC) → Anwenden beim Beenden + Auto-Neustart;
  Fallback auf die volle Setup.exe (UAC) bei fehlendem `ERCTelemetry.files.json` oder
  Program-Files-Installation. `%LOCALAPPDATA%\ERCTelemetry` (Settings + DB) bleibt erhalten.
- **Migration (Nutzerordner):** Der Installer legt die App seit dem Umzug in
  `%LOCALAPPDATA%\Programs\ERCTelemetry` ab (kein UAC, Delta-Updates möglich). Bestehende
  Program-Files-Installationen installieren beim ersten Update automatisch in den
  Nutzerordner; die alte Kopie unter Program Files einmalig manuell deinstallieren
  (Präzedenz: 1.0.1→0.x-Wechsel). Die Firewall-Regel legt die App beim ersten Start an
  (einmalige UAC-Abfrage, Setup-Tab) — der Installer macht das nicht mehr.
- Kein Git-Repo — keine Git-Operationen; der Repo-Stand ist der Stand.

## 6. Untouchables (auch beim Release)

- UDP-Port-Konstante (20777; Nutzer nutzt 20778 via Settings) — nicht ändern.
- settings.json-Schema, Overlay-DOM-IDs/CSS-Var-Kontrakt, HudWidgetIds — unantastbar.
- Channel-Consumer-Regel: jeder neue Consumer bekommt seinen eigenen Channel.
- `dotnet build` 0 Warnungen + `dotnet test` grün nach jeder Änderung.

## 7. Wenn etwas schiefläuft

- ISCC nicht gefunden → Inno Setup 6 installieren oder `ISCC=<Pfad>` setzen.
- Server nicht erreichbar → `installer/server-target.txt` (T:\downloads) prüfen.
- Manifest-sha256 = Hash der versionierten Datei; fileName muss darauf zeigen.
- Version falsch publiziert → **nicht senken**: nächste Version höher springen und in
  `UPDATELOG.md` (Nutzer-Sprache) dokumentieren.
