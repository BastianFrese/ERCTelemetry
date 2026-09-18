/**
 * ERCTelemetry — Auto-Regie
 *
 * Verbindet die Telemetrie (WebSocket /ws) mit Streamlabs und schaltet
 * Szenen wie ein TV-Regisseur:
 *
 *   Rennstart      -> Start-Szene   (z. B. "Live · Gameplay")
 *   Rennende       -> Ende-Szene    (z. B. "Streaming Ending Soon")
 *   Ausfall        -> kurzer Blick aufs Info-Board, dann zurueck
 *   optional: heisses Duell -> Duell-Szene
 *
 * Benutzung (aus dem Repo-Root):
 *
 *   node tools/regie.js --test               # nur Szenenliste ausgeben
 *   node tools/regie.js --token <TOKEN>      # Regie laufen lassen
 *
 * Streamlabs: Einstellungen -> API/Websocket -> Port 59650 + Token.
 * Token NICHT in Dateien einchecken — per Argument oder SLOBS_TOKEN-Umgebung.
 */
'use strict';
const crypto = require('crypto');
const http = require('http');

// ── Konfiguration ─────────────────────────────────────────────
const args = process.argv.slice(2);
function argWert(name) {
  const a = args.find(x => x.startsWith('--' + name + '='));
  return a ? a.split('=').slice(1).join('=') : null;
}
const CONF = {
  test: args.includes('--test'),
  obsHost: argWert('host') || '127.0.0.1',
  obsPort: parseInt(argWert('port'), 10) || 59650,
  token: argWert('token') || process.env.SLOBS_TOKEN || '',
  telemetry: argWert('telemetry') || 'ws://127.0.0.1:8090/ws',
  szenen: {
    start: 'Live · Gameplay',        // bei session-started
    ende: 'Streaming Ending Soon',   // bei session-ended
  },
  duellSekunden: 0,              // so lange unter 0.3 s, bevor gewechselt wird (0 = aus)
  duellSzenen: '',               // leer = kein Wechsel beim Duell (Alerts reichen)
  ausfallSekunden: 8,            // so lange aufs Board bei Ausfall (0 = aus)
  ausfallSzenen: 'ERC · Info-Board',
  rueckSzenen: 'Live · Gameplay',  // wohin nach dem Board-Zwischenschritt
  abkuehlenMs: 90000,            // min. Pause zwischen Regie-Eingriffen
  kommentarSzenen: '',           // z. B. 'ERC · Info-Board': bei KI-Kommentar kurz hin (leer = aus)
  kommentarSekunden: 12,         // so lange bleiben (nur wenn kommentarSzenen gesetzt)
};

// ── Mini-WebSocket-Client (keine deps, Client-Frames maskiert) ─
function wsVerbinde(url, handlers) {
  const u = new URL(url);
  const key = crypto.randomBytes(16).toString('base64');
  const req = http.get({
    host: u.hostname, port: u.port || 80, path: u.pathname + u.search,
    headers: { Connection: 'Upgrade', Upgrade: 'websocket',
               'Sec-WebSocket-Key': key, 'Sec-WebSocket-Version': 13 },
  });
  const sock = { socket: null };
  req.on('upgrade', (res, socket) => {
    sock.socket = socket;
    socket.setNoDelay(true);
    socket.on('data', d => empfangen(d, handlers));
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
  }
}
function rahmen(payload) {
  const maske = crypto.randomBytes(4);
  const maskiert = Buffer.from(payload);
  for (let i = 0; i < maskiert.length; i++) maskiert[i] ^= maske[i & 3];
  let head;
  if (payload.length < 126) head = Buffer.from([0x81, 0x80 | payload.length]);
  else if (payload.length < 65536) {
    head = Buffer.alloc(4); head[0] = 0x81; head[1] = 0x80 | 126;
    head.writeUInt16BE(payload.length, 2);
  } else {
    head = Buffer.alloc(10); head[0] = 0x81; head[1] = 0x80 | 127;
    head.writeBigUInt64BE(BigInt(payload.length), 2);
  }
  return Buffer.concat([head, maske, maskiert]);
}

// ── SLOBS-RPC (Streamlabs Desktop: SockJS auf /api, Port 59650) ─
// SockJS-Schale: Pfad /api/000/<session>/websocket, Frames sind
// JSON-Arrays, deren Elemente JSON-Strings sind (doppelte Kodierung).
function slobsVerbinde(ziel) {
  let idZaehler = 10;
  const wartend = new Map();
  let authVersprechen; // wird in onMessage 'o' gesetzt — Fragen warten darauf
  const sock = wsVerbinde('ws://' + ziel.host + ':' + ziel.port +
    '/api/000/' + crypto.randomBytes(8).toString('hex') + '/websocket', {
    onMessage: text => {
      if (text === 'o') { // SockJS-Session offen -> Auth schicken
        authVersprechen = new Promise((resolve, ablehnen) => {
          wartend.set(1, { resolve: authOkMachen(resolve), ablehnen: ablehnen });
        });
        // Achtung: Top-Level method muss "auth" sein (aus app.asar verifiziert:
        // "auth" === request.method && "TcpServerService" === request.params.resource)
        rpc(1, 'auth', { resource: 'TcpServerService', args: [ziel.token] });
        return;
      }
      if (text[0] !== 'a' && text[0] !== 'm') return; // o/c/h ignorieren
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
    onClose: () => { console.error('[slobs] Verbindung zu — Prozess beenden und neu starten.'); },
  });

  function rpc(id, methode, params) {
    // SLOBS-Drahtformat (app.asar verifiziert): oben method = FUNKTIONSNAME,
    // params.resource = Service.  auth -> {method:'auth', params:{resource:'TcpServerService', args:[token]}}
    sock.send(JSON.stringify([JSON.stringify({ jsonrpc: '2.0', id: id, method: methode, params: params })]));
  }
  function authOkMachen(resolve) {
    return ergebnis => {
      if (ergebnis === false) {
        console.error('Streamlabs hat den Token abgelehnt.');
        process.exit(1);
      }
      console.log('[slobs] auth OK');
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
        setTimeout(() => { if (wartend.delete(id)) ablehnen(new Error('Timeout: ' + resource + '.' + method)); }, 8000);
      }).catch(ablehnen);
    }),
  };
}

// ── Hauptprogramm ─────────────────────────────────────────────
async function main() {
  const slobs = slobsVerbinde({ host: CONF.obsHost, port: CONF.obsPort, token: CONF.token });

  let szenen;
  try {
    szenen = await slobs.frage('ScenesService', 'getScenes');
  } catch (e) {
    console.error('Keine Verbindung zu Streamlabs: ' + e.message);
    console.error('Token/Port pruefen: Einstellungen -> API/Websocket (Port 59650).');
    process.exit(1);
  }
  const namen = szenen.map(s => s.name);
  console.log('[slobs] Szenen (' + namen.length + '): ' + namen.join(' | '));
  [CONF.szenen.start, CONF.szenen.ende, CONF.ausfallSzenen].forEach(n => {
    if (n && !namen.includes(n)) console.error('  ! Szene fehlt: "' + n + '"');
  });

  if (CONF.test) {
    console.log('[test] OK — nichts geschaltet. Regie-Modus: node tools/regie.js --token <TOKEN>');
    process.exit(0);
  }

  const szenenId = name => { const s = szenen.find(x => x.name === name); return s ? s.id : null; };
  let zuletztGegriffen = 0;
  let heissSeit = 0;

  async function schalte(name, grund) {
    const id = szenenId(name);
    if (!id) { console.error('  ! Szene unbekannt: ' + name); return; }
    try {
      await slobs.frage('ScenesService', 'makeSceneActive', [id]);
      console.log('[regie] ' + new Date().toLocaleTimeString('de-DE') + '  ->  ' + name + '  (' + grund + ')');
    } catch (e) { console.error('[regie] Schalten fehlgeschlagen: ' + e.message); }
  }

  function greifen() { return Date.now() - CONF.abkuehlenMs > zuletztGegriffen; }

  function behandleEvent(ev) {
    if (ev.type === 'session-started' && greifen()) {
      zuletztGegriffen = Date.now();
      schalte(CONF.szenen.start, 'Lights out — Rennen gestartet');
    } else if (ev.type === 'session-ended' && greifen()) {
      zuletztGegriffen = Date.now();
      schalte(CONF.szenen.ende, 'Zielflagge — Session beendet');
    } else if (ev.type === 'retirement' && CONF.ausfallSekunden > 0 && CONF.ausfallSzenen && greifen()) {
      zuletztGegriffen = Date.now();
      const grund = 'Ausfall: ' + (ev.text || '').slice(0, 40);
      schalte(CONF.ausfallSzenen, grund).then(() => {
        setTimeout(() => schalte(CONF.rueckSzenen, 'zurueck zur Rennansicht'), CONF.ausfallSekunden * 1000);
      });
    }
  }

  function duellWacht(msg) {
    if (CONF.duellSekunden <= 0 || !CONF.duellSzenen) return;
    const reihen = (msg.standings || []).filter(r => r.resultStatus === 'Active');
    const heiss = reihen.some(r => r.pitStatus === 'None' && r.gapToCarInFrontMs > 0 && r.gapToCarInFrontMs < 300);
    if (heiss) {
      if (!heissSeit) heissSeit = Date.now();
      else if (Date.now() - heissSeit > CONF.duellSekunden * 1000 && greifen()) {
        zuletztGegriffen = Date.now();
        schalte(CONF.duellSzenen, 'Duell brennt seit ' + CONF.duellSekunden + 's');
        heissSeit = 0;
      }
    } else heissSeit = 0;
  }

  function verbindeTelemetrie() {
    wsVerbinde(CONF.telemetry, {
      onMessage: text => {
        let msg;
        try { msg = JSON.parse(text); } catch { return; }
        if (msg.t === 'event') behandleEvent(msg);
        else if (msg.t === 'state') duellWacht(msg);
        else if (msg.t === 'commentary') behandleKommentar(msg);
      },
      onClose: () => setTimeout(verbindeTelemetrie, 4000),
    });
  }

  let zuletztKommentar = 0;
  function behandleKommentar(msg) {
    if (!CONF.kommentarSzenen || !msg.text) return;
    const jetzt = Date.now();
    if (jetzt - zuletztKommentar < 60000) return; // nicht jede Zeile -> Szene
    zuletztKommentar = jetzt;
    schalte(CONF.kommentarSzenen, 'Kommentar: ' + msg.text.slice(0, 40)).then(() => {
      setTimeout(() => schalte(CONF.rueckSzenen, 'zurueck zur Rennansicht'), CONF.kommentarSekunden * 1000);
    });
  }

  console.log('[regie] Regeln: start="' + CONF.szenen.start + '" ende="' + CONF.szenen.ende + '"');
  console.log('[regie] Telemetrie: ' + CONF.telemetry + '  (Strg+C beendet)');
  verbindeTelemetrie();
}

main();
