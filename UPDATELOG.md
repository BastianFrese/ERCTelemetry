# Update-Log — ERCTelemetry

## 0.6.4.1 — Beta (2026-09-11)

### Neu

- **Session teilen ohne Einrichtung**: Enduser können ihre Aufnahmen jetzt direkt
  weitergeben, ohne vorher einen Schlüssel in den Einstellungen einzutragen — die App
  holt sich den Server-Schlüssel automatisch. Wer selbst einen Schlüssel einträgt,
  behält weiterhin Vorrang (z. B. nach einem Server-Schlüssel-Wechsel).

### Verbessert

- **Zuverlässigeres Teilen**: Ist der Telemetrie-Server kurz nicht erreichbar, arbeitet
  die App mit dem zuletzt gültigen Schlüssel weiter, statt das Teilen abzubrechen.

## 0.6.4 — Beta (2026-09-11)

### Behoben

- **Session teilen mit großen Clips**: Beim Teilen schlugen Sessions mit Aufnahmen über
  ~100 MB bisher mit „Teilen fehlgeschlagen: Error while copying to a stream“ fehl
  (Upload-Limit des Telemetrie-Servers). Große Clips lädt die App jetzt automatisch in
  kleineren Teilen hoch — der Server setzt sie wieder zu einer vollständigen Aufnahme
  zusammen, damit funktioniert auch das Teilen langer Rennen mit großen Clips zuverlässig.

## 0.6.3 — Beta (2026-09-11)

Diese Version verbindet die App mit deiner ERC-Community: Ergebnis direkt nach dem
Rennen melden, mit deinem Discord-Account einloggen und Track-Setups ganz einfach
aus der App in F1 26 übernehmen.

### Neu
- **Rennen an erdi-erc.de senden**: Nach einem beendeten Liga-Rennen fragt die App, ob
  du das Ergebnis an die ERC-Website senden möchtest. Du wählst die Liga aus — die
  Gesamtwertung landet als Entwurf beim Admin zur Freigabe. Dafür brauchst du deinen
  persönlichen API-Key (Einstellungen → ERC-Ergebnis).
- **Discord-Login**: Ein Klick auf „Mit Discord verbinden“ loggt dich mit deinem
  Discord-Account ein — nötig für die Track-Setups und den Key-Abgleich. Der Login läuft
  sicher über den Telemetrie-Server.
- **Track-Setups (Setups-Tab)**: Ein neuer Tab lädt die Track-Setups, die dein
  Discord-Zugriff auf erdi-erc.de sehen darf. Du filterst nach Strecke und Spieljahr und
  erzeugst mit einem Klick den ERC1-Share-Code, den du direkt in F1 26 einfügst.

### Verbessert
- **Sprachausgabe im richtigen Moment**: Ansagen wie „überhitzte Bremsen“ kommen jetzt
  deutlich näher am tatsächlichen Renngeschehen. Staut sich einmal viel an, werden alte
  Ansagen verworfen, statt verspätet ausgesprochen zu werden — nach der Zielflagge kommen
  keine veralteten Kommentare mehr.

### Bekannte Grenzen
- Der Discord-Token wird nur im Arbeitsspeicher gehalten und nie gespeichert — nach
  einem App-Neustart loggst du dich einmal neu ein.
- Zum Senden von Ergebnissen brauchst du deinen persönlichen API-Key (wird vom ERC-Admin
  ausgestellt).

## 0.6.2.135 — Beta (2026-09-08)

### Behoben
- **Session-Abschluss**: Schließt du das Spiel oder verlässt du die Online-Lobby, wird die
  Session jetzt automatisch als beendet markiert und erscheint in der Historie — auch ohne
  Zielflagge. Pausierst du länger als 30 Sekunden und spielst dann weiter, läuft die
  Session nahtlos weiter (Runden und Ergebnis bleiben erhalten).

### Neu
- **Clip-Auflösung einstellbar**: In den Clip-Einstellungen kannst du die Auflösung der
  Kollisions-Clips wählen (1280 / 1920 / 2560 / 3440). Auf deinem Ultrawide-Bildschirm
  nimmst du Clips jetzt in voller Breite auf — das Seitenverhältnis bleibt erhalten.

## 0.6.2 — Beta (2026-09-08)

Die App wird jetzt dein Renn-Ingenieur: Während des Rennens analysiert sie live
Reifen, Tank, Tempo und deinen Rivalen — und spricht dir die wichtigsten Hinweise
direkt ins Ohr, mit der natürlichen Neural-Stimme aus 0.6.0.

### Neu
- **Live AI-Analyse**: Die App beobachtet dein Rennen und spricht dir Hinweise zu —
  einmal pro Stint, wenn die Reifen verschlissen sind (ab 60 %), bei Tank-Defizit,
  bei Tempo-Trend-Wechsel und alle 5 Runden einen kurzen Status-Überblick.
- **Boxenstopp-Timing**: Die App sagt dir, wann du an die Box sollst („Box in Runde X“),
  ob du weiterfahren kannst oder sofort rein musst — inklusive Undercut-Fenster, wenn
  ein Gegner dicht hinter dir ist.
- **Rivalen-Trends**: Die App meldet, wenn dein direkter Rivale an die Box kommt,
  die Reifen wechselt oder eine schnelle Runde fährt.
- **Reifen-Temperatur**: Überhitzen die Vorderreifen (über 105 °C) oder die Bremsen
  (über 800 °C), wirst du gewarnt.

### Verbessert
- **Mit oder ohne AI-Key**: Ohne API-Key spricht die eingebaute Vorlage (kostenlos),
  mit Key formuliert die AI die Hinweise natürlich aus.

### Bekannte Grenzen
- Die Live-Analyse ist standardmäßig aus — aktivieren in Einstellungen → Verhalten
  („Live AI-Analyse (Reifen/Tank/Tempo)“). Sie braucht die Sprach-Alerts („STIMME“).

## 0.6.1 — Beta (2026-09-07)

Kollisions-Clips sind jetzt echte Videos: mit Ton, flüssig abspielbar und in
korrekten Farben — auch auf HDR-Bildschirmen.

### Neu
- **Echte Video-Clips**: Kollisionen werden jetzt als richtiges Video aufgenommen
  (H.264/AAC-MP4) statt als Einzelbilder — flüssige Wiedergabe statt Ruckeln.
- **Mit Ton**: Die Clips enthalten jetzt den Ton deines PCs (z. B. Spiel- und
  Teamradio-Audio) — du hörst die Kollision, nicht nur du siehst sie.
- **Korrekte Farben (HDR)**: Auf HDR-Bildschirmen sind die Clips nicht mehr
  überbelichtet — die Farben entsprechen dem, was du im Spiel siehst.

### Verbessert
- **Einstellbar**: In den Einstellungen → Kollisions-Clips kannst du die Bildrate
  (FPS 1–60) und das Audio-Gerät wählen.

### Bekannte Grenzen
- Die Aufnahme läuft über einen Puffer der letzten Sekunden — gespeichert werden
  nur Kollisionen (mit Vor- und Nachlauf), kein Dauer-Mitschnitt.
- Bei einem Bildschirm mit 30 Hz sind maximal 30 Bilder pro Sekunde möglich, auch
  wenn eine höhere Bildrate eingestellt ist.

## 0.6.0 — Beta (2026-09-07)

Die Sprachausgabe klingt jetzt richtig natürlich: Die Alerts spricht eine Microsoft
Neural-Stimme statt der alten, blechernen Desktop-Stimme. Du kannst zwischen mehreren
deutschen Stimmen wählen.

### Neu
- **Natürliche Stimmen (Neural)**: „Box, Box, Reifen sind durch", die Regen-Vorhersage
  und alle anderen Ansagen kommen jetzt von einer Microsoft-Neural-Stimme (wie beim
  Vorlesen in Edge) — deutlich angenehmer zu hören.
- **Stimme wählbar**: In den Einstellungen → Verhalten gibt es ein neues Feld
  „STIMME", in dem du zwischen mehreren deutschen Stimmen wählst (z. B. Katja, Conrad,
  Florian, Ingrid, Jonas). Das Feld ist frei editierbar — jede andere Stimme lässt sich
  eintippen.

### Verbessert
- **Fällt automatisch zurück**: Wenn gerade keine Internetverbindung besteht, spricht
  die App eine Ansage mit der lokalen Windows-Stimme (wie bisher) — die Alerts bleiben
  zuverlässig, nur online klingt es natürlich.

### Bekannte Grenzen
- Die Neural-Stimmen laufen über den freien Edge-Sprachdienst von Microsoft und
  brauchen eine Internetverbindung. Sollte der Dienst einmal nicht erreichbar sein,
  greift automatisch die lokale Windows-Stimme.

## 0.5.2 — Beta (2026-09-07)

Der Twitch-Login läuft jetzt komplett über den Server — kein lokales Browser-Fenster
mehr, und der Login kann nicht mehr durch einen doppelt gesendeten Code fehlschlagen.

### Neu
- **Login über den Server**: „Mit Twitch verbinden" startet den Login jetzt direkt über
  den Telemetrie-Server. Die App zeigt dir den Status an (erfolgreich / abgebrochen /
  fehlgeschlagen), sobald du die Freigabe im Browser bestätigt hast — das lokale
  localhost-Fenster entfällt komplett.

### Behoben
- **Twitch-Login „Invalid code" (endgültig)**: Twitch schickt den Login-Code manchmal
  zweimal hintereinander (bekannter Twitch-Bug). Der Server tauscht den Code jetzt
  genau einmal ein — der zweite, bereits verbrauchte Code kann den Login nicht mehr
  stören.

## 0.5.1 — Beta (2026-09-07)

Der Twitch-Login ist jetzt robuster: Ein alter Browser-Tab mit einem bereits
verbrauchten Login-Code kann den Login nicht mehr stören.

### Behoben
- **Twitch-Login „Invalid code"**: Wenn ein alter Browser-Tab (oder eine gecachte
  Weiterleitung) einen bereits verbrauchten Code an die App schickte, schlug der Login
  mit „Invalid code" fehl. Die App prüft jetzt ein Sicherheits-Token (`state`), das
  Twitch beim Login zurückschickt — nur der Code des aktuellen Login-Versuchs wird
  akzeptiert, alte werden verworfen.

## 0.5.0 — Beta (2026-09-07)

Der Twitch-Login ist jetzt viel einfacher: Ein Klick auf „Mit Twitch verbinden" genügt —
kein Client-ID/Client-Secret mehr eintragen.

### Neu
- **Einfacher Twitch-Login**: In den Einstellungen reicht ein Klick auf „Mit Twitch
  verbinden". Die App öffnet den Browser, du gibst Twitch die Freigabe — Kanal und
  Anzeigename werden automatisch erkannt. Die Client-ID ist fest in der App eingebaut,
  ein Client-Secret wird nicht mehr benötigt (PKCE).
- **Login über die eigene Domain**: Die Twitch-Freigabe läuft über
  `telemetrie.erdi-erc.de` (HTTPS) und wird sicher an die App auf deinem PC
  weitergeleitet.

## 0.4.2.1 — Beta (2026-09-07)

Updates laufen jetzt wie bei Discord: Es werden nur die geänderten Dateien geladen
(statt der kompletten Setup-Datei), im Hintergrund und ohne Administrator-Rechte —
und die App startet danach automatisch neu.

### Neu
- **Schnelle Updates (Delta)**: Beim Update werden nur noch die geänderten Dateien
  heruntergeladen (ein paar MB statt ~300 MB) und beim Beenden der App installiert.
  Die App startet danach automatisch neu.
- **Installation im Nutzerordner**: Die App wird künftig in den eigenen Nutzerordner
  installiert (`%LOCALAPPDATA%\Programs\ERCTelemetry`) — Updates brauchen keine
  Administrator-Rechte mehr. Bestehende Installationen wechseln beim ersten Update
  automatisch dorthin.
- **Firewall-Freigabe**: Die App legt die Firewall-Freigabe für die Telemetrie beim
  ersten Start selbst an (einmalige Rückfrage) — der Installer macht das nicht mehr.

### Verbessert
- **Update-Log-Button**: Im Setup-Tab ist der Button „Update-Log" jetzt aktiv und zeigt
  die Änderungen der aktuellen Version.
- **Robustere Updates**: Kaputte Update-Infos werden erkannt statt zu verwirrenden
  Fehlern zu führen; abgebrochene Downloads räumen ihre Reste auf.

## 0.4.2 — Beta (2026-09-07)

Telemetrie an andere Apps weiterleiten: RaceLab, SimHub & Co. bekommen die
Daten jetzt parallel auf demselben PC.

### Neu
- **Weiterleitung (andere Apps)**: In den Einstellungen lässt sich die Telemetrie an
  weitere Apps auf demselben PC weiterleiten (z. B. RaceLab, SimHub). Die App empfängt
  die Daten vom Spiel und schickt sie unverändert an bis zu 8 eigene Ziele
  (`127.0.0.1:Port`). Jedes Ziel lässt sich einzeln an- und abschalten.
- Die Weiterleitung ist standardmäßig aus — erst wer sie in den Einstellungen aktiviert,
  bekommt die Daten an andere Apps geschickt.

## 0.4.1 — Beta (2026-09-07)

Zwei History-Anzeigen korrigiert: nur deine eigenen Ereignisse, die echte
Rundenlänge — und keine Jahreszahl mehr hinter den Teamnamen.

### Behoben
- **Ereignisse in der Historie**: In der Rennhistory stehen (und im Report/Export)
  jetzt nur noch die Ereignisse, Strafen, Verwarnungen und Überholmanöver, die du
  selbst bekommen hast — nicht mehr die aller Fahrer (z. B. keine Safety-Car-Meldungen
  mehr, die dich nicht betreffen). Bei einer Kollision mit dir bist du weiterhin dabei.
- **Rundenlänge auf Session-Karten**: Ein 5-Runden-Rennen zeigt jetzt wieder 5 Runden
  statt 110 (22 Fahrer × 5). Es zählt die höchste Rundenzahl eines einzelnen Fahrers —
  auch wenn jemand vorzeitig ausgeschieden ist.
- **Teamnamen ohne Jahreszahl**: Hinter den Teamnamen in Historie, Report und Exporten
  steht keine „26" mehr — aus „Mercedes26" wird wieder „Mercedes".

## 0.4.0 — Beta (2026-09-07)

Komplett neuer F1-TV-Look: druckvoller, moderner — und im Dunkel-Modus ist jetzt
wirklich jeder Text lesbar.

### Neu
- **F1-TV-Broadcast-Design**: Zielflaggen-Streifen in Titelleiste und Footer,
  rot leuchtende Geschwindigkeits-Ziffern (Fahrer & Rivale), Glow hinter der
  Gang-Anzeige, glänzende THR/BRK/ERS-Balken und ein farbiger Auswahl-Hintergrund
  in der Navigationsleiste.

### Verbessert
- **Dark-Modus-Lesbarkeit**: Kleine Beschriftungen, Legenden und der Update-Log
  sind deutlich heller und überall lesbar.
- **Diagramme** (Runde A/B, Positionen): Grid-Linien und Referenzvergleiche sind
  jetzt sichtbar statt unsichtbar-dunkel.
- **In-Game-HUD**: Geschwindigkeits- und Statuswerte leuchtender, DRS und
  Blauflaggen besser lesbar, Widgets deckender gegen helle Spielfelder, hellere
  Laptop-Beschriftung — und der RPM-Balken glänzt im German-Schema im passenden
  Gold.

### Behoben
- Im HUD unsichtbarer Text (Zeitabstand, ungültige Runde) schwarz auf dunklem
  Grund → jetzt klar lesbar.

## 0.3.0 — Beta (2026-09-07)

Session teilen wie TrackTitan: Session-Daten und Kollisions-Clips über einen Link
im Browser anschauen.

### Neu
- **Session teilen**: Im History-Tab → Session-Detail gibt es „Session teilen…" — die App
  lädt Session-Daten und die Kollisions-Clips (MP4) auf den Share-Server hoch und kopiert
  den Link in die Zwischenablage.
- **Öffentliche Session-Seite**: Der Link öffnet im Browser eine dunkle Session-Seite
  (passend zu erdi-erc.de) mit Ergebnis-Tabelle und einem Video-Player pro Kollisions-Clip
  (mit Spulen/Seek).
- **Link-Format**: `https://telemetrie.erdi-erc.de/s/{uid}`

### Verbessert
- Upload mit Fortschrittsanzeige; schlägt ein Clip-Upload fehl, wird die Session auf dem
  Server wieder gelöscht (kein halber Upload).

### Bekannte Grenzen
- Geteilt werden Session-Daten und Kollisions-Clips; Ereignisse/Überholmanöver folgen in
  einer späteren Version.
- Der Link ist öffentlich — jeder mit dem Link kann die Session ansehen (wie TrackTitan).

## 0.2.1 — Beta (2026-09-05)

History-Tab komplett neu: Übersichtliche Session-Karten, Filter, Pagination
und die für Liga-Rennen zentrale Verwarnungs-/Strafen-Einsicht pro Runde und Kurve.

### Neu
- **Deine Sessions**: History-Tab (Nav 04) im neuen, modernen Design — 2-spaltiges
  Session-Kartenraster mit Streckenbild (44 Strecken, CC-BY-4.0 julesr0y/f1-circuits-svg)
  oder Monogramm-Kürzel-Fallback, Team-Farbe, großer Bestzeit-Anzeige, Runden-/Fahrerzahl,
  Datum und Status-Markierung (OFFEN/ABGEBROCHEN).
- **Filter + Pagination**: Strecke, Session-Typ und Zeitraum (Gesamt / 7 / 30 Tage)
  verengen die Karten live; Paginierung ‹/› + nummerierte Buttons (12 pro Seite).
- **Session-Detail**: Klick auf eine Karte öffnet das Detail mit allen bisherigen
  Bereichen (Ergebnisse & Pace, Positionen & Runden inkl. Speed-Trace mit Pedalen,
  Sitzungsvergleich, Ereignisse, Exporte, Report-Tab). „Zurück" bewahrt Filter + Seite.
- **Strafen & Verwarnungen pro Runde/Kurve** (Liga-Funktion): eigener Abschnitt im
  Session-Detail — gruppiert pro Runde, Typ-Badge (STRAFE rot / VERWARNUNG gold /
  ABKÜRZEN gold), Ort-Beschreibung wie „~Kurve 12 · Sektor 3 · 82 % · 4,1 km" und
  Zusammenfassung („3 Strafen · 5 Verwarnungen").
- **Verwarnungserfassung live**: Abkürz- und Gesamt-Verwarnungen werden aus dem
  UDP-Stream erfasst (Zähler-Anstieg = neue Verwarnung; Abnahme = Flashback wird
  ignoriert) und mit Runde + Distanz gestempelt.

### Verbessert
- Session-Karten werden aus der Datenbank gepaged (12/Seite) statt alle Sessions
  auf einmal zu laden.

### Bekannte Grenzen
- Motion/Telemetry/Setup-Daten gibt das Spiel nur für den eigenen Fahrer her → Teile des
  Reports (z. B. Trace-Duell) stehen nur für den Spieler voll zur Verfügung.
- Installierte 1.0.1-Instanzen bekommen 0.x nie als Update angeboten — einmal manuell
  über die Setup.exe installieren.