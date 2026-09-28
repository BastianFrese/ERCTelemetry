# Rennabbruch → reduzierte Punkte: was die App senden muss

> Ergänzung zu `docs/HANDOFF.md`. Ziel: Wenn ein Ligarennen **abgebrochen** wird, vergibt
> erdi-erc.de nur **50 %** oder **75 %** der Punkte. Damit die Webseite das automatisch
> erkennen kann, muss die App die **gefahrene Distanz** mitschicken. Heute kommt am Server
> kein einziges Distanz- oder Abbruchsignal an.

## Warum überhaupt etwas fehlt

Der Server berechnet Punkte **nicht** aus mitgesendeten Zahlen, sondern bei jedem
Tabellen-Rebuild neu aus der Endposition × Punkteschlüssel. Er braucht deshalb kein
Punktefeld, sondern ein **Renn-weites Distanzverhältnis**, um zu erkennen, dass die Session
vorzeitig endete.

Die Daten dafür liegen in der App **bereits vor** — sie werden nur nicht weitergereicht:

| Wert | Wo er schon existiert |
|---|---|
| Soll-Distanz (Soll-Runden) | `SessionMeta.TotalLaps` — `src/ERCTelemetry.Core/Session/Snapshots.cs:13` |
| Gefahrene Runden je Fahrer | `FinalResultRow.NumLaps` — `src/ERCTelemetry.Core/Session/Snapshots.cs:321` |

Beides wird in `PersistencePump.TryPublishErcEnd` (`src/ERCTelemetry.App/Composition/PersistencePump.cs:209-247`)
aktuell **verworfen**: nur Position, Fahrer, Zeit, Grid-Position und DNF gehen in den Payload.

## Der Vertrag mit dem Server

Der Server liest den Payload tolerant (unbekannte JSON-Keys werden ignoriert). Zwei **neue,
optionale** Felder genügen:

| Feld | Typ | Bedeutung |
|---|---|---|
| `totalLaps` | `int?` | Soll-Runden der Session (Renn-Distanz) |
| `finishes[].numLaps` | `int?` | Vom Fahrer tatsächlich absolvierte Runden |

Daraus leitet der Server ab:

```
CompletedLaps = max(numLaps über alle gewerteten Fahrer)
Quote         = CompletedLaps / totalLaps
```

| Quote | Punkte |
|---|---|
| 100 % (regulär beendet) | 100 % |
| ≥ 75 %, aber abgebrochen | 75 % |
| < 75 % | 50 % |

Der Admin sieht diesen Vorschlag im Review-Formular der Webseite und kann ihn **übersteuern**.
Die App muss also nichts entscheiden — sie liefert nur die Zahlen.

> **Nur Zahlen senden, keinen Faktor.** Der Server speichert `totalLaps` und `completedLaps`
> als Rohdaten. So kann eine spätere Regeländerung (andere Schwelle) ohne Neu-Import wirken,
> und das Review-Formular kann „31 / 44 Runden (70 %)" anzeigen.

## 1. Payload erweitern

`src/ERCTelemetry.Core/Share/ErcResultPayload.cs`

- `ErcRaceResultPayload` (`:9-15`) um `int? TotalLaps = null` erweitern.
- `ErcRaceFinish` (`:20-25`) um `int? NumLaps = null` erweitern.

Beide **optional mit Default** halten, damit bestehende Aufrufer weiter kompilieren.

Die Datei wird mit den Web-Defaults serialisiert (`Web defaults` → camelCase), die Namen
`totalLaps` und `numLaps` kommen also genau so beim Server an.

## 2. Werte im Pump einsammeln

`src/ERCTelemetry.App/Composition/PersistencePump.cs`

Die Soll-Distanz beim Session-Start puffern — sie ist am Sessionende nicht mehr sicher
verfügbar:

```csharp
// Feld neben _sessionType / _track
private byte _totalLaps;

// in Handle(), case SessionStarted s: (bei :72-82)
_totalLaps = s.Meta.TotalLaps;
```

Die gefahrenen Runden stehen direkt in `ended.Results` (`row.NumLaps`) — die Liste wird in
`TryPublishErcEnd` (`:225-240`) ohnehin schon durchlaufen:

```csharp
finishes.Add(new ErcRaceFinish(
    Position: dnf ? 0 : row.Position,
    Driver: drivers.TryGetValue(row.CarIndex, out var name) ? name : row.Name,
    RaceTimeMs: row.TotalRaceTimeSeconds > 0 ? (long)Math.Round(row.TotalRaceTimeSeconds * 1000) : null,
    QualifyingPosition: row.GridPosition > 0 ? row.GridPosition : null,
    Dnf: dnf,
    NumLaps: row.NumLaps > 0 ? row.NumLaps : null));
```

## 3. Event und Dialog durchreichen

- `src/ERCTelemetry.App/Composition/ErcRacePromptService.cs:9-13` — `ErcRaceEndedEvent` um
  `int? TotalLaps` erweitern.
- `PersistencePump.TryPublishErcEnd` (`:246`) — beim `new ErcRaceEndedEvent(...)` mitgeben.
- `src/ERCTelemetry.App/Dashboard/ErcSendRaceDialog.xaml.cs:61-67` — beim Bauen des
  `ErcRaceResultPayload` in `TotalLaps:` übernehmen.

## 4. CSV-Export nachziehen

`src/ERCTelemetry.Core/Persistence/ResultsExporter.cs`

- `WriteJson` (`:14-47`) schreibt `session.totalLaps` schon (`:26`) — **unverändert**.
  `results[].numLaps` steht ebenfalls bereits drin (`:35`). Der Telemetrie-CSV-Weg der
  Webseite liest allerdings **CSV**, nicht JSON.
- `WriteCsv` (`:49-71`): Header (`:52`) um eine Spalte `totalLaps` ergänzen und je Zeile
  mitschreiben. `numLaps` pro Zeile existiert bereits (`:59`).
  > Die Signatur `WriteCsv(string path, IReadOnlyList<FinalResultRow> results)` hat aktuell
  > **keinen** Zugriff auf die Session — sie braucht `SessionMeta?` (oder `int totalLaps`)
  > als zusätzlichen Parameter, analog zu `WriteJson`.
- **Der Aufrufer hat die Zahl auch nicht zur Hand.** Einziger Produktionsaufruf ist
  `src/ERCTelemetry.App/Dashboard/HistoryViewModel.cs:936`
  (`ResultsExporter.WriteCsv(dlg.FileName, _db.GetResults(id))`). `TotalLaps` **ist** in der
  Sessions-Tabelle persistiert (`TelemetryDb.OpenSession` bindet `meta.TotalLaps` in
  `TelemetryDb.cs:271`), aber **kein Getter gibt sie heraus**: weder `SessionSummary`
  (`:748-757`) noch `SessionCard` (`:796-807`) führen das Feld. Also entweder einen kleinen
  Getter ergänzen (z. B. `GetSessionTotalLaps(long sessionId)`) oder beide Records erweitern —
  und den Wert in `HistoryViewModel` mitgeben.
  > Nebenbei: `WriteJson` wird an `HistoryViewModel.cs:725` mit `meta: null` aufgerufen, der
  > JSON-Export schreibt `session` also derzeit gar nicht. Falls das mitgezogen wird, hat der
  > JSON-Weg dieselbe Lücke.

Der CSV-Header der Webseite ist bereits auf genau diese Spalten vorbereitet; `numLaps` wird
dort sogar schon gemappt, bisher aber ignoriert.

## 5. Randfälle

| Fall | Verhalten |
|---|---|
| `TotalLaps == 0` (Zeitrennen, unbekannt) | Feld als `null` senden → Server bleibt bei 100 % |
| Replay / Idle / freies Training | Feuert weiterhin **nicht** (`PersistencePump.cs:211-217`) — keine Änderung |
| Alle Fahrer DNF (`NumLaps` alle 0) | `NumLaps: null` → Server behandelt wie „unbekannt" (100 %) |
| Abgebrochenes Rennen, das die App gar nicht sieht | Admin setzt den Faktor im Rennformular manuell |
| Alte App-Version gegen neuen Server | Kein Feld → 100 %, unkritisch |
| Neue App-Version gegen alten Server | Server ignoriert unbekannte Keys, unkritisch |

**Wichtig:** `Dnf`-Zeilen behalten `Position = 0`. Für die Ableitung zählen nur die
**gewerteten** Fahrer, weil DNF-Zeilen eine abgebrochene Rundenzahl tragen können.

## 6. Tests

- `tests/` des Core-Projekts: Payload-Serialisierung mit/ohne `totalLaps`/`numLaps`
  (camelCase-Namen sichern).
- `ResultsExporter`-Test: neuer CSV-Header + Spalte, `totalLaps > 0` und `0`.
- Falls `TryPublishErcEnd` testbar ist: leeres `SessionMeta.TotalLaps` ⇒ `TotalLaps: null`.

## Checkliste zum Abhaken

- [ ] `ErcRaceResultPayload.TotalLaps` und `ErcRaceFinish.NumLaps` ergänzt
- [ ] `_totalLaps` beim `SessionStarted` gepuffert
- [ ] `NumLaps` je Finish befüllt (`0` → `null`)
- [ ] `ErcRaceEndedEvent` + `TryPublishErcEnd` + Send-Dialog durchgereicht
- [ ] `WriteCsv` schreibt `totalLaps` (SessionMeta durchgereicht)
- [ ] Tests grün, `dotnet build` ohne Warnungen
