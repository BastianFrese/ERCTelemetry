/**
 * ERCTelemetry - Demo-Server fuer Overlay-Tests mit Dummy-Daten.
 *
 * Serviert die Overlay-Seiten statisch und fuetttert sie ueber einen
 * WebSocket (/ws) mit einer lebendigen Fake-Session - gleiche Nachrichten
 * wie die echte App (Wire protocol v1), aber ohne Spiel:
 *
 *   node tools/demo-server.js [port]     (Standard 8091, aus Repo-Root)
 *
 * Dann z. B.: http://127.0.0.1:8091/overlay/broadcast.html
 * Alle Seiten verbinden sich selbst (ws://location.host/ws).
 */
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const PORT = parseInt(process.argv[2], 10) || 8091;
const OVERLAY_DIR = path.join(__dirname, '..', 'src', 'ERCTelemetry.App', 'wwwroot', 'overlay');

// Statischer File-Server
const server = http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0]);
  if (p === '/' || p === '/overlay/') p = '/overlay/broadcast.html';
  const name = p.replace(/^\/overlay\//, '');
  // nur Overlay-Dateien: direkt im overlay/-Ordner oder ein Level tiefer (data/)
  const erlaubt = /^[A-Za-z0-9._-]+$/.test(name) || /^data\/[A-Za-z0-9._-]+$/.test(name);
  const file = path.join(OVERLAY_DIR, name);
  if (!erlaubt || !file.startsWith(OVERLAY_DIR)) {
    res.writeHead(404); res.end('nicht gefunden: ' + name); return;
  }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); res.end('nicht gefunden: ' + name); return; }
    const type = name.endsWith('.json') ? 'application/json'
      : name.endsWith('.js') ? 'text/javascript'
      : name.endsWith('.css') ? 'text/css' : 'text/html; charset=utf-8';
    res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-store' });
    res.end(data);
  });
});

// Rohes WebSocket (keine deps): Handshake + Server-zu-Client-Frames
const sockets = new Set();
server.on('upgrade', (req, socket) => {
  const key = req.headers['sec-websocket-key'];
  if (!key) { socket.destroy(); return; }
  const accept = crypto.createHash('sha1')
    .update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write(
    'HTTP/1.1 101 Switching Protocols\r\n' +
    'Upgrade: websocket\r\n' +
    'Connection: Upgrade\r\n' +
    'Sec-WebSocket-Accept: ' + accept + '\r\n\r\n');
  sockets.add(socket);
  socket.on('close', () => sockets.delete(socket));
  socket.on('error', () => sockets.delete(socket));
  socket.resume(); // ankommende Frames (ping/select) verwerfen wir
});

function wsSend(text) {
  const payload = Buffer.from(text, 'utf8');
  const n = payload.length;
  let head;
  if (n < 126) { head = Buffer.from([0x81, n]); }
  else if (n < 65536) {
    head = Buffer.alloc(4); head[0] = 0x81; head[1] = 126; head.writeUInt16BE(n, 2);
  } else {
    head = Buffer.alloc(10); head[0] = 0x81; head[1] = 127;
    head.writeUInt32BE(0, 2); head.writeUInt32BE(n, 6);
  }
  const frame = Buffer.concat([head, payload]);
  for (const s of sockets) { try { s.write(frame); } catch { /* weg ist weg */ } }
}

// Fake-Session: 8 Fahrer, atemnde Luecken, Boxenstop, Strafen, Karte
const START = Date.now();
const RUNDENDAUER = 95000;   // ms pro Streckenumlauf (Karten-Tempo)
const FAHRER = [
  { name: 'LeutnantKW',      team: 'McLaren',      num: 81, base: 0.0000,  speed: 1.000 },
  { name: 'Erdi',            team: 'RedBullRacing', num: 1,  base: 0.0036,  speed: 1.006 },
  { name: 'Vettel_Fan',      team: 'Ferrari',      num: 5,  base: 0.0078,  speed: 0.997 },
  { name: 'Kerbstrecke-Kai', team: 'Mercedes',     num: 44, base: 0.0110,  speed: 1.012 },
  { name: 'ApexHunter',      team: 'AstonMartin',  num: 14, base: 0.0152,  speed: 0.995 },
  { name: 'GripLord',        team: 'Alpine',       num: 10, base: 0.0184,  speed: 1.006 },
  { name: 'ShiftUp',         team: 'Williams',     num: 23, base: 0.0226,  speed: 1.012 },
  { name: 'Boxenstop',       team: 'Haas',         num: 31, base: 0.0258,  speed: 0.990 },
];

function fahrerFeld() {
  const rennen = (Date.now() - START) / 1000;
  const feld = FAHRER.map((f, i) => {
    const fort = -f.base + (rennen / RUNDENDAUER) * f.speed +
      Math.sin(rennen / 9 + i * 1.7) * 0.003; // Atem-Puls: Duelle kommen & gehen
    return Object.assign({}, f, { fort: fort, idx: i });
  });
  feld.sort((a, b) => b.fort - a.fort);
  return feld.map((f, i) => {
    const lueckeVorne = i === 0 ? 0 : (feld[i - 1].fort - f.fort) * RUNDENDAUER;
    return {
      position: i + 1, carIndex: f.idx, name: f.name, team: f.team,
      raceNumber: f.num, isPlayer: f.name === 'LeutnantKW',
      currentLapNum: 12 + Math.floor((f.fort - FAHRER[0].base) * 44),
      lastLapTimeMs: 89876 + i * 743, bestLapTimeMs: 89012 + i * 655,
      currentLapTimeMs: (rennen * 1000) % 95000,
      gapToLeaderMs: Math.round((feld[0].fort - f.fort) * RUNDENDAUER),
      gapToCarInFrontMs: Math.round(lueckeVorne),
      pitStatus: f.name === 'Boxenstop' && (rennen % 60) < 12 ? 'Pitting' : 'None',
      pitStops: f.name === 'Boxenstop' ? 1 : 0,
      penalties: f.name === 'Kerbstrecke-Kai' ? 5 : 0,
      resultStatus: 'Active',
      tyreCompound: ['F1C3', 'F1C4', 'F1C2', 'F1C3', 'F1C5', 'F1C3', 'F1C2', 'F1C1'][f.idx],
      tyreAgeLaps: [12, 3, 19, 8, 5, 22, 14, 9][f.idx],
      gridPosition: [1, 3, 2, 4, 6, 5, 8, 7][f.idx],
      sector1TimeMs: 28456, sector2TimeMs: 31022, sector3TimeMs: 28111,
      s1Status: ['Green', 'Purple', 'None'][i % 3],
      s2Status: ['None', 'Green', 'Purple'][(i + 1) % 3],
      s3Status: ['Purple', 'None', 'Green'][(i + 2) % 3],
      bestSector1TimeMs: 28400, bestSector2TimeMs: 31000, bestSector3TimeMs: 28100,
      lapValidity: 1,
    };
  });
}

function spielerFrame() {
  const rennen = (Date.now() - START) / 1000;
  const speed = 265 + Math.sin(rennen / 3.1) * 55 + Math.sin(rennen / 1.7) * 18;
  const gang = Math.max(3, Math.min(8, 5 + Math.round(Math.sin(rennen / 4.2) * 2.6)));
  return {
    player: {
      carIndex: 0, name: 'LeutnantKW', speed: Math.round(speed), gear: gang,
      engineRpm: Math.round(7800 + speed * 12),
      throttle: Math.max(0, Math.min(1, 0.6 + Math.sin(rennen / 2.3) * 0.5)),
      brake: Math.max(0, Math.sin(rennen / 2.3 + 2.6)) * 0.85,
      ersPercent: 45 + Math.sin(rennen / 6) * 35, ersDeployMode: 'Hotlap',
      fuelInTank: 4.2, fuelRemainingLaps: 3.1,
      tyreCompound: 'F1C3', tyreAgeLaps: 12,
      drsAllowed: rennen % 40 < 25, drsOn: rennen % 40 < 20,
    },
    rival: {
      carIndex: 1, name: 'Erdi', speed: Math.round(speed * 1.01), gear: gang,
      engineRpm: 9900, throttle: 0.9, brake: 0.05, ersPercent: 60,
      ersDeployMode: 'Overtake', fuelInTank: 5.1, fuelRemainingLaps: 3.4,
      tyreCompound: 'F1C4', tyreAgeLaps: 5, drsAllowed: true, drsOn: false,
    },
  };
}

// Karte: geschlossene Strecke - Autos kreisen, Outline liegt sofort vor
function mapPositions() {
  const rennen = (Date.now() - START) / 1000;
  return FAHRER.map((f, i) => {
    const s = (f.base + (rennen / RUNDENDAUER) * f.speed) % 1;
    const w = s * Math.PI * 2;
    return {
      carIndex: i, isPlayer: f.name === 'LeutnantKW',
      x: Math.cos(w) * (950 + Math.sin(w * 3) * 180),
      z: Math.sin(w) * (420 + Math.cos(w * 2) * 120),
    };
  });
}

function trackLayout() {
  const punkte = [];
  for (let i = 0; i < 84; i++) {
    const w = (i / 84) * Math.PI * 2;
    punkte.push([
      Math.cos(w) * (950 + Math.sin(w * 3) * 180),
      Math.sin(w) * (420 + Math.cos(w * 2) * 120),
    ]);
  }
  return { t: 'tracklayout', track: 'Spa-Francorchamps', sessionUid: 1, points: punkte, startX: 950, startZ: 0 };
}

// Sende-Loops
function sessionNachricht(standings) {
  return {
    t: 'state',
    session: {
      sessionUid: 1, sessionType: 'Race', track: 'Spa-Francorchamps', totalLaps: 44,
      weather: 'LightRain', gameMode: 'OnlineCustom', isNetworkGame: true,
      playerCarIndex: 0, airTemperature: 19, trackTemperature: 27, sessionTimeLeft: 0,
      marshalZones: [
        { zoneStart: 0, flag: (Date.now() - START) % 30000 < 8000 ? 3 : 0 },
        { zoneStart: 0.4, flag: 0 },
      ],
      forecast: [
        { timeOffsetMinutes: 15, weather: 'LightRain', rainPercent: 65, trackTemperature: 26, airTemperature: 21 },
        { timeOffsetMinutes: 30, weather: 'Cloudy', rainPercent: 20, trackTemperature: 29, airTemperature: 23 },
      ],
    },
    standings: standings,
    tyres: standings.map((r, i) => ({ carIndex: r.carIndex, wearPercent: 10 + i * 9, damagePercent: i === 5 ? 12.5 : 0 })),
  };
}

// Fake-AI-Kommentarzeilen (t: 'commentary' wie die echte App-Pipeline)
const KOMMENTARE = [
  'Kampf an der Spitze: LeutnantKW und Erdi liegen unter einer Zehntel!',
  'Kerbstrecke-Kai kassiert 5 Sekunden - Track Limits.',
  'Boxenstop meldet Technikproblem - das Rennen ist fuer ihn gelaufen.',
  'LeutnantKW dreht die schnellste Runde - 1:31.101!',
  'GripLord aemt seine weichen Reifen ab, 22 Runden alt.',
  'Regen kommt in rund 15 Minuten - Stint-Rechnung wird spannend.',
];
let komIdx = 0;
setInterval(() => {
  wsSend(JSON.stringify({ t: 'commentary', text: KOMMENTARE[komIdx % KOMMENTARE.length] }));
  komIdx++;
}, 9000);
setTimeout(() => wsSend(JSON.stringify({ t: 'commentary', text: 'Lights out und away we go - Rennen in Spa!' })), 2500);

setInterval(() => wsSend(JSON.stringify(sessionNachricht(fahrerFeld()))), 200);
setInterval(() => wsSend(JSON.stringify({ t: 'player', ...spielerFrame() })), 33);
setInterval(() => wsSend(JSON.stringify({ t: 'map', positions: mapPositions() })), 100);

// Ereignisse: FL, Strafe, Ausfall, PB - alle ~12 s eines, danach Pause
const EVENTE = [
  { t: 'event', type: 'fastest-lap', text: 'Schnellste Runde: Erdi - 1:31.234', carIndex: 1 },
  { t: 'event', type: 'penalty', text: '5 s Zeitstrafe: Kerbstrecke-Kai (Track Limits)', carIndex: 3 },
  { t: 'event', type: 'personal-best', text: 'Persoenliche Bestzeit: LeutnantKW - 1:31.890', carIndex: 0 },
  { t: 'event', type: 'retirement', text: 'Ausfall: Boxenstop (Technik)', carIndex: 7 },
  { t: 'event', type: 'fastest-lap', text: 'Schnellste Runde: LeutnantKW - 1:31.101', carIndex: 0 },
];
let evIdx = 0;
setInterval(() => {
  const ev = EVENTE[evIdx % EVENTE.length];
  evIdx++;
  wsSend(JSON.stringify(Object.assign({}, ev, { utc: new Date().toISOString(), seq: evIdx })));
}, 12000);
setTimeout(() => wsSend(JSON.stringify(Object.assign({}, EVENTE[0], { utc: new Date().toISOString(), seq: 0 }))), 1500);
setTimeout(() => wsSend(JSON.stringify(trackLayout())), 800);
setInterval(() => wsSend(JSON.stringify(trackLayout())), 30000);

console.log('ERCTelemetry Demo-Server: http://127.0.0.1:' + PORT + '/overlay/  (' + sockets.size + ' clients)');
setInterval(() => { console.log('  clients: ' + sockets.size); }, 30000).unref();
server.listen(PORT, '127.0.0.1', () => console.log('bereit auf Port ' + PORT));
