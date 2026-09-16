# ERCTelemetry — Ideen / Roadmap

> Brainstorm für die Zeit nach Phase 6. Alle Phasen 0–6 sind fertig (Stand 2026-09-07),
> `dotnet build`/`dotnet test` grün. Dieses File sammelt die nächsten Ideen — wir arbeiten
> hier dran, bis wir uns auf die nächste Richtung festlegen.

## Ausgangslage

Bereits vorhanden (nicht neu bauen!):

- Live-Standings/Ergebnisse für alle Spieler, Player-vs-Rival-Dashboard
- Overlays: standings, player-card, telemetry, timing-tower, race-control, relative, tyres,
  map, h2h, commentary, broadcast (OBS-Browser-Source + transparentes WPF-Overlay)
- History (SQLite) + JSON/CSV-Export + Share-Pages (`telemetrie.erdi-erc.de/s/{uid}`) + Collision-Clips
- UDP-Forwarding (bis 8 Ziele, RaceLab/SimHub), Packet-Recorder/Replay (`.f1rec`)
- Analyse: PaceAnalyzer, StrategyAdvisor, OvertakeDetector, FuelCalculator, LapTraceComparer,
  BlindSpotCalculator, RaceReportBuilder, DriverDuel
- Delta-Updates, Tray, Firewall, Update-Log

---

## 🤖 AI / Analyse

> **Grundsatz:** Alles nur, sofern es auf jeder PC/Laptop-Hardware läuft, ohne die
> Game-Performance zu beeinflussen → LLM-Berechnung in der Cloud (API), lokal nur ein
> dünner HTTP-Client + Throttling. **LLM ist optional (nur mit API-Key), nie Pflicht.**

### Hybrid-Architektur (Basis für alle drei Features) ✅
- **Layer 1 — deterministisch (ohne LLM):** Template-/Regel-Engine auf den bestehenden
  Analysen (`RaceReportBuilder`, `LapTraceComparer`, `BlindSpotCalculator`, Events).
  Kostenlos, sofort, offline — funktioniert immer.
- **Layer 2 — LLM-Politur (optional):** Nur wenn ein API-Key in den Settings gesetzt ist.
  Formuliert die Layer-1-Ergebnisse natürlich. Ohne Key → automatischer Fallback auf
  Layer 1, Feature bleibt nutzbar.
- **Umsetzung:** Pro Feature ein Interface (`IRaceCommentator`, `IRaceCoach`,
  `IRaceSummarizer`) mit zwei Implementierungen (Template + LLM). Anbieter austauschbar
  (Claude, GPT, Gemini …), A/B-Test der deutschen Qualität möglich.
- **Reihenfolge:** Erst Layer 1 bauen und lokal testen, LLM-Key später nachrüsten.
- **Umgesetzt (Layer 2):** `Core/Llm` — `OllamaClient` (dünner HTTP-Client auf
  `{baseUrl}/api/chat`, OpenAI-kompatibel, Bearer-Token, **null bei jedem Fehler** →
  Caller fallen auf Layer 1 zurück), `IRaceCommentator`/`IRaceCoach`/`IRaceSummarizer`
  + `LlmRaceCommentator`/`LlmRaceCoach`/`LlmRaceSummarizer` (deutsche System-Prompts,
  bündeln Layer-1-Ergebnisse). `App/Composition/LlmService` (liest Settings live, baut den
  Client bei Key/Modell/URL-Änderung neu, `IsConfigured`/`Status`/`TestAsync`/
  `GetModelsAsync`). Settings-Tab "AI-KOMMENTATOR (LLM)": Aktivieren + API-Key +
  Verbindungstest → lädt die verfügbaren Modelle (GET `/api/tags`, z. B. gemma, deepseek)
  in ein Auswahl-Dropdown (editierbar). Standard-Modell `deepseek-v4-flash:cloud`
  (Ollama-Cloud-Abo), Base-URL `https://ollama.com` (kein UI-Feld — nur per settings.json).
  21 Tests (OllamaClient-Wire inkl. Modell-Liste + Prompt-Inhalte).

### Live-AI-Kommentator ✅
- Telemetrie in Echtzeit → "Rennkommentator" als Stream-Overlay.
- **Layer 1:** Regel-Engine auf `EventFeed` (Gap < 0,5s → "Druck von hinten!", Reifen
  > 80% → "Boxenfenster!", Regen steigt → "Regen in ~3 Runden"). Deterministisch, sofort.
- **Layer 2:** LLM bekommt die gebündelten Events + Kontext und formuliert abwechslungsreiche
  Kommentare. Throttling (nicht jede Sekunde), Kosten.
- Basis: `RaceReportBuilder` + `EventFeed` (Events kommen schon gebündelt an).
- `CommentaryPlanner` (Core): Layer-1-Regeln auf Snapshot + StoreEvent — Druck von hinten
  (< 0,5s, 20s-Cooldown), Boxenfenster (Verschleiß ≥ 80 %, einmal pro Stint), Regen-Vorhersage
  (≥ 50 % innerhalb 5 min, 60s-Cooldown), Überholmanöver + Race-Control-Momente (Safety Car,
  Start, DRS, Spieler-spezifisch). Läuft im Overlay-Pump (`OverlayWebHost`), kein neuer Channel.
- Wire-Protokoll: neue `commentary`-Nachricht (`OverlayCommentary`), Overlay-Seite
  `/overlay/commentator.html` (scrollender Feed, max 8 Zeilen). 16 Tests.
- **Layer 2 (umgesetzt):** `OverlayWebHost` startet eine gedrosselte LLM-Schleife (45 s
  Intervall, `SemaphoreSlim(1,1)` gegen Stapelung): bekommt den frischesten Snapshot +
  die letzten 20 Layer-1-Zeilen (`CommentaryContext`) und publiziert die LLM-Antwort als
  `commentary`-Zeile. Fehlender Key/Modell, Fehler oder belegtes Gate → Tick wird
  übersprungen, Layer 1 redet weiter.

### AI-Coach (Post-Session-Feedback) ✅
- Nach der Session verständliches Feedback: "In Kurve 3 verlierst du 0,4s gegen deinen
  Rivalen — du bremst zu früh."
- **Layer 1:** Strukturierter Gap-Report aus `LapTraceComparer` + `BlindSpotCalculator`
  ("Kurve 3: −0,4s, Bremspunkt 12m früher") + Tipp-Datenbank (Finding → Ratschlag).
- **Layer 2:** LLM verpackt die Findings in natürliche Sprache.
- **Umgesetzt:** `CoachReport`/`CoachReportBuilder` (Core): konsumiert `DuelReport.TraceTips`
  + `CornerLosses`, mappt jedes Finding auf einen deutschen Ratschlag (Tipp-Datenbank:
  Verlust-Schwellen 0,2s/0,5s → stärkere Tipps, Tip-Kind loss/gain/brake/apex → Aktion).
  AI-COACH-Karte im Rennreport (Titel "vs. {Rivale}", Gesamtverlust, Findings-Liste).
  9 Tests.
- **Layer 2 (umgesetzt):** `RaceReportViewModel` feuert nach dem Rebuild einen
  `CoachAsync`-Aufruf (fire-and-forget, `_rebuildVersion`-Guard verwirft veraltete
  Antworten nach einem Picker-Wechsel); die LLM-Formulierung erscheint als `LlmCoach`-Zeile
  unter den Findings. Ohne Key/Fehler bleibt die Karte rein Layer 1.

### Rennzusammenfassung ✅
- Statt trockener Statistik eine Story: "Du hast dich in Runde 12 mit einem Undercut von
  P5 auf P3 geschoben…"
- **Layer 1:** Template-Generator auf `RaceReportBuilder`-Daten (Start → Events → Ziel),
  wie Automated Insights/Wordsmith.
- **Umgesetzt:** `RaceSummaryBuilder` (Core) — deterministische deutsche Erzählung aus dem
  `RaceReport`: Intro (Session-Typ, Strecke, Runden, Wetter), Sieger/Podium, Spieler-Story
  (Start→Ziel, schnellste Runde, Boxenstopps, Führungsrunden, Strafen), Spieler-Events aus der
  Timeline (Überholmanöver, schnellste Runde, Safety Car, max 6), Duell-Absatz (Runden-Score,
  beste Runde, größter Verlust). ZUSAMMENFASSUNG-Karte im Rennreport. 8 Tests.
- **Layer 2 (umgesetzt):** `RaceReportViewModel` feuert nach dem Rebuild einen
  `SummarizeAsync`-Aufruf (fire-and-forget, `_rebuildVersion`-Guard); die LLM-Erzählung
  erscheint als `LlmSummary`-Zeile unter den Layer-1-Absätzen. Ohne Key/Fehler bleibt die
  Karte rein Layer 1.

---

## 📡 Streamer-Integration

### Twitch-Chat-Commands ✅
- Zuschauer tippen `!gap`, `!pace`, `!reifen` → Antwort im Chat. Dazu wenn möglich, dass man Fahrer namen dazuschreiben kann, aber auch so, dass man einen fahrernamen nicht zu 100% genau schreiben muss, sondern z.b: ein Fahrer heißt Manuel Hauser2, ein andere fahrer heißt Erdi10, dass es reicht wenn schreibt, z.b !gap hauser erdi.
- **Twitch-Login nötig:** Der Streamer muss sich über Twitch (OAuth) anmelden, damit die
  Chat-Commands funktionieren (IRC/EventSub braucht ein Token).
- Basis: Overlay-Server (Kestrel) läuft schon; Twitch-IRC/EventSub-Client fehlt.
- **Umgesetzt:** `Core/TwitchChat` (headless-testbar, 6 Testdateien): `ChatCommandParser`
  (`!gap`/`!pace`/`!reifen`/`!tyres`), `DriverNameMatcher` (exakt > Präfix > Teilstring, case-insensitiv —
  `!gap hauser erdi` löst "Manuel Hauser2" und "Erdi10" auf), `ChatAnswerBuilder` (deutsche Antworten,
  invariant formatiert), `TwitchChatService` (Per-User- + Global-Cooldown), `TwitchIrcClient`
  (IRC über WebSocket, PING/PONG, serialisierte Sends), `TwitchOAuth` (Autorisierungs-Code-Flow).
  App-seitig `TwitchChatHost` (eigener `TwitchSnapshots`-Channel, verbindet/trennt nach Settings,
  OAuth-Login über lokalen Callback-Port 8091, eingebettete Client-ID + PKCE — kein Secret, Kanal
  wird aus dem Token erkannt) + Settings-Sektion "TWITCH-CHAT" (Schalter, Login-Button, Live-Status).

### Voice-Alerts (TTS) ✅
- "Box, Box, Reifen sind durch" / "Achtung, Regen in 3 Runden" als Sprachausgabe.
- Basis: **Microsoft-Neural-Stimmen via Edge-TTS** (readaloud-WebSocket, z. B. Katja/Conrad Neural)
  — deutlich natürlicher als die generische Windows-TTS-Stimme. Ohne Internet fällt die Ausgabe
  pro Ansage automatisch auf die lokale Windows-Stimme (`System.Speech`) zurück, damit Alerts
  auch offline nie verstummen. Stimme wählbar in Settings → Verhalten ("STIMME"-Dropdown, editierbar).
- **Umgesetzt:** `Core/VoiceAlerts/VoiceAlertPlanner` (headless-testbar, 19 Tests) + App-seitiger
  `VoiceAlertService` (eigener `VoiceEvents`-Channel). Ansagen: Renn-/Quali-Start, Regen-Vorhersage
  (≤ 15 min, ≥ 50 %), Rennende mit Position, Reifenverschleiß (einmal pro Stint, compound-abhängig),
  Überholmanöver, Strafen/Verwarnungen, Safety Car, Startampeln, DRS, Kollision, schnellste Runde.
  Opt-in in Settings → Verhalten (Checkbox + Test-Button).

---

## 🏎️ Renn-Strategie

### Wetter-Radar ✅
- F1 26 hat dynamisches Wetter → aus Telemetrie (Regenintensität pro Sektor) eine
  Regen-Vorhersage bauen und als Overlay zeigen.
- `WeatherRadar` (Core): Forecast-Samples → Modell (Segmente, Risiko-Klasse, Zusammenfassung).
- HUD-Overlay: Regen-Wahrscheinlichkeits-Balken (Segment pro Zeitfenster, Blau nach
  Regenwahrscheinlichkeit, Tooltip mit Details) unter der Wetter-Zeile; Dashboard nutzt
  dieselbe Zusammenfassung (DRY). 7 Tests.

### Undercut/Overcut-Rechner ✅
- Pit-Stop-Fenster live: "Wenn du jetzt rein kommst, kommst du vor P4 raus."
- `PitWindowCalculator` (Core): Gap zum Vordermann + Boxenstopp-Verlust + Reifen-Delta →
  Undercut-Fenster (funktioniert es jetzt? Wie viele Runden bleibt das Fenster offen?)
  und Overcut-Check. Pure, testbare Logik.
- HUD-Strategiezeile zeigt "undercut ✓ (window N laps)" / "overcut ✓" live. 8 Tests.

### Sektor-Deltas live ✅
- Mini-Sektoren mit Grün/Lila wie im F1-TV-Broadcast, statt nur Gesamtzeit.
- Basis: LapData-Sektoren sind schon im Store; Delta-Logik + Overlay-Widget fehlen.
- **Umgesetzt:** Timing-Tower-Widget im In-Game-HUD (Position · Name · drei Mini-Sektor-Chips
  lila/grün · Abstand zum Führenden, Spieler-Zeile gold hervorgehoben). `UpdateTower` baut die
  Zeilen nur bei Zustandsänderung neu (Signatur-Vergleich, kein 30-fps-Churn). `HudPurple`-Pinsel
  in beiden Farbschemata; `HudShowTower`-Setting (Settings → HUD, Default an).

---

## 📊 Langzeit

### Karriere-/Saison-Tracking ✅
- Über Sessions hinweg eine Mini-Meisterschaft: Punkte, Fahrerwertung, Konstrukteurs-Wertung,
  Formkurve.
- `ChampionshipCalculator` (Core): Fahrerwertung (Punkte, Siege, Podiums, beste Position,
  Team = meistgefahrenes), Konstrukteurs-Wertung, Formkurve (chronologisch). Tiebreaks:
  Punkte → Siege → beste Position → Name. 7 Tests.
- `TelemetryDb.GetChampionship()`: aggregiert alle finalisierten Race-Sessions.
- UI: MEISTERSCHAFT-Karte im History-Tab (Fahrer-/Konstrukteurs-DataGrid + Formkurve).

### Driver-Rating / ELO ✅ (lokal)
- Eigene Pace über Sessions tracken und gegen Rivalen bewerten.
- Basis: `PaceAnalyzer` + History-DB.
- **Umgesetzt (lokal):** `DriverRatingCalculator` (Core) — ELO über alle gespeicherten
  Race-Sessions: jeder Fahrer startet bei 1500, pro Rennen wird jedes Fahrer-Paar nach
  Zielposition verglichen (Erwartungswert aus Rating-Differenz, K-Faktor 32). FAHRER-RATING
  (ELO)-Karte im History-Tab (Leaderboard, Spieler-Zeile hervorgehoben, "Dein ELO").
  8 Tests.
- **Discord-Login + Leaderboard (offen):** Login über Discord (OAuth), gemeinsames
  Leaderboard und Vergleich untereinander — damit Rookies von erfahrenen Fahrern lernen
  können. Das ELO-Modell ist dafür schon server-tauglich.
- Aufwand steigt dadurch auf **groß** (User-Accounts + Leaderboard-API auf dem ShareServer).
---

## 📱 Sonstiges

### Mobile Companion
- **Ausgelagert nach `docs/MOBILE.md`** — native App für Konsolen-Spieler (nur Handy, kein PC).

### Setup-Sharing ✅
- Car-Setups exportieren/importieren als kompakter Share-Code (wie F1s eigene Codes).
- `SetupCodec` (Core): `ERC1-{24 Base36-Tokens}-{Checksum}` — Floats skaliert, negative
  Werte mit `n`-Präfix, Checksumme fängt Tippfehler/fremde Formate ab, Decode wirft nie.
- UI: SETUP-Karte im Rennreport (Werte + „Code kopieren"), Import-Box für fremde Codes
  (Vorschau, wird nicht gespeichert). 8 Tests (Roundtrip, Checksumme, Negativwerte, Müll).

---

## Priorisierung

> Alle Desktop-App-Ideen sind umgesetzt. Einziger offener Punkt: **Mobile Companion**
> (separate native App, Bauplan in `docs/MOBILE.md`).

| Idee | Geilheit | Aufwand | Reuse | Status |
|---|---|---|---|---|
| Live-AI-Kommentator | ★★★★★ | mittel | RaceReportBuilder, EventFeed | ✅ |
| AI-Coach | ★★★★★ | mittel | LapTraceComparer, BlindSpotCalculator | ✅ |
| Rennzusammenfassung | ★★★★☆ | klein | RaceReportBuilder | ✅ |
| Twitch-Chat-Commands | ★★★★☆ | mittel | Overlay-Server | ✅ |
| Voice-Alerts (TTS) | ★★★★☆ | klein | Events | ✅ |
| Wetter-Radar | ★★★★☆ | mittel | Session-Wetter | ✅ |
| Undercut/Overcut-Rechner | ★★★★☆ | mittel | StrategyAdvisor | ✅ |
| Sektor-Deltas live | ★★★★☆ | mittel | LapData-Sektoren | ✅ |
| Karriere-/Saison-Tracking | ★★★☆☆ | mittel | History-DB | ✅ |
| Driver-Rating / ELO | ★★★☆☆ | groß | PaceAnalyzer, ShareServer | ✅ (lokal) |
| Mobile Companion | ★★★☆☆ | mittel | Core, F1Game.UDP | ⬜ |
| Setup-Sharing | ★★★☆☆ | klein | ShareServer | ✅ |

> ⬜ = offen, 🚧 = in Arbeit, ✅ = fertig
