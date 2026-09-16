# ShareServer / OAuth / Security — Bug-Fix-Liste (Stand 2026-09-16)

Re-Hunt des Gebiets, dessen Agent beim letzten Shutdown unterbrochen wurde
(siehe `docs/BUGFIXES.md` Zeilen 11 + 87). Die Funde unten wurden ursprünglich als reiner
Findings-Report verifiziert und dokumentiert; **seit 2026-09-16 werden sie behoben.** Der
Fix-Status für jeden Fund steht in der Tabelle oben. Verifiziert durch Build (0 Warnungen/
0 Fehler) + 846 Tests (63 ShareServer, 783 Core), Stand nach der Code-Review-Runde unten
(fünfter Pass). Referenz für das beabsichtigte Sicherheitsverhalten sind die Tests in
`tests/ERCTelemetry.ShareServer.Tests/`.

Fokus laut Auftrag: OAuth (Discord/Twitch), AuthN/Z auf allen Endpoints, Path-Traversal
via `fileName`, Content-Type/Size-Limits, Rate-Limiting/Brute-Force, Info-Leaks in
Fehlermeldungen, Secrets im Quellcode, CSRF/Open-Redirect/Cookies/HTTPS, Header-
Injection, SQL-/Path-Injection über uid/token/name.

---

## Fix-Status (2026-09-16)

| # | Fund | Status |
|---|------|--------|
| HIGH 1 | Discord-Client-Secret eingecheckt (`appsettings.Development.json:4`) | **FIXT** — Secret aus Quellcode + Build-Artefakten entfernt, wird ausschließlich über Env-Var `Share__DiscordClientSecret` geladen. **Rotation im Developer Portal bewusst NICHT durchgeführt** — Auftraggeber-Waiver („müssen wir nicht machen"). |
| HIGH 2 | De-facto unauthentifiziertes Überschreiben/Löschen fremder Sessions | **FIXT** — `Program.cs:158/193`: POST über einer existierenden Session → 409 statt 200-with-overwrite; server-vergebene uid; DELETE ohne Berechtigung → 404. |
| HIGH 3 | Unbegrenzte Part-Größe, keine Quotas/Rate-Limits → Disk-DoS | **FIXT** — `Program.cs:20`: `MaxRequestBodySize = ClipUpload.MaxPartBytes`; `Program.cs:245/252`: Content-Length gegen erwartete Part-Länge; `Program.cs:100`: `UseRateLimiter` + `RequireRateLimiting("api")` auf allen Endpoints. |
| MEDIUM 1 | Rohe Exception-Meldungen landen als `status.message` | **FIXT** — Callback speichert nur die fixe Meldung `"Login fehlgeschlagen"`, Details nur per `logger.LogError(...)`; kein `ex.Message` mehr im Status-JSON. |
| MEDIUM 2 | Tokens hängen 15 min; öffentliches Share-Token schützt private Tokens | **FIXT** — Only-once-Delivery (bei `success` wird der Attempt per `TryRemove` entfernt); Status/Delete an den Nonce-Header `X-Login-Nonce` gebunden (403 bei fehlendem/falschem Nonce, konstantzeit-verglichen); TTL auf 5 min gesenkt. |
| MEDIUM 3 | Token-Ablauf bricht Chat stillschweigend; Refresh-Token ungenutzt | **FIXT (Minimal-Pfad)** — IRC-Numerics 463/464/465/466 → `AuthenticationFailed`-Event → App zeigt „Twitch-Token abgelaufen oder ungültig — bitte neu einloggen." und trennt. **Serverseitiger Refresh-/Revoke-Flow bleibt separates Feature** (hier nur die geforderte Minimal-Meldung). |
| LOW 1 | Kein HTTPS/HSTS/ForwardedHeaders auf dem Server | **FIXT (serverseitig)** — `Program.cs:92`: `UseForwardedHeaders` (X-Forwarded-Proto). HSTS/Redirect und Abschotten direkter Port-Exposition sind **externer Cloudflare/Operator-Schritt** (noch offen). |
| LOW 2 | `IsValidFileName` lässt Steuerzeichen zu | **FIXT** — `SharePaths.cs:34`: `!fileName.Any(char.IsControl)`; Tests decken NUL/Newline/Control-Byte ab. |
| LOW 3 | Race paralleler Callbacks überschreibt `success` mit `error` | **FIXT** — Success-Pfad schreibt bedingungslos; Error-Pfad liest den aktuellen Store erneut und schreibt nur, wenn `Status == "pending"`. Deterministischer Test: `Parallel_callbacks_do_not_clobber_a_success`. |

**Offen / extern:**
- Discord-Client-Secret-Rotation — vom Auftraggeber ausdrücklich gestrichen.
- Cloudflare-Edge: HSTS/Redirect + direkte Port-Exposition — Operator-Aufgabe.
- OAuth-Refresh-/Revoke-Endpoint — separates Feature, nicht Teil dieser Bug-Fix-Runde.

---

## HIGH — `src/ERCTelemetry.ShareServer/appsettings.Development.json:4` — Discord-Client-Secret im Quellcode eingecheckt

`"DiscordClientSecret": "_GpmDgO287Ej_jxKlUjERvGS_Nrso-lo"` (Zeile 4), dazu der
`DiscordClientId` in Zeile 3. Das Secret liegt **unverschlüsselt in der Source- und in der
Build-Ausgabe**:

- `src/ERCTelemetry.ShareServer/appsettings.Development.json`
- `installer/shareserver-stage/appsettings.Development.json` (identisch)
- `src/ERCTelemetry.ShareServer/bin/Debug|Release/net10.0/appsettings.Development.json`
  (wird beim Publish mitkopiert)

Der Server liest das Secret aus `Share:DiscordClientSecret` (`Program.cs:37/52`) und
verwendet es beim Code-Austausch (`Program.cs:455-457`). Ein eingechecktes Client-Secret
ist für jeden mit Repo-/Build-Zugriff lesbar und erlaubt (zusammen mit Client-Id und
Redirect-URI) OAuth-Codes für beliebige Nutzer einzulösen.

**Fix:** Das Secret aus dem VCS und aus allen Build-Artefakten entfernen; in **allen**
Umgebungen (auch Development) ausschließlich über Env-Var/Secret-Store laden
(`Share__DiscordClientSecret`); das jetzt verbrannte Secret im Discord Developer Portal
**rotieren** — es gilt als kompromittiert. Ggf. `.gitignore`-Regel für
`appsettings.Development.json` prüfen.

---

## HIGH — `src/ERCTelemetry.ShareServer/Program.cs:103-110` + `197-216` — De-facto unauthentifiziertes Überschreiben/Löschen fremder Sessions (öffentliches Token + frei wählbare SessionUid)

Die einzige AuthZ ist das Share-Token — und das ist **öffentlich**: `GET /api/config`
(`Program.cs:77`) liefert es unauthentifiziert aus, und `ShareConstants.DefaultToken`
schifft es in jede App-Binary. Die Tests bestätigen diesen „public by design“-Status
(`ShareServerTests.Config_endpoint_returns_the_current_token_without_auth`). Damit hat
**jeder**, der einen einzigen GET absetzt, Schreib- UND Löschzugriff:

- `POST /api/sessions` nimmt die `SessionUid` **aus dem Client-Manifest** und überschreibt
  eine evtl. existierende Session ohne Prüfung:
  ```csharp
  var dir = SharePaths.SessionDir(rootPath, manifest.SessionUid);
  Directory.CreateDirectory(dir);
  await File.WriteAllTextAsync(SharePaths.ManifestPath(rootPath, manifest.SessionUid), ...);
  ```
  Wer die uid einer fremden, veröffentlichten Session kennt (der Share-Link ist öffentlich),
  kann deren `manifest.json` mit einem eigenen Manifest überschreiben → die öffentliche
  Seite `/s/{uid}` wird gefälscht (Defacement), und per Clip-PUT (`Program.cs:141/160/178`)
  können die MP4s ersetzt werden.
- `DELETE /api/sessions/{uid}` (`Program.cs:197-216`) löscht jede Session mit bekanntem uid
  rekursiv (`Directory.Delete(dir, recursive: true)`).

`IsValidUid` wird im POST übrigens gar nicht aufgerufen (nur im PUT/DELETE) — ein
Traversal ist wegen `ulong` ausgeschlossen, aber die uid wird nicht gegen eine bestehende
Session / einen Besitzer geprüft.

**Fix:** Die `SessionUid` serverseitig erzeugen (z. B. aus dem manifest in eine
server-vergebene, unerratbare uid überführen) oder zumindest `POST /api/sessions` auf
nicht-existierende Sessions beschränken (409 bei existierender uid); DELETE nur für
eigene (durch einen beim POST vergebenen Secret-Key) Sessions erlauben. Falls das
öffentliche Token aus Produktgründen bleibt: die Write/Delete-Endpoints mit einem zweiten,
nicht-öffentlichen Mechanismus (z. B. HMAC-signierter uid) versehen.

---

## HIGH — `src/ERCTelemetry.ShareServer/Program.cs:14` + `150-191` — Unbegrenzte Part-Größe, keine Quotas/Rate-Limits → Disk-DoS auf dem einzigen geteilten Host

- Kestrel-Limit ist **200 MB pro Request** (`Program.cs:14`), die Chunk-Validierung
  (`Program.cs:150-158`) prüft nur `parts ≤ ClipUpload.MaxParts` und `0 ≤ part < parts`,
  aber **nie die Größe des Bodies**: die Client-Konstante `ClipUpload.MaxPartBytes` (80 MB)
  wird serverseitig nicht erzwungen. Jeder `.part`-File darf also bis zu 200 MB groß sein.
- Mit dem öffentlichen Token kann ein Angreifer pro Session bis zu **4096 Parts × 200 MB ≈
  800 GB** an `.partN`-Scratch-Files anlegen, ohne je den letzten Part zu senden — die
  Assembly-Files werden nur beim letzten Part gelöscht (`Program.cs:187-190`); nicht
  abgeschlossene Sessions hinterlassen die Parts dauerhaft (Cleanup greift erst nach
  `RetentionDays` über die Manifest-Mtime, `ShareCleanupService.cs:51-53`).
- Es gibt **kein Rate-Limiting** (kein `UseRateLimiter`, keinerlei `ConcurrentDictionary`-
  oder Request-Zähler in `Program.cs`), keine Sessions-/Clips-/Byte-Quota und keine
  Content-Length-Prüfung gegen die erwartete Part-Länge. Der Assembly-Loop
  (`Program.cs:178-185`) kann bei vielen Parts pro Request viele GB am Stück lesen.

**Fix:** Serverseitig `Content-Length` gegen die erwartete Part-Länge prüfen (und Teile
über `MaxPartBytes` ablehnen); eine Byte-/Session-/IP-Quota einführen; beim
Sitzungs-Level oder mit `PeriodicTimer` verwaiste `.part*`-Dateien aufräumen; Upload-
Endpoints ratelimitieren (auch nur die Login-Start/Status-Endpoints sind bisher
unlimitiert).

---

## MEDIUM — `src/ERCTelemetry.ShareServer/Program.cs:345` + `474` — rohe Exception-Meldungen landen als `status.message` beim Poller und in der App-UI (internes Info-Leak)

```csharp
twitchLogins[state] = attempt with { Status = "error", Error = ex.Message };
// bzw. discordLogins[state] = attempt with { Status = "error", Error = ex.Message };
```

`ex.Message` stammt aus den OAuth-Helfern und enthält **vollständige HTTP-Antwort-Bodies**:
`TwitchOAuth.cs:82` `throw new InvalidOperationException($"Twitch token exchange failed: {response.StatusCode} {json}");`, ebenso `TwitchOAuth.cs:111`, `DiscordOAuth.cs:80/109`. Dieser String wird
über `/twitch/login/status` (`Program.cs:376`) bzw. `/discord/login/status`
(`Program.cs:505`) an den Poller ausgeliefert und in der App dem Nutzer präsentiert
(`TwitchChatHost.cs:118`, `DiscordLoginHost.cs:125`: `"Login fehlgeschlagen: {status.Error}"`)
sowie zusätzlich geloggt (`TwitchChatHost.cs:115/134`). Bei Proxy-/Edge-Fehlern können so
interne HTML-Seiten/Server-Infos (und im Extremfall Echo-Token-Anteile) nach außen gelangen.

**Fix:** Im Callback nur eine fixe, nutzersichere Meldung (`"Login fehlgeschlagen"`,
Details ausschließlich per `logger.LogError(...)`) speichern; `Error` nicht im Status-JSON
zurückgeben oder nur einen kurzen Fehlercode.

---

## MEDIUM — `src/ERCTelemetry.ShareServer/Program.cs:331-337` + `460-466` + `369-374` + `498-503` — OAuth-Access-/Refresh-Tokens hängen 15 min im In-Memory-Store und hängen an ausschließlich dem öffentlich bekannten Share-Token

Der Server speichert die echten OAuth-Credentials (Twitch `chat:read`/`chat:edit`,
Discord `identify`/`guilds`/`guilds.members.read` **inkl. Refresh-Token**) pro `state` im
`ConcurrentDictionary` (`Program.cs:62-65`) und liefert sie über den
`/…/login/status`-Endpoint aus:

```csharp
"success" => Results.Ok(new { status = "success", token = attempt.Token, refreshToken = attempt.RefreshToken, user = ... }),
```

Geschützt ist dieser Endpoint **nur durch das per `/api/config` öffentlich auslesbare
Share-Token** (`Program.cs:355-359`). Die einzige echte Barriere zum Abgreifen der Tokens
ist damit die Zustands-Entropie (128-Bit-Guid) — praktisch nicht brute-forcebar (und kein
Rate-Limit), aber die Sicherheit der Discord-/Twitch-Credentials steht auf einem Wert, der
zusätzlich in der **Browser-URL** des Autorisierungsflusses sichtbar ist und über den
App→Server-Kanal transportiert wird. Design-Kopplung: „öffentlicher“ Upload-Token schützt
„private“ OAuth-Tokens.

**Fix:** Tokens nur **einmal** an den Poller ausliefern (nach dem ersten erfolgreichen
Poll löschen), status-Endpoint zusätzlich an einen vom App-Start generierten,
nicht-öffentlichen Nachweis binden (oder das Ergebnis verschlüsselt/HPKE an den
App-Start zurückgeben) und TTL der Attempts auf das Notwendige (z. B. 5 min) senken.

---

## MEDIUM — `src/ERCTelemetry.App/Composition/TwitchChatHost.cs:104-109` — Refresh-Tokens werden geholt, aber nirgends verwendet; Ablauf des Twitch-Access-Tokens bricht den Chat stillschweigend

Der Server holt und speichert `RefreshToken` (`Program.cs:334-336`, `464-466`), der App-
Host speichert beim Poll **nur** den Access-Token:

```csharp
_settings.Update(settings => settings with { TwitchToken = status.Token, TwitchChannel = status.User?.Login, TwitchEnabled = true });
```

`RefreshToken` wird im gesamten Repo **nirgends konsumiert** (per `grep` verifiziert:
einzige Vorkommen sind das Parsen in `TwitchOAuth.cs:89`/`DiscordOAuth.cs:87` und das
Speichern in den Attempt-Records). Es gibt keinen Refresh-Flow, keine
Ablauf-Erkennung und kein Revoke beim Logout (`DiscordLoginHost.Logout` löscht nur den
RAM-Token). Ein abgelaufener Twitch-Access-Token führt dazu, dass der IRC-Login
(`TwitchChatHost.ConnectAsync`) stillschweigend scheitert, bis der Streamer sich erneut
einloggt — ein „expiry/refresh race“, das der Auftrag explizit abfragt.

**Fix:** Einen serverseitigen Refresh-Endpoint ergänzen (oder im App-Host den
`RefreshToken` halten und gegen `/token` mit `grant_type=refresh_token` austauschen);
beim Logout/Neustart Tokens server-seitig revoken. Mindestens: bei IRC-Verbindungsfehler
eine aussagekräftige „Token abgelaufen → neu einloggen“-Meldung statt eines generischen
Fehlers.

---

## LOW — `src/ERCTelemetry.Core/Settings/AppSettingsStore.cs:45-55` — Twitch-OAuth-Access-Token im Klartext in `settings.json` persistiert

`AppSettingsStore.Save` schreibt das komplette `AppSettings` (inkl. `TwitchToken`,
`AppSettings.cs:412`) unverschlüsselt als JSON nach
`%LOCALAPPDATA%\ERCTelemetry\settings.json` (`AppSettingsStore.cs:20-22`). Der Twitch-
Access-Token (Scopes `chat:read`/`chat:edit`) ruht damit im Klartext auf der Platte; kein
DPAPI/Windows-Credential-Manager. Der Discord-Token bleibt dagegen bewusst im RAM
(`DiscordLoginHost.cs:64-74`). Access-Tokens sind kurzlebig, aber ein geloggter
(Plaintext-)Token einer früheren Session reicht zusammen mit einem evtl. in die Env/Logs
leckenden Refresh-Token für eine Session-Hijack.

**Fix:** `TwitchToken` (wie beim Discord-Token) im RAM halten oder mit DPAPI
(`ProtectedData`) verschlüsselt persistieren; beim Logout auch die Persistence löschen.

---

## LOW — `src/ERCTelemetry.ShareServer/Program.cs:28-52` — kein HTTPS-Redirect/HSTS auf dem Server selbst; Klartext-Hop für OAuth-Codes/Tokens [unverified]

`Program.cs` enthält weder `UseHttpsRedirection` noch `UseHsts` noch
`ForwardedHeaders`; der Kestrel-Server liefert HTTP aus und verlässt sich komplett auf den
Cloudflare-Edge vor `telemetrie.erdi-erc.de`. Solange der Server nur hinter dem Edge
erreichbar ist, ist das ok ([unverified]: ob der Server auch direkt (z. B. Port 80/5000
offen) erreichbar ist, lässt sich aus dem Repo nicht belegen). Ist er direkt erreichbar,
fließen die OAuth-Authorization-Codes und die per `/login/status` ausgelieferten
Access-/Refresh-Tokens im Klartext über das Netz (das Share-Token selbst ist zwar
öffentlich, die OAuth-Tokens sind es nicht).

**Fix:** `app.UseForwardedHeaders` (X-Forwarded-Proto von Cloudflare) + optional
HSTS/Redirect auf der Edge-Konfiguration sicherstellen; direkte Port-Exposition
unterbinden.

---

## LOW — `src/ERCTelemetry.Core/Share/SharePaths.cs:29-33` — `IsValidFileName` lässt Steuerzeichen zu → kaputte `Location`-Header / 500s; CRLF-Splitting [unverified]

```csharp
public static bool IsValidFileName(string fileName) =>
    fileName.StartsWith("clip-", StringComparison.Ordinal) &&
    fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
    fileName.IndexOfAny(['/', '\\']) < 0 &&
    fileName.IndexOf("..", StringComparison.Ordinal) < 0;
```

Die Prüfung blockiert `/`, `\`, `..` — aber **keine Steuerzeichen** wie `\r`/`\n` oder
Leerzeichen. Ein `fileName` mit CRLF besteht die Validierung (Datei landet als
`clip-…\r\n….mp4` auf der Platte) und wird in den `Location`-Header von
`Results.Created($"/s/{uid}/clips/{fileName}", null)` (`Program.cs:146/193`) eingesetzt.
Kestrel lehnt CR/LF in Header-Werten üblicherweise mit einer Exception ab → der Request
endet in einem unhandled 500 (DoS-Flavor) statt eines sauberen 400. Ob ein echtes
Header-Splitting möglich ist, konnte ich ohne Laufzeitcheck nicht bestätigen
**[unverified]** — die fehlende Steuerzeichen-Validierung ist aber am Code verifiziert.

**Fix:** In `IsValidFileName` zusätzlich Steuerzeichen (`char.IsControl`) ablehnen und den
`Location`-Wert mit `Uri.EscapeDataString`/`Path.GetFileName` normalisieren.

---

## Sauber geprüft (keine Bugs)

- **Path-Traversal über `fileName`/`uid`:** Der Client encoded zwar mit
  `Uri.EscapeDataString` (`ShareService.cs:146-147`), aber der Server **re-validiert**
  serverseitig in jedem Pfad: PUT (`Program.cs:126`), GET-Clip (`Program.cs:254`),
  GET-Page/DELETE (`Program.cs:222/204`) via `IsValidUid`/`IsValidFileName`. `%2f`- und
  `..`-Varianten sind damit abgedeckt (Tests: `Path_traversal_in_file_name_is_rejected`).
- **XSS auf der öffentlichen Session-Seite:** `SharePageRenderer` escaped **alle**
  Nutzer-Strings (`Esc(...)` auf Track/SessionType/Namen/Teams/Status/Dateinamen,
  `SharePageRenderer.cs:42-57`/`103-106`); numerische Felder sind typisiert. Die
  Callback-`LoginPage`-Meldungen sind statisch, kein Nutzer-Input landet im HTML.
- **Konstantzeit-Tokenvergleich:** `Program.FixedTimeEquals` (Längen-Check + 
  `CryptographicOperations.FixedTimeEquals`) — kein Early-Exit bei falschem ersten Byte.
- **CSRF auf den mutierenden Endpoints:** Alle `/api/*`- und `/login/*`-Mutationen
  verlangen den benutzerdefinierten `X-Share-Token`-Header; Formulare/Cross-Site-Requests
  können custom Header nicht setzen, CORS ist nicht aktiviert.
- **Open Redirects:** Es gibt keinen einzigen Redirect-Endpoint (nur 200-HTML-Seiten);
  kein `returnUrl`/`next`.
- **Cookies:** Der Server setzt keine Cookies → keine Cookie-Flag-/SameSite-Fragen.
- **SQL-Injection:** Der ShareServer führt **kein SQL**; Persistenz ist reines
  Dateisystem (manifest.json + MP4s), uid ist `ulong`-typisiert, der Cleanup-Service
  löscht nur Unterverzeichnisse des Roots.
- **OAuth-Doppel-Redirect / Single-Exchange:** Der „nur einmal tauschen“-Guard
  (`Program.cs:316-322`/`445-451`) ist Tests-gestützt korrekt
  (`Callback_double_redirect_exchanges_only_once`); unbekannte `state`-Werte werden
  sauber abgewiesen (`Callback_with_unknown_state_shows_error_page`).
- **State→Attempt-Bindung:** `POST /twitch|discord/login/start` und die
  Status-/Delete-Endpoints sind token-geschützt; die Callbacks prüfen die Existenz des
  `state`.

---

## Ergänzung (2026-09-16, zweiter Verifikations-Pass)

Zusätzlicher Fund aus dem Nachprüf-Pass gegen den OAuth-Callback. Alle 9 Funde oben
bleiben unverändert gültig; dieser kommt hinzu:

### LOW — `src/ERCTelemetry.ShareServer/Program.cs:316-345` (+ analog `418-478` Discord) — Race bei parallelen Callbacks: „success“ wird durch den Verlierer-`catch` zu „error“ überschrieben

Der Doppel-Redirect-Guard (`Program.cs:316-322`, analog Discord `445-451`) prüft den
**vor** den `await`-Punkten gelesenen `attempt`-Snapshot (Zeile 291 bzw. 420). Zwei
**echt parallele** Callback-GETs mit gleichem `code`+`state` passieren beide den Guard
(beide sehen `pending`), beide tauschen den Code (`ExchangeCodeAsync`/`GetUserAsync`,
`Program.cs:326-330`). Der zweite Austausch erhält von Twitch/Discord
`invalid code` (bereits eingelöst) → `catch` (`Program.cs:342-345`) schreibt
`Status = "error"` **über den bereits gespeicherten Erfolg** (`Program.cs:331-337`):

```csharp
catch (Exception ex)
{
    logger.LogError(ex, "Twitch login exchange failed for state {State}", state);
    twitchLogins[state] = attempt with { Status = "error", Error = ex.Message };  // überschreibt success
}
```

Die App pollt dann `status = "error"` und meldet „Login fehlgeschlagen“, obwohl der Login
serverseitig erfolgreich abgeschlossen wurde. Das sequenzielle Doppel-Redirect-Fenster ist
test-abgedeckt (`Callback_double_redirect_exchanges_only_once`), das parallele nicht. Kein
Credential-Diebstahl (der verlierende Austausch scheitert client-seitig), daher nur LOW.

**Fix:** Den letzten bekannten Status pro `state` beim Schreiben mergen statt blind zu
ersetzen, z. B. erst nach dem Austausch erneut lesen und nur überschreiben, wenn der
gespeicherte Versuch noch `pending` ist (`TryGetValue` → `Status == "pending"` →
dann erst schreiben); oder pro `state` das Schreiben serialisieren (Lock).

---

## Review-Fix-Runde (2026-09-16, dritter Pass — Code-Review + Security-Review)

Zusätzliche Funde aus dem Post-Fix-Review (C#-Code-Reviewer + Security-Reviewer) über den
Stand der beiden Fix-Runden oben. Die 10 Funde davor bleiben gültig; die folgenden wurden
ergänzt, implementiert und per Test verifiziert:

| # | Fund | Status |
|---|------|--------|
| H1 | Byte-Budget-Bypass: die Part-Längen-Prüfung zählte `ContentLength ?? 0` — ein chunked Body ohne Content-Length (bzw. mit lügendem Kopf) passierte als „0 Bytes“ unlimitiert → Disk-DoS | **FIXT** — `ClipUpload.CopyWithinBudgetAsync` (`src/ERCTelemetry.Core/Share/ClipUpload.cs`) streamt den Body und zählt die tatsächlich geschriebenen Bytes; bricht bei Überschreitung ab (413, Teil-Datei per `TryDelete` entfernt). `ClipUpload.TryClaimSessionBytes` verrechnet per Compare-and-Swap gegen ein `ConcurrentDictionary<ulong,long>`, sodass auch **parallele** Part-PUTs derselben Session das Gesamtbudget nicht überziehen können. Budget konfigurierbar über `Share__LimitSessionBytes` (Default `ClipUpload.MaxSessionBytes` = 2 GiB). Tests: neue `UploadBudgetTests` (u. a. chunked Body ohne Content-Length → 413, abgelehnte Bytes werden dem Budget nicht angerechnet) + `ClipUploadTests`-Budget-Fälle. |
| M1 | Discord-Code nicht an eine Challenge gebunden (kein PKCE) → ein zwischengezogener Authorization-Code wäre ohne Wissen des auslösenden Clients einlösbar | **FIXT** — RFC 7636 PKCE: `DiscordOAuth.NewCodeVerifier()` (43-char base64url, 32 Zufallsbytes) + `CodeChallenge()` (base64url SHA-256). `Program.cs` legt den Verifier auf dem `DiscordLoginAttempt.CodeVerifier` ab, schickt die S256-Challenge in die Authorize-URL und reicht `code_verifier` beim Code-Austausch mit. **Twitch unterstützt kein PKCE** (residual dokumentiert — dort bleibt die State-Geheimhaltung Pflicht). Tests: `DiscordOAuthTests` (inkl. RFC-7636-Testvektor), `DiscordLoginTests.Start_returns_authorize_url_state_and_nonce` + Stub weist Exchange ohne `code_verifier` ab. |
| M2 | Roh-Provider-`error` aus dem Direkt-Fehlerpfad (`/…/callback?error=…`) durfte als `status.message` über den Status-Poll an die App zurückfließen | **FIXT** — beide direkten `?error=`-Pfade whitelisten: `access_denied` → `access_denied`, jeder andere Wert → fixe Meldung `"Login fehlgeschlagen"`; roher Provider-Input/`ex.Message` landet in keinem Attempt-Feld mehr. Tests: `Callback_with_arbitrary_error_does_not_echo_raw_provider_input` (Twitch + Discord, mit `<script>`-Payload). |
| M1-error | Ergänzung zu LOW 3: ein **nach** dem Erfolg eintreffender Error-Callback (zweiter Provider-Redirect) konnte den stored `success` überschreiben | **FIXT** — Fehlerpfad liest den Attempt vor dem Schreiben erneut und überschreibt nur, wenn `Status == "pending"`. Tests: `Callback_error_after_a_success_does_not_clobber_the_result` (beide Provider). |
| L1 (App) | Twitch-Access-Token weiterhin im Klartext in `%LOCALAPPDATA%\ERCTelemetry\settings.json` | **FIXT** — einmalige Migration beim `AppSettingsService`-Aufbau: vorhandene, unverschlüsselte Tokens werden einmal per DPAPI (`ProtectedData.DataProtectionScope.CurrentUser`, Marker `dpapi:` — jetzt `AppSettingsStore.EncryptedPrefix`) verschlüsselt persistiert; der Marker verhindert erneute (Doppel-)Verschlüsselung. `CryptographicException` zusätzlich im `Update`-Catch-Filter. |
| L3 | `IsValidFileName` ließ Windows-invalide Zeichen (`: * ? " < > |`) und Trailing-Dot/-Space durch → Pfad-Ambiguität / ADS | **FIXT** — `SharePaths.IsValidFileName` lehnt zusätzlich `':'`, `'*'`, `'?'`, `'"'`, `'<'`, `'>'`, `'|'` und Dateinamen mit führendem/abhängigem Trailing-`.`/` ` ab; 9 neue `SharePathsTests`-Fälle. |
| L4 | Twitch verweigert den Login teils per `NOTICE * :Login authentication failed` statt per Numeric 463–466 → `AuthenticationFailed`-Meldung blieb aus | **FIXT** — Parser kennt jetzt `IrcNotice(Target, Text)`; `TwitchIrcClient` feuert `AuthenticationFailed` für NOTICE mit Target `*`/leer und Text „Login authentication failed“/„Incorrect Password“. Tests: `IrcMessageParserTests` (NOTICE-Fälle) + `TwitchIrcClient`-Coverage. |
| C#① | `IrcMessageParser`: `int.Parse` auf Zeichen, die `char.IsDigit` bejaht — Vollbreite-Unicode-Ziffern (`４６４`) warfen → Verstoß gegen die Never-Throws-Garantie, IRC-Leseschleife stirbt | **FIXT** — Numerik-Erkennung ASCII-only (`All(c => c is >= '0' and <= '9')`), `int.Parse` mit `CultureInfo.InvariantCulture`. Test: `Non_ascii_digit_numeric_command_is_unknown_not_throws`. |
| C#③ | `AppSettingsService.Update`: nicht gefangene `CryptographicException` (DPAPI-Entschlüsselung korrupter/legacy-Bestände) → Absturz beim Sichern | **FIXT** — Catch-Filter um `CryptographicException` (neben `IOException`/`UnauthorizedAccessException`/`ArgumentException`) erweitert. |

Referenz-Tests für die Runde: `UploadBudgetTests`, `ClipUploadTests`, `SharePathsTests`,
`DiscordOAuthTests`, `IrcMessageParserTests` (Core) sowie die ergänzten M2-/PKCE-/Error-After-
Success-Tests in `DiscordLoginTests`/`TwitchLoginTests` (ShareServer). Build 0 Warnungen/
0 Fehler, **819 Tests grün** (54 ShareServer, 765 Core). Die ShareServer-Testklassen laufen
seit dieser Runde in einer gemeinsamen `[Collection("ShareServer")]`, damit die prozess- 
globalen Env-Vars der Fixtures (`Share__LimitSessionBytes` u. a.) nicht zwischen Klassen
paralleler Races auslösen.

---
## Security-Review-Runde (2026-09-16, vierter Pass — Security-Reviewer)

Post-Fix-Security-Review über den Stand der drei Runden oben. Die 19 Funde davor bleiben
gültig; die folgenden wurden ergänzt, implementiert und per Test verifiziert (bis auf den
als residual dokumentierten LOW 5):

| # | Fund | Status |
|---|------|--------|
| MEDIUM-1 | Disk-DoS-Rest: die H1-Session-Quota begrenzt nur die Platte **pro Session** — ein Halter des öffentlichen Tokens eröffnet unbegrenzt viele Sessions, jede mit eigenem `MaxSessionBytes`-Budget → Gesamtplatte unbegrenzt | **FIXT** — Server-globales Budget `ClipUpload.MaxGlobalBytes` (20 GiB, Default; konfigurierbar über `Share__LimitGlobalBytes`). `ClipUpload.TryClaimGlobalBytes`/`ReleaseGlobalBytes` (CAS/`Interlocked`, identisches Muster zur Session-Quota). `Program.cs`-`ClaimBytes` claimt zuerst **global**, bei Session-Fehler Rückbuchung; `ReleaseSession` (DELETE + POST-Reconcile) gibt die Bytes ans globale Budget zurück. Tests: neue `ClipUploadTests`-Fälle (Akkumulation, Ablehnung über Cap, Release, parallel nie über Cap) + neue `GlobalBudgetTests` (zweite Session nach ausgeschöpfter Global-Quota → 413 + keine Datei; DELETE setzt die Quota wieder frei). |
| LOW 3-Erw | `ErcApiKey`/`LlmApiKey` ruhten weiter im Klartext in `%LOCALAPPDATA%\ERCTelemetry\settings.json` — die L1-DPAPI galt nur dem `TwitchToken` | **FIXT** — DPAPI auf **alle drei** Secret-Felder erweitert: `AppSettingsStore.EncryptSecret`/`DecryptSecret` statt der Twitch-only-Funktionen. Einmal-Migration via `NeedsSecretMigration(path)` (roh von der Platte über `JsonDocument` — der In-Memory-Wert ist immer entschlüsselt und könnte nie als Diskriminator dienen) + `AppSettingsService`-Aufbau migriert, wenn ein Secret den `dpapi:`-Marker noch nicht trägt. Tests: `AppSettingsTests` (Migration für jedes Secret, kein Klartext auf Windows, Save→Load-Roundtrip, Legacy-Plus-Encrypt-Pfad). |
| LOW 4 | Nach externem Cleanup (Retention-Sweep) bleibt die Session-uid in-memory belegt → Re-POST derselben Game-Session antwortet 409, bis der Server neu startet | **FIXT** — POST-Reconcile vor dem 409-Check: `sessionOwners.ContainsKey(uid) && !File.Exists(manifestPath)` → `ReleaseSession(uid)` (entfernt Owner + Byte-Konten, gibt Global-Freigabe zurück) → anschließend 201 statt 409. Test: `Repost_of_a_session_whose_files_were_swept_creates_it_afresh` (inkl. frischem Owner-Secret). |
| LOW 5 | Session-UID-Squatting: die uid kommt aus dem Client-Manifest — ein Angreifer kann eine beabsichtigte uid vorab belegen (Denial-of-Share) | **Residual (dokumentiert)** — Fix = server-vergebene (d. h. nicht vom Client wählbare) uids; das ist ein Client-Vertrag-/Protokoll-Change, kein Bugfix dieser Runde. |
| LOW 6 | Roher `ex.Message` aus den OAuth-Catch-Sites in `logger.LogError` — Log-Injection-Risiko bei steuerbarem Provider-/Header-Input | **FIXT** — `SanitizeLogText`: entfernt Steuerzeichen (v. a. CRLF) und kappt bei 1024 Zeichen; an beiden Catch-Sites (`Twitch`/`Discord`-Exchange) verwendet, Meldung bleibt parameter-gebunden (`{State}`/`{Error}`). |
| LOW-Note | 400-Body echote die `part`/`parts`-Query-Werte (reflected Input) zurück | **FIXT** — fixe Meldung `"Invalid part range."` statt Echo. Test: `Chunked_upload_rejects_invalid_part_range` prüft jetzt zusätzlich den **body** == Fixmeldung (6 Fälle, u. a. nicht-numerisch/`.`,`<script>`) und damit das Nicht-Echo. |

Referenz-Tests für die Runde: `ClipUploadTests`, `AppSettingsTests` (Core) sowie
`GlobalBudgetTests` + die LOW-4-/LOW-Note-Ergänzungen in `ShareServerTests` (ShareServer).
Die `GlobalBudgetTests` bauen bewusst **pro Test eine eigene Fixture/Server** auf — die Tests
treiben die globale Quota aktiv an ihren Cap und dürfen diesen Zustand nicht in den nächsten
Test leaken (die Prozess-env-`Share__*`-Vars bleiben über die `[Collection("ShareServer")]`-
Serialisierung abgesichert). Build 0 Warnungen/0 Fehler, **834 Tests grün**
(57 ShareServer, 777 Core).

---

## Code-Review-Runde (2026-09-16, fünfter Pass — Code-Reviewer/Accountability)

Post-Fix-Code-Review über den Stand der vier Runden. Die 19+6 Funde bleiben gültig; der
Reviewer fand zusätzlich vier Accountability-Defekte an dem **Byte-Budget-Accounting**, das
die 4. Runde einführte. Alle vier wurden implementiert und per Test verifiziert:

| # | Fund | Status |
|---|------|--------|
| HIGH | Retention-Sweep gibt Bytes **nicht** an die Budgets zurück → Per-Session- und Global-Quota driften dauerhaft auf Exhaustion (bis Server-Neustart) — der Sweep löscht nur Dateien, toucht das In-Memory-Accounting nie (auch: Sweep-Reaps von Part-Dateien einer **lebenden** Session verloren) | **FIXT** — `ShareCleanupService` bekommt zwei Callbacks: `onOrphanPartReaped(uid, bytes)` pro abgeräumter `.part*`-Datei (Größe vor dem Delete gemessen) und `onSessionReleased(uid)` pro abgelaufener Session (uid per Verzeichnisname geparst); `CleanupOnce` ist jetzt `internal` (Tests fahren einen Sweep synchron statt auf den Stunden-Timer zu warten, `InternalsVisibleTo` ergänzt). `Program.cs` verdrahtet beide auf `RefundOrphanPart` + `ReleaseSession`: Session-Refund über neues `ClipUpload.TryReleaseSessionBytes` (CAS-Ledger wie das Claim; Eintrag bei Volldeckung entfernt), und `RefundOrphanPart` gibt **genau den Betrag** an den Global-Zähler zurück, den der Session-Refund wirklich freigegeben hat. **Nachfassung aus dem Review des Fixes (0-Eintrag-Doppelrelease):** der POST-Re-Create setzt den Session-Eintrag auf 0, während die alten Part-Dateien noch auf Platte liegen — ein späterer Sweep-Reap hätte gegen den 0-Eintrag vorher `true` gemeldet und dieselben (bereits vom Reconcile rückgebuchten) Bytes ein zweites Mal global freigegeben; wiederholt trieb das den Global-Zähler in den Negativen, womit `TryClaimGlobalBytes` das Cap still aushebelt. Fix: `TryReleaseSessionBytes` liefert jetzt die tatsächlich freigegebenen Bytes (`claimed` bei Volldeckung, **nie mehr**) — der 0-Eintrag liefert 0, also bucht `RefundOrphanPart` nichts zurück. Tests: `ClipUploadTests` (Teil-Refund, Volldeckung entfernt Eintrag, fehlende Session → 0, Session auf 0 zurückgesetzt → 0, Concurrent-Ledger nie negativ) + neue `CleanupTests` (Orphan-Part abgeräumt+erstattet, frische Part unberührt, abgelaufene Session einmal freigegeben, abgelaufene Session mit Orphan-Part, Nicht-uid-Verzeichnis ohne Callbacks). |
| MEDIUM | Claim vs. DELETE-Race strangt globale Bytes: ein Clip-PUT, der den Owner-Check **vor** einem konkurrierenden DELETE passierte, kann seine Byte-Claim danach noch landen → `sessionBytes[uid]` ungleich 0 ohne Owner, das nie freigegeben wird; der POST-Re-Create überschrieb den Zähler mit 0 **ohne Rückbuchung** → Leak | **FIXT** — POST-Create bucht nach dem Owner-Insert jede aktuell strande Residue von `sessionBytes[uid]` an den Global-Zähler zurück (`TryRemove` + `ReleaseGlobalBytes`), **dann** `TryAdd(uid, 0)` — der Indexer-Assignment wurde durch `TryAdd` ersetzt, damit kein frisch gelandeter Claim der neuen Live-Session mit 0 zerschlagen wird. |
| LOW | Integer-Overflow in den Claim-Guards: `used + add > max` kann bei operator-gesetzten Caps nahe `long.MaxValue` negativ wrappen | **FIXT** — in `TryClaimSessionBytes` und `TryClaimGlobalBytes` als `current > max \|\| add > max - current` (Subtraktionsform) umgeschrieben, mit Kommentar. |
| LOW | POST-TOCTOU: zwei gleichzeitige POSTs derselben uid → Last-Writer-Win im Indexer-Assignment; der Owner-Secret des Ersten verliert still | **FIXT** — `sessionOwners.TryAdd` statt `sessionOwners[uid] = …`; der Verlierer bekommt 409 (sein Manifest-Write war idempotent — dieselbe Game-Session). |
| LOW | Sweep bricht den ganzen Stunden-Lauf ab, wenn ein Verzeichnis mitten im Durchlauf verschwindet (`DirectoryNotFoundException`/`IOException` aus `EnumerateFiles`/`Delete`/`GetLastWriteTimeUtc` unter konkurrierendem DELETE) → alle restlichen Sessions bleiben diese Stunde liegen | **FIXT** — pro-Verzeichnis-`try/catch (IOException, UnauthorizedAccessException)`: ein einzelnes verschwundenes Verzeichnis wird protokolliert und übersprungen, der Lauf geht weiter. |

Referenz-Tests der Runde: `CleanupTests` + `ClipUploadTests`-`TryReleaseSessionBytes`-Fälle
(ShareServer/Core). Build 0 Warnungen/0 Fehler, **846 Tests grün** (63 ShareServer, 783 Core).

---

## Zählung

- **HIGH:** 3 — alle 3 FIXT
- **MEDIUM:** 4 — MEDIUM 1/2 FIXT, MEDIUM 3 Minimal-Pfad FIXT (Refresh-Flow separat)
- **LOW:** 2 (Hauptreport) + 1 (Ergänzung) — LOW 1 serverseitig FIXT (Edge extern), LOW 2 + LOW 3 FIXT
- **Review-Fix-Runde (3. Pass):** 9 Funde (H1, M1, M1-error, M2, L1-App, L3, L4, C#①, C#③) — alle FIXT
- **Security-Review-Runde (4. Pass):** 6 Funde (MEDIUM-1, LOW-3-Erweiterung, LOW 4, LOW 5, LOW 6, LOW-Note) — 5 FIXT, LOW 5 residual (server-vergebene uids = Client-Vertragsänderung)
- **Code-Review-Runde (5. Pass):** 5 Funde (HIGH Sweep-Refund, MEDIUM Claim/Delete-Race, LOW Overflow, LOW POST-TOCTOU, LOW Sweep-Robustheit) — alle FIXT
- **Gesamt Funde:** 30
- **Code geändert?** Ja — Fix-Runden 2026-09-16 (siehe Fix-Status-Tabellen oben).
