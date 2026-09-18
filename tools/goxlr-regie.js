/**
 * ERCTelemetry — goxlr-regie.js
 *
 * Brücke: GoXLR-Sampler-Buttons → SLOBS-Szenen.
 *
 * Der Community-Daemon "GoXLR Utility" (Port 14564) pusht Button-Events als
 * JSON-Patches über seine WebSocket; wir mappen sie auf SLOBS-Szenen mit dem
 * verifizierten SockJS-Protokoll aus regie.js (Port 59650 + Token).
 *
 *   node tools/goxlr-regie.js --test               # Szenenliste ausgeben
 *   node tools/goxlr-regie.js --discovery          # GoXLR-Events loggen
 *   node tools/goxlr-regie.js --token <TOKEN>      # Brücke laufen lassen
 *
 * Belegung unten in MAPPING; Button-Namen: SamplerSelectA/B/C, SamplerClear,
 * SamplerTopLeft/TopRight/BottomLeft/BottomRight. Voraussetzung: GoXLR Utility
 * (goxlr-daemon) läuft, die offizielle GoXLR-App ist geschlossen.
 * Token NICHT in Datei einchecken — per Argument oder SLOBS_TOKEN-Umgebung.
 */
'use strict';
const crypto = require('crypto');
const http = require('http');

// ── Belegung: GoXLR-Button → SLOBS-Szene ────────────────────────────────────
const MAPPING = {
  SamplerSelectA: 'Live · Gameplay',
  SamplerSelectB: 'Live · ERC Race',
  SamplerSelectC: 'Live · ERC Broadcast',
  SamplerClear: 'Intermission',
  SamplerTopLeft: 'ERC · Startaufstellung',
  SamplerTopRight: 'ERC · Meisterschaft',
  SamplerBottomLeft: 'ERC · Info-Board',
  SamplerBottomRight: 'Live · ERC Kommentar',
};

// ── Konfiguration ────────────────────────────────────────────────────────────
const args = process.argv.slice(2);
function argWert(name) {
  // --name=WERT und --name WERT (Leerzeichen) akzeptieren
  const i = args.indexOf('--' + name);
  if (i >= 0 && args[i + 1]) return args[i + 1];
  const a = args.find(x => x.startsWith('--' + name + '='));
  return a ? a.split('=').slice(1).join('=') : null;
}
const CONF = {
  discovery: args.includes('--discovery'),
  test: args.includes('--test'),
  goxlr: process.env.GOXLR_WS || 'ws://127.0.0.1:14564/api/websocket',
  obsHost: argWert('host') || '127.0.0.1',
  obsPort: parseInt(argWert('port'), 10) || 59650,
  token: argWert('token') || process.env.SLOBS_TOKEN || '',
};

function log(...xs) {
  console.log(new Date().toLocaleTimeString('de-DE'), ...xs);
}

// ── GoXLR (Utility-Daemon, Port 14564) — globales Node-WebSocket ────────────
let goxlrSock;
function verbindeGoxlr(onPatch) {
  function anlaufen() {
    goxlrSock = new WebSocket(CONF.goxlr);
    goxlrSock.addEventListener('open', () => log('[goxlr] verbunden:', CONF.goxlr));
    goxlrSock.addEventListener('message', e => {
      let msg;
      try { msg = JSON.parse(String(e.data)); } catch { return; }
      const patch = msg && msg.data && msg.data.Patch;
      if (Array.isArray(patch)) onPatch(patch);
    });
    goxlrSock.addEventListener('error', e => log('[goxlr] Fehler:', (e.message || (e.error && e.error.message) || '')));
    goxlrSock.addEventListener('close', () => {
      log('[goxlr] getrennt — neuer Versuch in 5 s');
      setTimeout(anlaufen, 5000);
    });
  }
  anlaufen();
}

// ── Mini-WebSocket-Client (keine deps, Client-Frames maskiert) ───────────────
// 1:1 aus regie.js übernommen (verifiziert gegen Streamlabs Desktop).
function wsVerbinde(url, handlers) {
  const u = new URL(url);
  const key = crypto.randomBytes(16).toString('base64');
  const req = http.get({
    host: u.hostname, port: u.port || 80, path: u.pathname + u.search,
    headers: { Connection: 'Upgrade', Upgrade: 'websocket',
               'Sec-WebSocket-Key': key, 'Sec-WebSocket-Version': 13 },
  });
  const sock = { socket: null };
  let gepingt = false;
  req.on('upgrade', (res, socket) => {
    sock.socket = socket;
    socket.setNoDelay(true);
    // WebSocket-Ping vom Server mit Pong beantworten — Browser machen das
    // automatisch, ein roher Socket nicht. Ohne Pong killt SLOBS die Session.
    socket.on('data', d => empfangen(d, {
      onMessage: handlers.onMessage,
      onClose: handlers.onClose,
      onPing: payload => {
        if (!gepingt) { gepingt = true; log('[ws] Server-Ping erhalten -> Pong'); }
        socket.write(rahmen(payload, 0x8A));
      },
    }));
    socket.on('close', () => handlers.onClose && handlers.onClose());
    socket.on('error', () => handlers.onClose && handlers.onClose());
    handlers.onOpen && handlers.onOpen();
  });
  req.on('error', e => { console.error('[ws] ' + url + ': ' + e.message); handlers.onClose && handlers.onClose(); });
  return {
    send: text => {
      const s = sock.socket;
      if (s) s.write(rahmen(Buffer.from(text, 'utf8')));
    },
    schliessen: () => { if (sock.socket) sock.socket.destroy(); },
  };
}

let wsPuffer = Buffer.alloc(0);
function empfangen(d, handlers) {
  wsPuffer = Buffer.concat([wsPuffer, d]);
  while (true) {
    if (wsPuffer.length < 2) return;
    const opCode = wsPuffer[0] & 0x0f;
    let len = wsPuffer[1] & 0x7f, off = 2;
    if (len === 126) { if (wsPuffer.length < 4) return; len = wsPuffer.readUInt16BE(2); off = 4; }
    else if (len === 127) { if (wsPuffer.length < 10) return; len = Number(wsPuffer.readBigUInt64BE(2)); off = 10; }
    if (wsPuffer.length < off + len) return;
    const text = wsPuffer.slice(off, off + len).toString('utf8');
    wsPuffer = wsPuffer.slice(off + len);
    if (opCode === 1 && handlers.onMessage) handlers.onMessage(text);
    else if (opCode === 9 && handlers.onPing) handlers.onPing(text);
  }
}

function rahmen(payload, opCode) {
  const ersteByte = opCode || 0x81;
  const maske = crypto.randomBytes(4);
  const maskiert = Buffer.from(payload);
  for (let i = 0; i < maskiert.length; i++) maskiert[i] ^= maske[i & 3];
  let head;
  if (payload.length < 126) head = Buffer.from([ersteByte, 0x80 | payload.length]);
  else if (payload.length < 65536) {
    head = Buffer.alloc(4); head[0] = ersteByte; head[1] = 0x80 | 126;
    head.writeUInt16BE(payload.length, 2);
  } else {
    head = Buffer.alloc(10); head[0] = ersteByte; head[1] = 0x80 | 127;
    head.writeBigUInt64BE(BigInt(payload.length), 2);
  }
  return Buffer.concat([head, maske, maskiert]);
}

// ── SLOBS-RPC (Streamlabs Desktop: SockJS auf Port 59650) ────────────────────
// 1:1 aus regie.js übernommen (verifiziert): SockJS-Pfad /api/000/<session>/websocket,
// Auth mit method 'auth' + resource 'TcpServerService', danach ScenesService.
function einzelVerbindung(ziel, beiTrennung) {
  let idZaehler = 10;
  const wartend = new Map();
  let authVersprechen;
  let keepalive = null;
  const sock = wsVerbinde('ws://' + ziel.host + ':' + ziel.port +
    '/api/000/' + crypto.randomBytes(8).toString('hex') + '/websocket', {
    onMessage: text => {
      if (text === 'o') {
        // SockJS killt unaktive Sessions (~40 s) — mit leeren 'a'-Frames
        // (JSON-Array ohne Nachrichten) am Leben halten.
        if (keepalive) clearInterval(keepalive);
        keepalive = setInterval(() => {
          try { sock.send('[]'); } catch { /* Verbindung weg -> Reconnect übernimmt */ }
        }, 10000);
        authVersprechen = new Promise((resolve, ablehnen) => {
          wartend.set(1, { resolve: authOkMachen(resolve), ablehnen: ablehnen });
        });
        rpc(1, 'auth', { resource: 'TcpServerService', args: [ziel.token] });
        return;
      }
      if (text[0] !== 'a' && text[0] !== 'm') return;
      let liste;
      try { liste = JSON.parse(text.slice(1)); } catch { return; }
      liste.forEach(inner => {
        let msg;
        try { msg = JSON.parse(inner); } catch { return; }
        if (msg.id === 1) {
          const w = wartend.get(1);
          if (w) { wartend.delete(1); if (msg.error) w.ablehnen(new Error(JSON.stringify(msg.error))); else w.resolve(msg.result); }
          return;
        }
        if (msg.id && wartend.has(msg.id)) {
          const w = wartend.get(msg.id);
          wartend.delete(msg.id);
          if (msg.error) w.ablehnen(new Error(JSON.stringify(msg.error)));
          else w.resolve(msg.result);
        }
      });
    },
    onClose: () => {
      if (keepalive) { clearInterval(keepalive); keepalive = null; }
      beiTrennung();
    },
  });

  function rpc(id, methode, params) {
    sock.send(JSON.stringify([JSON.stringify({ jsonrpc: '2.0', id: id, method: methode, params: params })]));
  }
  function authOkMachen(resolve) {
    return ergebnis => {
      if (ergebnis === false) {
        console.error('Streamlabs hat den Token abgelehnt.');
        process.exit(1);
      }
      log('[slobs] auth OK');
      resolve();
    };
  }
  function warteAufAuth(versuche) {
    if (authVersprechen) return authVersprechen;
    if ((versuche || 0) > 100) return Promise.reject(new Error('SockJS-Session kam nie auf'));
    return new Promise(r => setTimeout(r, 50)).then(() => warteAufAuth((versuche || 0) + 1));
  }
  return {
    frage: (resource, method, methodArgs) => new Promise((resolve, ablehnen) => {
      warteAufAuth().then(() => {
        const id = ++idZaehler;
        wartend.set(id, { resolve: resolve, ablehnen: ablehnen });
        rpc(id, method, { resource: resource, args: methodArgs || [] });
        setTimeout(() => {
          if (!wartend.delete(id)) return;
          ablehnen(new Error('Timeout: ' + resource + '.' + method));
          // Silent-Death-Watchdog: reagiert SLOBS nicht, ist die Verbindung
          // wahrscheinlich tot (kein Close-Event kommt) — hart trennen, damit
          // die Außenhülle sie neu aufbaut.
          log('[slobs] Timeout bei ' + resource + '.' + method + ' — Verbindung wird hart getrennt (Reconnect)');
          sock.schliessen();
        }, 8000);
      }).catch(ablehnen);
    }),
  };
}

function slobsVerbinde(ziel) {
  let innen = null;
  function aufbauen() {
    innen = einzelVerbindung(ziel, () => {
      log('[slobs] getrennt — neuer Versuch in 5 s');
      innen = null;
      setTimeout(aufbauen, 5000);
    });
  }
  aufbauen();

  async function frage(resource, method, methodArgs, versuche) {
    // Keine lebende Verbindung (z. B. SLOBS startet gerade) -> kurz warten
    if (!innen) {
      if ((versuche || 0) > 120) throw new Error('SLOBS-Verbindung kam nicht zurück');
      await new Promise(r => setTimeout(r, 500));
      return frage(resource, method, methodArgs, (versuche || 0) + 1);
    }
    try {
      return await innen.frage(resource, method, methodArgs);
    } catch (e) {
      // Timeout wurde schon hart getrennt; 1-2 Retries gegen die Neue
      if ((versuche || 0) < 2) {
        await new Promise(r => setTimeout(r, 1500));
        return frage(resource, method, methodArgs, (versuche || 0) + 1);
      }
      throw e;
    }
  }
  return { frage: (r, m, a) => frage(r, m, a) };
}

// ── Hauptprogramm ────────────────────────────────────────────────────────────
async function main() {
  // 1) GoXLR-Daemon prüfen — beim Autostart läuft er evtl. noch nicht hoch,
  //    also warten statt sofort aufzugeben (bei --test klassisch abbrechen).
  while (true) {
    try {
      await goxlrStatus();
      break;
    } catch (e) {
      if (CONF.test) {
        console.error('GoXLR Utility nicht erreichbar: ' + e.message);
        console.error('Läuft der Daemon (goxlr-daemon.exe)? Offizielle GoXLR-App muss geschlossen sein.');
        process.exit(1);
      }
      log('[goxlr] Utility nicht erreichbar (' + e.message + ') — warte auf Daemon, neuer Versuch in 5 s');
      await new Promise(r => setTimeout(r, 5000));
    }
  }

  // 2) SLOBS: Szenen holen (--test nur anzeigen). Wartet ebenfalls, bis
  //    Streamlabs beim Systemstart erreichbar ist.
  const slobs = slobsVerbinde({ host: CONF.obsHost, port: CONF.obsPort, token: CONF.token });
  let szenen;
  let szenenVersuche = 0;
  while (true) {
    try {
      szenen = await slobs.frage('ScenesService', 'getScenes');
      break;
    } catch (e) {
      szenenVersuche++;
      if (CONF.test || szenenVersuche > 60) {
        console.error('Keine Verbindung zu Streamlabs: ' + e.message);
        console.error('Token/Port pruefen: Einstellungen -> API/Websocket (Port 59650).');
        process.exit(1);
      }
      log('[slobs] noch nicht erreichbar (' + e.message + ') — neuer Versuch in 5 s');
      await new Promise(r => setTimeout(r, 5000));
    }
  }
  const namen = szenen.map(s => s.name);
  log('[slobs] Szenen (' + namen.length + '): ' + namen.join(' | '));

  if (CONF.test) {
    log('[test] OK — nichts geschaltet. Brücke: node tools/goxlr-regie.js --token <TOKEN>');
    process.exit(0);
  }

  // 3) Unbelegte Buttons melden, damit man sie in MAPPING nachträgt
  const unbelegt = Object.entries(MAPPING).filter(([, szene]) => !szene).map(([b]) => b);
  if (unbelegt.length) log('[map] (Hinweis) ohne Szene: ' + unbelegt.join(', '));

  const szenenId = name => { const s = szenen.find(x => x.name === name); return s ? s.id : null; };

  // 4) Button-Events: bei Press (value=true) die gemappte Szene schalten
  verbindeGoxlr(patch => {
    for (const op of patch) {
      const m = String(op.path || '').match(/\/button_down\/([A-Za-z]+)$/);
      if (!m) continue;
      const button = m[1];
      if (op.value !== true) continue; // nur Press, kein Release
      if (CONF.discovery) { log('[goxlr] Button:', button); continue; }
      const szene = MAPPING[button];
      if (!szene) continue;
      const id = szenenId(szene);
      if (!id) { log('[map] Szene unbekannt: "' + szene + '" (Button ' + button + ')'); continue; }
      slobs.frage('ScenesService', 'makeSceneActive', [id])
        .then(() => log('[regie] Button', button, '->', szene))
        .catch(e => log('[regie] Schalten fehlgeschlagen: ' + e.message));
    }
  });

  log('[goxlr-regie] aktiv — Strg+C beendet.');
}

// GoXLR-Daemon kurz anpingen (HTTP statt WS reicht für den Check)
function goxlrStatus() {
  const http2 = require('http');
  return new Promise((resolve, reject) => {
    const req = http2.get({ host: '127.0.0.1', port: 14564, path: '/api/version', timeout: 3000 }, res => {
      res.resume();
      res.statusCode < 500 ? resolve() : reject(new Error('HTTP ' + res.statusCode));
    });
    req.on('timeout', () => { req.destroy(new Error('Timeout')); });
    req.on('error', reject);
  });
}

main().catch(e => { console.error(e); process.exit(1); });