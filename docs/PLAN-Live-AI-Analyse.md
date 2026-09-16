# Plan: Live AI-Analyse + Renn-Assistenten

> Implementierungsplan für die nächste Feature-Runde. Zielgruppe: **Neulinge**, die
> während des Rennens lernen wollen, wie sie reagieren müssen (Reifen, Tank, Tempo,
> Gegner). Stand: 2026-09-08, `dotnet build`/`dotnet test` grün.
>
> **Status: ALLE 4 Phasen umgesetzt (2026-09-08).** `dotnet build` 0 Warnungen,
> `dotnet test` 656 Core + 13 ShareServer grün. Layer 1 (frei, ohne Key) vs. Layer 2
> (LLM, hinter `LlmEnabled`+`LlmApiKey`) strikt getrennt — Paywall-fähig.

## Kontext

Bereits vorhanden (nicht neu bauen!):

- **LLM-Schicht (Layer 2):** `Core/Llm` — `OllamaClient` (dünner HTTP-Client, null bei
  Fehler → Fallback auf Layer 1), `IRaceCommentator`/`IRaceCoach`/`IRaceSummarizer` +
  Llm-Implementierungen, `App/Composition/LlmService` (liest Settings live, `IsConfigured`).
- **Voice-Alerts:** `Core/VoiceAlerts/VoiceAlertPlanner` + `App/Composition/VoiceAlertService`
  (eigener `VoiceEvents`-Kanal, Edge-TTS mit Windows-Fallback, `_audio`-Playback-Kanal).
- **Proximity-Spotter:** `Core/VoiceAlerts/ProximitySpotter` + `VoiceSnapshots`-Kanal
  (4-Hz-Loop, 20-m-Reichweite, Richtung/Abstand/„Clear").
- **Analyse:** `PaceAnalyzer`, `StrategyAdvisor`, `FuelCalculator`, `OvertakeDetector`,
  `LapTraceComparer`, `BlindSpotCalculator`, `RaceReportBuilder`, `DriverDuel`.
- **Overlay/HUD:** Kommentar-Overlay (`/overlay/commentator.html`), In-Game-HUD-Widgets.

**Die Daten für die Live-Analyse sind alle schon im `TelemetrySnapshot`:**
`StandingsRow` (LastLapTimeMs, BestLapTimeMs, Sektorzeiten, TyreCompound, TyreAgeLaps,
FuelUsedLastLap, GapToLeaderMs), `PlayerFrame` (FuelInTank, FuelRemainingLaps,
TyreCompound, TyreAgeLaps, Wheels mit Pressure/SurfaceTemp/BrakeTemp), `Tyres`
(TyreStatus: WearPercent/DamagePercent), `TyreSets` (Reifenbank).

**Lücke:** Die bestehende AI ist **event-getrieben** (Kommentator) und **on-demand**
(Coach, Zusammenfassung). Es fehlt ein **periodischer Live-Analyse-Loop**, der alle paar
Runden auf den Zustand schaut und aktiv wird, wenn etwas bemerkenswert ist.

---

## Feature 1: Live AI-Analyse (Rundenzeiten / Reifen / Tank) — Hauptfeature

### Verhalten (Zielbild)

Während des Rennens spricht die App (oder zeigt im Overlay) nur dann etwas, wenn es
wirklich relevant ist:

- **Reifen:** „Vorderreifen bei 70 % Verschleiß — in 4 Runden verlierst du 0,3 s/Runde.
  Boxenfenster öffnet sich." (einmal pro Stint)
- **Tank:** „Du verbrauchst 2,1 kg/Runde — das reicht nicht bis Runde 30. Sparmodus oder
  früher Stopp." (einmal pro Stint)
- **Tempo-Trend:** „Deine letzten 3 Runden werden 0,4 s langsamer — die Reifen bauen ab."
  (bei Trend-Wechsel)
- **Status-Update:** alle 5 Runden ein kurzer Überblick (Position, Lücke, Reifen, Tank).

### Layer 1: Zustands-Digest (deterministisch, Core)

**Neu `Core/Analysis/LiveAnalysisDigest.cs`** — reine Daten:

```csharp
public sealed record LiveAnalysisDigest(
    byte Lap, byte TotalLaps, byte Position, int GapToLeaderMs,
    IReadOnlyList<uint> LastLapTimesMs,   // Rolling-Window (letzte 5)
    uint BestLapTimeMs,
    LapTrend Trend,                        // Slower / Faster / Stable
    float TrendDeltaMsPerLap,              // + = langsamer
    ActualCompound TyreCompound, byte TyreAgeLaps, float TyreWearPercent,
    float FuelInTank, float FuelRemainingLaps, float FuelUsedLastLap,
    float ProjectedFuelAtEnd);             // Tank − Verbrauch × Restrunden (negativ = Defizit)

public enum LapTrend { Slower, Faster, Stable }
```

**Neu `Core/Analysis/LiveAnalysisDigestBuilder.cs`** — **stateful** (rolling window):
- Hält die letzten 5 `LastLapTimeMs` des Spielers; erkennt einen neuen Rundenabschluss
  (Wert ändert sich) und hängt an.
- Trend: Durchschnitt der letzten 3 vs. der vorherigen 2 → Slower/Faster/Stable,
  Delta pro Runde.
- Reifen: Spieler-`TyreStatus.WearPercent` aus `Tyres` (null → 0), `TyreAgeLaps` aus
  `Standings`.
- Tank: `Player.FuelInTank`, `Player.FuelRemainingLaps`, `StandingsRow.FuelUsedLastLap`;
  `ProjectedFuelAtEnd = FuelInTank − FuelUsedLastLap × (TotalLaps − Lap)`.
- `Reset()` bei Session-Wechsel (neuer `SessionMeta`).

**Neu `Core/Analysis/LiveAnalysisTrigger.cs`** — entscheidet, WANN gesprochen wird:
- Reifen-Schwelle: `TyreWearPercent ≥ 60` → „Boxenfenster" (einmal pro Stint, Reset bei
  Compound-Wechsel).
- Tank-Defizit: `ProjectedFuelAtEnd < 0` → „Tank knapp" (einmal pro Stint).
- Tempo-Trend: Trend wechselt auf `Slower` mit `TrendDeltaMsPerLap ≥ 0.3` → „Zeiten
  werden langsamer" (einmal pro Trend-Wechsel).
- Status-Update: alle `N` Runden (Default 5) → kurzer Überblick.
- Rückgabe: `LiveAnalysisTriggerReason?` (Enum + Digest) oder null (nichts sagen).

### Layer 2: LLM-Formulierung (optional, Core)

**Neu `Core/Llm/ILiveAnalyst.cs`** + zwei Implementierungen (Muster der bestehenden
Interfaces):

- **`LiveAnalysisTemplate` (Layer 1):** deterministische deutsche Sätze aus dem Digest +
  Trigger-Grund. Funktioniert ohne API-Key.
- **`LlmLiveAnalyst` (Layer 2):** System-Prompt „Du bist ein deutscher F1-Fahrcoach für
  einen Neuling. Analysiere den Rennzustand und gib 2–4 konkrete, freundliche Sätze:
  was ist wichtig (Reifen, Tank, Tempo), was soll der Fahrer tun." + Digest als
  strukturierter Text. Null bei Fehler → Fallback auf Template.

### Ausgabe

- **Sprache (primär):** über die bestehende Voice-Pipeline (`_audio`-Kanal + `SpeakAsync`).
- **Overlay (optional, Phase 2):** eine Zeile im Kommentar-Overlay (`commentary`-Nachricht)
  oder ein HUD-Widget.

### Architektur (App)

- **`AppServices`:** neuer Kanal `Channel<TelemetrySnapshot> LiveAnalysisSnapshots`
  (bounded 8, `SingleReader = true` — **eigener Kanal pro Consumer**, Competing-Consumer-Regel),
  Feed im Aggregator-Loop, `TryComplete` in `Dispose`.
- **`VoiceAlertService`:** dritter Loop `RunLiveAnalysisAsync` (Muster von
  `RunProximityAsync`):
  - Liest `LiveAnalysisSnapshots`, baut den Digest (stateful Builder), prüft den Trigger.
  - Bei Trigger: `LlmService.IsConfigured` → `LlmLiveAnalyst`, sonst `LiveAnalysisTemplate`.
  - Spricht das Ergebnis nur, wenn `VoiceAlertsEnabled` UND `LiveAnalysisEnabled`.
  - `LlmService` wird in den Konstruktor injiziert (Komposition).
- **Kein neuer TTS-Pfad** — `_audio`-Kanal + `SpeakAsync` werden wiederverwendet.

### Settings

- **`AppSettings.cs`:** `bool LiveAnalysisEnabled { get; init; }` (Default **false** —
  LLM kostet API-Geld; Layer-1-Template funktioniert aber auch ohne Key) + Konstruktor-Param.
- **`SettingsViewModel.cs`:** Property + `LoadFrom` + `SaveAsync`-Block.
- **`MainWindow.xaml`:** Checkbox „Live AI-Analyse (Reifen/Tank/Tempo)" unter der
  Proximity-Checkbox in der STIMME-Sektion.

### Tests (Core, headless)

`tests/ERCTelemetry.Core.Tests/Analysis/LiveAnalysisDigestBuilderTests.cs` +
`LiveAnalysisTriggerTests.cs` + `LiveAnalysisTemplateTests.cs`:
- Digest: Rolling-Window erkennt neuen Rundenabschluss, Trend Slower/Faster/Stable,
  Delta-Berechnung, Reifen-Wear, Tank-Prognose (Defizit + ausreichend).
- Trigger: Reifen-Schwelle feuert einmal pro Stint (Reset bei Compound-Wechsel),
  Tank-Defizit einmal pro Stint, Tempo-Trend bei Wechsel, Status-Update alle N Runden,
  kein Trigger bei unauffälligem Zustand.
- Template: deutsche Sätze für jeden Trigger-Grund, invariante Formatierung.

---

## Feature 2: Boxenstopp-Timing (deterministisch, ohne AI)

- **Neu `Core/Analysis/PitStopAdvisor.cs`:** empfiehlt die Boxenrunde aus
  Reifenabbau-Kurve (Wear % + Alter → projizierter Verlust), Tank (Restrunden) und
  Streckenposition (Gap zu Vorder-/Hintermann). Rückgabe: „Box in Runde 18" / „weiter
  fahren" / „jetzt rein".
- Baut auf `StrategyAdvisor`/`FuelCalculator` auf (DRY).
- **Ausgabe:** Sprach-Ansage (einmal pro Stint) + optional HUD-Strategiezeile.
- **Kein LLM nötig** — funktioniert immer, auch offline.
- **Tests:** Reifenabbau treibt die Empfehlung, Tank-Notfall überstimmt, Position
  (Undercut-Fenster) fließt ein.

---

## Feature 3: Rivalen-Trends (AI)

- **Neu `Core/Analysis/RivalDigestBuilder.cs`:** verfolgt den Rivalen (aus
  `Standings`/`Tyres`): Boxenstopp (NumPitStops steigt), Reifenwechsel (Compound ändert
  sich), Tempo-Trend (letzte 3 Runden), Lücke zum Spieler.
- **Neu `Core/Llm/LlmRivalAnalyst.cs`** (Layer 2) + Template (Layer 1): 1–2 Sätze —
  „Bob ist in Runde 12 reingekommen, hat Medium aufgezogen und fährt jetzt 0,3 s
  schneller."
- **Trigger:** nur bei bemerkenswerten Aktionen des Rivalen (Stopp, Reifenwechsel,
  schnelle Runde), nicht periodisch.
- **Ausgabe:** Sprache + Kommentar-Overlay.
- **Tests:** Digest erkennt Stopp/Reifenwechsel/Trend, Template-Sätze, Trigger-Cooldown.

---

## Feature 4: Reifen-Temperatur-Warnung (deterministisch, ohne AI)

- **Neu `Core/Analysis/TyreTempMonitor.cs`:** prüft `Player.Wheels.SurfaceTemp` /
  `BrakeTemp` gegen Schwellen (z. B. Vorderreifen > 105 °C, Bremse > 800 °C).
- **Ausgabe:** Sprach-Ansage mit Cooldown (nicht jede Sekunde): „Vorderreifen überhitzt
  (110 °C) — du blockierst zu viel."
- **Kein LLM nötig.**
- **Tests:** Schwelle feuert, Cooldown, keine Warnung bei normalen Temperaturen.

---

## Reihenfolge / Phasen

1. **Phase 1 — Live AI-Analyse (Feature 1):** Digest + Trigger + Template (Layer 1)
   zuerst bauen und lokal testen, LLM-Politur (Layer 2) danach nachrüsten — exakt das
   Muster der bestehenden AI-Features.
2. **Phase 2 — Boxenstopp-Timing (Feature 2):** deterministisch, kein LLM.
3. **Phase 3 — Rivalen-Trends (Feature 3):** braucht den Digest-Builder aus Phase 1.
4. **Phase 4 — Reifen-Temperatur-Warnung (Feature 4):** klein, unabhängig.

> Empfehlung: Phase 1 + 2 zuerst — sie decken die Kernfrage „wann boxen, wie fahren"
> ab und funktionieren ohne API-Key. Phase 3 + 4 sind danach kleine Ergänzungen.

---

## Verifikation

1. `dotnet build` (0 Warnungen) + `dotnet test` (alle Tests grün, inkl. neuer Tests).
2. **Live (User):** Im Rennen Reifenabbau/Tank-Defizit/Tempo-Trend provozieren →
   Ansagen kommen genau einmal pro Stint; Status-Update alle 5 Runden; ohne API-Key
   spricht das Layer-1-Template, mit Key die AI-Formulierung.

## Nicht im Scope

- **Mobile Companion** (separate App, `docs/MOBILE.md`).
- **Discord-Login + gemeinsames Leaderboard** (offen in `docs/IDEAS.md`).
- Proximity-Reichweite/Detailgrad als Einstellung (aktuell fixe 20-m-Zonen).
