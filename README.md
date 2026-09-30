# ERCTelemetry

Windows-Desktop-Anwendung für die Rennsimulation **EA F1 26**. Sie liest die
UDP-Telemetrie des Spiels mit, wertet sie live aus und macht daraus das, was ein
Streamer und eine Liga im Betrieb brauchen: ein Live-Klassement, ein Cockpit für
den eigenen Wagen, automatisch geschnittene Kollisions-Clips und ein Overlay, das
direkt in OBS oder im Spiel darübergelegt werden kann.

Die App läuft im echten Betrieb — sie ist der Telemetrie-Teil zu
[ERDI's Racing Community](https://erdi-erc.de) und speist die Liga-Website mit
Ergebnisdaten.

## Was sie kann

- **Live-Klassement und Ergebnisse** für alle Fahrer einer Session, inklusive
  Zwischenständen, Strafen und Session-Historie
- **Fahrer-gegen-Rivalen-Vergleich** — Telemetrie-Dashboard mit Gas, Bremse,
  Geschwindigkeit und Linienvergleich
- **Strategie-Analyse** — Sprit, Reifen, Boxenstopp-Fenster
- **Automatische Kollisions-Clips** — FFmpeg schneidet brenzlige Szenen als
  H.264-MP4 heraus
- **Overlays** für OBS (Browser-Quelle) sowie ein Overlay-Fenster im Spiel
- **Sprachhinweise** (Edge-TTS) und **KI-Rennkommentar** über ein lokales Ollama
- **Session-Historie** in einer SQLite-Datenbank
- **„ShareServer"** — eine kleine ASP.NET-Core-Anwendung, die Session-Seiten
  veröffentlicht und Clip-Uploads entgegennimmt (Anmeldung über Twitch oder Discord)

## Aufbau

| Projekt | Beschreibung |
|---|---|
| `src/ERCTelemetry.Core` | net10.0-Klassenbibliothek — die gesamte Logik, ohne UI und damit headless testbar |
| `src/ERCTelemetry.App` | net10.0-windows WPF-Anwendung (AssemblyName `ERCTelemetry`), Tray-Icon, Overlays |
| `src/ERCTelemetry.ShareServer` | net10.0 ASP.NET-Core-Minimal-API für Session-Seiten und Clip-Uploads |
| `tests/ERCTelemetry.Core.Tests` | xUnit-Tests für die Core-Bibliothek |
| `tests/ERCTelemetry.ShareServer.Tests` | xUnit-Tests für den ShareServer |

**880 Tests** (801 + 79) laufen in beiden Testprojekten.

## Voraussetzungen

- **Windows 10/11**
- **.NET 10 SDK**
- **FFmpeg** — wird **nicht** mitgeliefert (die Binärdatei ist ~98 MB groß und
  gehört nicht in die Versionsverwaltung). Benötigt wird der
  [gyan.dev-Essentials-Build](https://www.gyan.dev/ffmpeg/builds/) (GPL).
  Die Datei muss unter
  `src/ERCTelemetry.App/ffmpeg/ffmpeg.exe` liegen.
  Fehlt sie, lässt sich das Projekt weiterhin bauen und starten — nur die
  Clip-Aufzeichnung bleibt dann mit einem Hinweis in den Einstellungen aus.

## Bauen und starten

```powershell
git clone https://github.com/BastianFrese/ERCTelemetry.git
cd ERCTelemetry

# FFmpeg hier ablegen (siehe oben):
#   src/ERCTelemetry.App/ffmpeg/ffmpeg.exe

dotnet build ERCTelemetry.slnx
dotnet test  ERCTelemetry.slnx
dotnet run --project src/ERCTelemetry.App
```

Der ShareServer läuft mit:

```powershell
dotnet run --project src/ERCTelemetry.ShareServer
```

## Einstellungen und Zugangsdaten

Die Anwendung legt ihre Konfiguration unter
`%LOCALAPPDATA%\ERCTelemetry\settings.json` ab. Zugangsdaten wie das
Twitch-Token oder API-Schlüssel werden darin **DPAPI-verschlüsselt** gespeichert
(mit `dpapi:`-Präfix, gebunden an das Windows-Benutzerkonto) — nie im Klartext.

Der ShareServer liest seine Geheimnisse (Discord-Client-Secret,
Verbindungszeichenfolgen) zur Laufzeit aus Umgebungsvariablen mit dem Präfix
`Share__`. In der Versionsverwaltung stehen nur die Vorlagen ohne Werte.

### Hinweis zum `DefaultToken`

In `src/ERCTelemetry.Core/Share/ShareConstants.cs` steht ein `DefaultToken`. Das
ist **kein versehentlich eingechecktes Geheimnis**: Der ShareServer liefert genau
diesen Wert über den offenen Endpunkt `GET /api/config` selbst aus, damit sich
die Clients automatisch konfigurieren. Er schützt lediglich davor, dass fremde
Anwendungen Clips hochladen. Wer ihn „repariert", bricht die Anmeldung.

## Weitere Dateien

- `docs/` — Projektstatus, Bugfix-Listen, Roadmap, Release-Checkliste
- `UPDATELOG.md` — nutzersichtbares Änderungsprotokoll (wird in der App angezeigt)
- `installer/` — Inno-Setup-Skript und Build-Skript für das Setup
- `tools/` — Hilfswerkzeuge, u. a. Skripte zur Ansteuerung von OBS/Streamlabs
  und GoXLR sowie ein Generator für Streckenlayouts
- `scripts/` — Betriebsskripte

## Lizenz

Für dieses Repository ist noch keine Lizenz hinterlegt. Eingebundene
Fremdinhalte haben eigene Bedingungen: FFmpeg (gyan.dev-Build, GPL),
Streckenumrisse (CC BY 4.0, siehe `src/ERCTelemetry.App/Assets/Tracks/LICENSE.txt`),
Schriftart Orbitron (SIL OFL, siehe `src/ERCTelemetry.App/Fonts/OFL.txt`).
