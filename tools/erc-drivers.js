/**
 * ERCTelemetry — ErdiHub-Fahrer-Datenholer
 *
 * Holt Fahrerdaten von erdi-erc.de und schreibt sie als JSON für die
 * Overlay-Seiten (grid.html, championship.html, lower-thirds.html):
 *
 *   node tools/erc-drivers.js              # alles neu holen
 *   node tools/erc-drivers.js --frisch     # Cache ignorieren
 *   node tools/erc-drivers.js --ziel X     # JSON nach X schreiben (App-Start-Hook)
 *   node tools/erc-drivers.js --legacy     # HTML-Scraping erzwingen (Fallback)
 *
 * Weg 1 (Standard): /api/telemetry/standings — ein Request mit Fahrer- +
 * Teamwertung direkt aus der Website-DB (autoritativ: Positionen, echte
 * Konstrukteurs-Punkte inkl. Reserve/Gast-Logik).
 * Weg 2 (Fallback, wenn die API fehlt): /Community/Teams -> 10 Team-Seiten ->
 * /Profile/<discord-id> — HTML-Scraping wie früher (Punkte aus der
 * Saison-Bilanz der Profile, Teamwertung muss das Overlay selbst summieren).
 *
 * Ausgabe: wwwroot/overlay/data/erc-drivers.json (+ Release-Kopie, falls vorhanden)
 */
'use strict';
const http = require('http');
const https = require('https');
const fs = require('fs');
const path = require('path');

const BASIS = 'https://erdi-erc.de';
const ABFRAGE_MS = 400;        // höflich bleiben
const CACHE_STUNDEN = 6;       // --frisch ignoriert das
const ZIEL = path.join(__dirname, '..', 'src', 'ERCTelemetry.App', 'wwwroot', 'overlay', 'data', 'erc-drivers.json');

function hole(pfad) {
  return new Promise((resolve, ablehnen) => {
    const m = https.get(BASIS + pfad, {
      headers: { 'User-Agent': 'Mozilla/5.0 (ERCTelemetry Overlay; lokal)', Accept: 'text/html' },
      timeout: 15000,
    }, res => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        res.resume();
        return resolve(hole(res.headers.location));
      }
      if (res.statusCode !== 200) { res.resume(); return ablehnen(new Error(pfad + ' -> HTTP ' + res.statusCode)); }
      let d = '';
      res.setEncoding('utf8');
      res.on('data', c => { d += c; });
      res.on('end', () => resolve(d));
    });
    m.on('timeout', () => m.destroy(new Error('Timeout ' + pfad)));
    m.on('error', ablehnen);
  });
}

// JSON-Antwort von der Standings-API holen (ein Request statt ~85 Seiten)
async function holeApi() {
  const body = await hole('/api/telemetry/standings');
  const j = JSON.parse(body);
  if (!j || !Array.isArray(j.fahrer)) throw new Error('Unerwartete API-Antwort');
  return j;
}

// "&amp;" und Co. zurueck in Klartext
function enthtml(s) {
  return String(s || '')
    .replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(+d))
    .replace(/&amp;/g, '&').replace(/&nbsp;/g, ' ')
    .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'");
}

function textVon(html) {
  return enthtml(html.replace(/<script[\s\S]*?<\/script>/g, ' ').replace(/<[^>]+>/g, '|'))
    .replace(/\|+/g, '|').replace(/\s+/g, ' ').trim();
}

function parseProfil(html, id) {
  const fahrer = { id, url: BASIS + '/Profile/' + id };

  // Name aus <title>: "Profil – Betrayed · Erdi's Racing Community"
  // (erst dekodieren: das – ist ein Entity und würde das Strippen otherwise sprengen)
  const titel = enthtml((html.match(/<title>([^<]+)<\/title>/) || [])[1] || '');
  fahrer.name = titel.replace(/^Profil\s*[–-]\s*/i, '').replace(/\s*·\s*Erdi.*$/i, '').trim();

  // Team + Logo: erstes /images/teams/*.svg mit alt
  const teamImg = html.match(/<img[^>]+src="([^"]*\/images\/teams\/[^"]+)"[^>]*>/i);
  if (teamImg) {
    fahrer.teamLogo = BASIS + teamImg[1];
    const alt = teamImg[0].match(/alt="([^"]*)"/i);
    if (alt) fahrer.team = enthtml(alt[1]).trim();
  }

  // Ingame-Name aus dem profile-card-Block im ROHEN HTML — Namen können
  // "|" enthalten ("ERC | MarcelSchumi"), also nicht über Text-Pipes splitten
  const ingameBlock = html.match(/Ingame-Name<\/div>[\s\S]{0,800}?<div class="text-mute small">([^<]*)<\/div>\s*<div class="fw-semibold">([^<]*)<\/div>/);
  if (ingameBlock) {
    fahrer.plattform = enthtml(ingameBlock[1]).trim();
    fahrer.ingame = enthtml(ingameBlock[2]).trim();
  }

  // Discord
  // Discord + Saison-Bilanz über den pipe-Text (dort schaden Pipes im Namen nicht,
  // weil wir nur label-nach-Wert-Muster suchen)
  const text = textVon(html);
  const discord = text.match(/Discord:\s*\|?\s*([A-Za-z0-9._]+)/);
  if (discord) fahrer.discord = discord[1];

  // Saison-Bilanz: zwischen "Saison-Bilanz" und "Discord:" auswerten
  const bilanzStelle = text.indexOf('Saison-Bilanz');
  if (bilanzStelle >= 0) {
    const b = text.slice(bilanzStelle, text.indexOf('Discord', bilanzStelle) > 0 ? text.indexOf('Discord', bilanzStelle) : bilanzStelle + 800);
    const zahl = label => {
      const m = b.match(new RegExp('([\\d.]+%?|P\\d+)\\s*\\|\\s*' + label));
      return m ? m[1] : null;
    };
    fahrer.saison = {
      rennen: +(zahl('Rennen') || 0),
      siege: +(zahl('Siege') || 0),
      podien: +(zahl('Podien') || 0),
      punkte: +(zahl('Punkte') || 0),
      bestFinish: zahl('Best') || null,
      winRate: zahl('Win-Rate') || null,
    };
  }

  // Liga: die Rennen-Links des Profils tragen leagueId=pro|second|rookie —
  // Mehrheitsentscheid, falls mal ein Gaststart in einer anderen Liga dabei ist.
  const ligaTreffer = html.match(/leagueId=([a-z]+)/g) || [];
  if (ligaTreffer.length) {
    const stimmen = {};
    ligaTreffer.forEach(m => {
      const l = m.split('=')[1];
      stimmen[l] = (stimmen[l] || 0) + 1;
    });
    fahrer.liga = Object.keys(stimmen).sort((a, b) => stimmen[b] - stimmen[a])[0];
  }
  return fahrer;
}

// Release-Kopie (falls gebaut), damit die laufende App die Daten serviert —
// nur im Repo-Modus (--ziel schreibt bereits dorthin, wo die App serviert)
function kopiereRelease() {
  const release = path.join(__dirname, '..', 'src', 'ERCTelemetry.App', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'wwwroot', 'overlay', 'data');
  try {
    fs.mkdirSync(release, { recursive: true });
    fs.copyFileSync(ZIEL, path.join(release, 'erc-drivers.json'));
    console.log('Release-Kopie: ' + release);
  } catch { /* kein Release-Build da */ }
}

async function main() {
  // --ziel=<Pfad>: die App startet den Scraper und lässt ihn direkt in ihren
  // eigenen wwwroot schreiben; ohne --ziel gilt der Repo-Pfad wie bisher.
  const zielArg = process.argv.find(a => a.startsWith('--ziel='));
  const ziel = zielArg ? path.resolve(zielArg.split('=').slice(1).join('=')) : ZIEL;

  // Cache?
  if (!process.argv.includes('--frisch')) {
    try {
      const alt = JSON.parse(fs.readFileSync(ziel, 'utf8'));
      const stunden = (Date.now() - new Date(alt.geholt).getTime()) / 36e5;
      if (stunden < CACHE_STUNDEN) {
        console.log('Cache ist frisch (' + alt.fahrer.length + ' Fahrer, vor ' + stunden.toFixed(1) + ' h geholt). Neu holen: --frisch');
        return;
      }
    } catch { /* noch keine Datei */ }
  }

  // Weg 1: Standings-API — ein Request, autoritative Positionen + echte
  // Konstrukteurswertung. Antwortformat ist kompatibel zum Scraper-Format.
  if (!process.argv.includes('--legacy')) {
    try {
      const api = await holeApi();
      fs.mkdirSync(path.dirname(ziel), { recursive: true });
      fs.writeFileSync(ziel, JSON.stringify(api, null, 2));
      console.log('OK (API): ' + api.fahrer.length + ' Fahrer, ' + (api.teams || []).length + ' Team-Zeilen, ' + (api.ligen || []).length + ' Ligen -> ' + ziel);
      if (!zielArg) kopiereRelease();
      return;
    } catch (e) {
      console.error('API-Weg fehlgeschlagen (' + e.message + ') — HTML-Scraping als Fallback.');
    }
  }

  console.log('Hole Teams von ' + BASIS + '/Community/Teams ...');
  const teamsHtml = await hole('/Community/Teams');
  const teamNamen = [...new Set((teamsHtml.match(/\/Community\/Team\?name=([^"&]+)/g) || [])
    .map(m => decodeURIComponent(m.split('=')[1])))];
  console.log('Teams: ' + teamNamen.join(', '));

  const profilIds = new Set();
  for (const team of teamNamen) {
    const html = await hole('/Community/Team?name=' + encodeURIComponent(team));
    (html.match(/\/Profile\/(\d+)/g) || []).forEach(m => profilIds.add(m.split('/')[2]));
    console.log('  ' + team + ': ' + profilIds.size + ' Profile insgesamt');
    await new Promise(r => setTimeout(r, ABFRAGE_MS));
  }

  console.log('Hole ' + profilIds.size + ' Fahrer-Profile ...');
  const fahrer = [];
  for (const id of profilIds) {
    try {
      const html = await hole('/Profile/' + id);
      const f = parseProfil(html, id);
      if (f.name && f.name !== 'Profil') fahrer.push(f);
      console.log('  ' + (f.ingame || f.name || id) + (f.team ? ' (' + f.team + ')' : ''));
    } catch (e) { console.error('  ! ' + id + ': ' + e.message); }
    await new Promise(r => setTimeout(r, ABFRAGE_MS));
  }

  const daten = { geholt: new Date().toISOString(), quelle: BASIS, fahrer };
  fs.mkdirSync(path.dirname(ziel), { recursive: true });
  fs.writeFileSync(ziel, JSON.stringify(daten, null, 2));
  console.log('OK (HTML): ' + fahrer.length + ' Fahrer -> ' + ziel);

  if (!zielArg) kopiereRelease();
}

main().catch(e => { console.error('Fehler: ' + e.message); process.exit(1); });