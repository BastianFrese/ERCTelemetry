/**
 * ERCTelemetry â€” slobs-rpc.js
 *
 * Einmal-RPC gegen Streamlabs Desktop (SockJS :59650) â€” zum Inspectieren
 * und Skripten des Mixers aus der Kommandozeile:
 *
 *   node tools/slobs-rpc.js AudioService getSources
 *   node tools/slobs-rpc.js ScenesService getScenes
 *   node tools/slobs-rpc.js "<resourceId>" getFilters          # dynamische Ressource
 *   node tools/slobs-rpc.js ConfigService getAppConfig
 *
 * Token: SLOBS_TOKEN-Umgebung oder --token <TOKEN> (nicht einchecken!).
 */
'use strict';
const crypto = require('crypto');
const http = require('http');

const args = process.argv.slice(2);
function argWert(name) {
  const i = args.indexOf('--' + name);
  if (i >= 0 && args[i + 1]) return args[i + 1];
  const a = args.find(x => x.startsWith('--' + name + '='));
  return a ? a.split('=').slice(1).join('=') : null;
}
const CONF = {
  host: argWert('host') || '127.0.0.1',
  port: parseInt(argWert('port'), 10) || 59650,
  token: argWert('token') || process.env.SLOBS_TOKEN || '',
};
// Resource darf auch per SLOBS_RES kommen (PS verschluckt sonst die Quotes)
const RESOURCE = process.env.SLOBS_RES || args[0];

function fehler(t) { console.error(t); process.exit(1); }
if (!CONF.token) fehler('Kein Token: SLOBS_TOKEN setzen oder --token <TOKEN> Ã¼bergeben.');
if (!RESOURCE || !args[1]) fehler('Benutzung: node tools/slobs-rpc.js <resource> <method> [argsJSON]');

// â”€â”€ Mini-WebSocket-Client (1:1 wie in regie.js) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
    socket.on('error', () => {});
    handlers.onOpen && handlers.onOpen();
  });
  req.on('error', e => fehler('[ws] ' + e.message));
  return { send: t => sock.socket && sock.socket.write(rahmen(Buffer.from(t, 'utf8'))) };
}
let buf = Buffer.alloc(0);
function empfangen(d, handlers) {
  buf = Buffer.concat([buf, d]);
  while (true) {
    if (buf.length < 2) return;
    const op = buf[0] & 0x0f;
    let len = buf[1] & 0x7f, off = 2;
    if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
    else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
    if (buf.length < off + len) return;
    const text = buf.slice(off, off + len).toString('utf8');
    buf = buf.slice(off + len);
    if (op === 1 && handlers.onMessage) handlers.onMessage(text);
  }
}
function rahmen(payload) {
  const m = crypto.randomBytes(4);
  const masked = Buffer.from(payload);
  for (let i = 0; i < masked.length; i++) masked[i] ^= m[i & 3];
  let head;
  if (payload.length < 126) head = Buffer.from([0x81, 0x80 | payload.length]);
  else { head = Buffer.alloc(4); head[0] = 0x81; head[1] = 0x80 | 126; head.writeUInt16BE(payload.length, 2); }
  return Buffer.concat([head, m, masked]);
}

// â”€â”€ Einmal-Session: auth -> call -> Ergebnis ausgeben -> exit â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
const sock = wsVerbinde('ws://' + CONF.host + ':' + CONF.port +
  '/api/000/' + crypto.randomBytes(8).toString('hex') + '/websocket', {
  onMessage: text => {
    if (text === 'o') {
      sock.send(JSON.stringify([JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'auth',
        params: { resource: 'TcpServerService', args: [CONF.token] } })]));
      return;
    }
    if (text[0] !== 'a' && text[0] !== 'm') return;
    let liste;
    try { liste = JSON.parse(text.slice(1)); } catch { return; }
    liste.forEach(inner => {
      let msg;
      try { msg = JSON.parse(inner); } catch { return; }
      if (msg.id === 1) {
        if (msg.error) fehler('Auth fehlgeschlagen: ' + JSON.stringify(msg.error));
        sock.send(JSON.stringify([JSON.stringify({ jsonrpc: '2.0', id: 2, method: args[1],
          params: { resource: RESOURCE,
                    args: process.env.SLOBS_ARGS ? JSON.parse(process.env.SLOBS_ARGS)
                      : (args[2] ? JSON.parse(args[2]) : []) } })]));
        return;
      }
      if (msg.id === 2) {
        if (msg.error) fehler('Fehler: ' + JSON.stringify(msg.error, null, 2));
        console.log(JSON.stringify(msg.result, null, 2));
        process.exit(0);
      }
    });
  },
});
setTimeout(() => fehler('Timeout â€” antwortet SLOBS nicht? (Port 59650, Drittanbieter-Verbindungen an?)'), 8000);