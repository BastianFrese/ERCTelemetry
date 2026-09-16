# ERCTelemetry — Mobile App (Bauplan)

> Ausgelagert aus `docs/IDEAS.md` (Stand 2026-09-07). Kompletter Bauplan für den
> Mobile Companion.

## Ziel

**Eine echte (native) App für Konsolen-Spieler, die neben ihrer Konsole nur ein
Handy haben — keinen PC.** Damit auch sie ihre Sessions so gut wie möglich haben:
Live-Standings, Gaps, Telemetrie, History — direkt auf dem Handy.

## Warum eine native App (und kein PWA)?

- ⚠️ **Ein PWA im Browser kann kein UDP empfangen** (Browser können keine rohen
  UDP-Sockets öffnen). Die F1-Telemetrie kommt aber als UDP-Pakete.
- Die App muss also nativ sein, um die Pakete direkt vom Spiel zu empfangen.

## Framework: .NET MAUI (Empfehlung)

- **`ERCTelemetry.Core` ist .NET (net10.0)** → die komplette Analyse (PaceAnalyzer,
  LapTraceComparer, BlindSpotCalculator, …) und das Packet-Parsing (F1Game.UDP)
  können **1:1 wiederverwendet** werden — kein Rewrite.
- MAUI targetet net10.0-android/ios → Core passt direkt.
- Flutter würde bedeuten, alles in Dart neu zu schreiben → unnötig.

## Architektur

### Live-Daten (Kern)
- Die Konsole sendet UDP an die IP des Handys (im Spiel konfigurierbar).
- Die App lauscht auf UDP, parst die Pakete (F1Game.UDP + Core) und zeigt
  Live-Standings, Gaps, Telemetrie, Player-Card.

### History + Analyse (lokal auf dem Handy)
- SQLite auf dem Handy (Core's `TelemetryDb`) → Sessions speichern.
- Analyse (PaceAnalyzer etc.) läuft auf dem Handy — kein Game-Performance-Problem,
  weil das Spiel auf der Konsole läuft.

### Teilen (optional, später)
- Sessions/Clips an den ShareServer hochladen → Share-Pages wie beim PC-Tool.

## Einschränkungen (ehrlich)

- **Gleiches WLAN:** Konsole + Handy müssen im selben Netz sein.
- **App im Vordergrund:** iOS pausiert Hintergrund-UDP — die App muss während des
  Spielens offen sein.
- **Handy-IP:** Muss im Spiel eingestellt werden (DHCP kann sie ändern → die App
  zeigt die aktuelle IP an).
- **Akku:** UDP-Empfang + Display kosten Akku.

## Bauplan (Phasen)

### Phase 1 — MAUI-Skeleton + UDP-Empfang
- MAUI-Projekt anlegen, Core referenzieren.
- UDP-Listener auf dem Handy, Pakete parsen, Live-Standings anzeigen.

### Phase 2 — Live-Dashboard
- Gaps, Telemetrie, Player-Card (recycelt die Overlay-Logik).

### Phase 3 — History + Analyse
- SQLite auf dem Handy, Sessions speichern, PaceAnalyzer etc. anzeigen.

### Phase 4 — Teilen (optional)
- Upload an den ShareServer, Share-Pages.

## Offene Fragen

- iOS zuerst, Android zuerst oder beides?
- Soll die App auch für PC-Spieler nützlich sein (PWA bleibt dann als Zusatz)?
