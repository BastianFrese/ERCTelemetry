/**
 * ERCTelemetry overlay client (wire protocol v1).
 *
 * Pages include this file, then:
 *   const overlay = connectOverlay({
 *     onHello: (msg) => {},                     // protocol handshake
 *     onState: (msg) => {},                     // ≤5 Hz: { session, standings, results }
 *     onPlayer: (msg) => {},                    // ~30 Hz: { player, rival } frames
 *     onEvent: (msg) => {},                     // immediate race-control events
 *     onMap: (msg) => {},                       // ~10 Hz: { uid, positions: [{carIndex, isPlayer, x, z}] }
 *     onConfig: (blocks) => {},                 // block visibility: { "com.battles": true, ... }
 *     onLayout: (msg) => {},                    // fitted circuit outline (world metres): { track, points: [[x,z],…], startX, startZ }
 *     onCommentary: (msg) => {},                // immediate AI-commentator lines: { text, seq }
 *   });
 *
 * Every config push is also stored on `window.overlayBlocks` (plain object,
 * {} when absent), so pages can re-apply visibility at any time — missing
 * keys mean the block stays visible. Times arrive raw in milliseconds — use
 * the shared format helpers below.
 * overlay.sendSelect(carIndex) switches which car fills the "player" slot
 * (the broadcast player or the global rival); sendSelect(null) resets it.
 */
(function () {
    'use strict';

    var RETRY_DELAY_MS = 2000;

    /** Color scheme: 'de' = black-red-gold, absent/null = classic. Toggles a body
     *  class so pages style the scheme purely through CSS variable overrides. */
    function applyScheme(scheme) {
        document.body.classList.toggle('scheme-de', scheme === 'de');
    }

    function connectOverlay(handlers) {
        handlers = handlers || {};
        var socket = null;
        var stopped = false;

        function connect() {
            if (stopped) {
                return;
            }

            var wsUrl = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws';
            socket = new WebSocket(wsUrl);

            socket.addEventListener('open', function () {
                document.body.classList.add('overlay-connected');
            });

            socket.addEventListener('message', function (ev) {
                var msg;
                try {
                    msg = JSON.parse(ev.data);
                } catch (e) {
                    return; // malformed frame — ignore, next messages still parse
                }

                switch (msg.t) {
                    case 'hello':
                        applyScheme(msg.scheme);
                        if (handlers.onHello) handlers.onHello(msg);
                        break;
                    case 'state':
                        applyScheme(msg.scheme); // last value wins → live scheme switch
                        if (handlers.onState) handlers.onState(msg);
                        break;
                    case 'player':
                        if (handlers.onPlayer) handlers.onPlayer(msg);
                        break;
                    case 'event':
                        if (handlers.onEvent) handlers.onEvent(msg);
                        break;
                    case 'map':
                        if (handlers.onMap) handlers.onMap(msg);
                        break;
                    case 'tracklayout':
                        if (handlers.onLayout) handlers.onLayout(msg);
                        break;
                    case 'config':
                        window.overlayBlocks = msg.blocks || {};
                        if (handlers.onConfig) handlers.onConfig(window.overlayBlocks);
                        break;
                    case 'commentary':
                        if (handlers.onCommentary) handlers.onCommentary(msg);
                        break;
                }
            });

            socket.addEventListener('close', function () {
                document.body.classList.remove('overlay-connected');
                // OBS keeps browser sources alive even while the app restarts — reconnect.
                setTimeout(connect, RETRY_DELAY_MS);
            });

            socket.addEventListener('error', function () {
                socket.close();
            });
        }

        connect();

        return {
            sendSelect: function (carIndex) {
                // Pages highlight their own selection on the map too (drawCar reads it).
                window.overlaySelectedIndex = (carIndex === null || carIndex === undefined)
                    ? null : carIndex;
                if (socket && socket.readyState === WebSocket.OPEN) {
                    var message = { t: 'select' };
                    if (carIndex !== null && carIndex !== undefined) {
                        message.carIndex = carIndex;
                    }
                    socket.send(JSON.stringify(message));
                }
            },
        };
    }

    /** 91089 → "1:31.089"; empty string for a missing (0) time. */
    function formatLapTime(ms) {
        if (!ms) {
            return '';
        }
        var m = Math.floor(ms / 60000);
        var s = Math.floor((ms % 60000) / 1000);
        var millis = ms % 1000;
        return m + ':' + String(s).padStart(2, '0') + '.' + String(millis).padStart(3, '0');
    }

    /** +23.641 gapped format; "Leader" for 0; "—" for unknown. */
    function formatGap(ms) {
        if (ms === undefined || ms === null) {
            return '';
        }
        if (ms <= 0) {
            return ms === 0 ? 'Leader' : '—';
        }
        return '+' + (ms / 1000).toFixed(3);
    }

    /** Clamps to 0–100 and appends %, for CSS width/height custom properties. */
    function pct(value) {
        return Math.max(0, Math.min(100, value)) + '%';
    }

    /**
     * Self-learning minimap. The game sends no track layout, so the map builds the
     * track outline from the cars' own positions: every reported (x, z) is quantized
     * onto a metre grid and remembered; after a lap or two the visited cells trace the
     * full circuit. Bounds come from the trail (not the live positions), so the map
     * stops rescaling once the track is known and the cars sit on the drawn line.
     * Returns { onState, onMap } to feed from connectOverlay.
     */
    function createMap(canvas, titleEl) {
        var ctx = canvas.getContext('2d');
        var trackLayer = document.createElement('canvas'); // redrawn only when the trail grows
        trackLayer.width = canvas.width;
        trackLayer.height = canvas.height;
        var tctx = trackLayer.getContext('2d');

        var GRID = 3;          // metres per trail cell
        var PAD = 18;          // canvas padding around the track
        var MIN_VIEW = 100;    // metres — never zoom in beyond this (grid-sized start)
        var trail = new Map(); // "ix,iz" → true
        var bounds = null;     // {minX, maxX, minZ, maxZ} in metres, from trail cells
        var trackDirty = true;
        var uid = null;
        var numbers = {};      // carIndex → race number (from standings)
        var playerName = '';
        var playerPos = null;
        var playerLap = null;

        // Fitted-layout state: the real circuit outline (world metres) pushed by the
        // app once the Core-side fit locks — replaces the self-learning trail.
        var layout = null;
        var layoutStartFinish = null;
        var statusByCar = {};  // carIndex → resultStatus (DNF filter)
        var trackName = '';    // circuit name from the session message

        function colors() {
            var cs = getComputedStyle(document.body);
            return {
                player: cs.getPropertyValue('--overlay-accent').trim() || '#e10600',
                others: cs.getPropertyValue('--overlay-muted').trim() || '#b8bfcb',
                track: cs.getPropertyValue('--overlay-track').trim() || 'rgba(184,191,203,0.30)',
            };
        }

        /** World (x, z) → canvas pixel. +Z maps up; aspect ratio is preserved and the
         *  map is centered. Scale is clamped so a fresh session never zooms into a
         *  single grid cell. */
        function project(x, z) {
            if (!bounds) {
                return { x: 0, y: 0, scale: 0 };
            }
            var spanX = Math.max(bounds.maxX - bounds.minX, 1);
            var spanZ = Math.max(bounds.maxZ - bounds.minZ, 1);
            var drawW = canvas.width - 2 * PAD;
            var drawH = canvas.height - 2 * PAD;
            var maxScale = Math.min(canvas.width, canvas.height) / MIN_VIEW;
            var s = Math.min(Math.min(drawW / spanX, drawH / spanZ), maxScale);
            return {
                x: PAD + (drawW - spanX * s) / 2 + (x - bounds.minX) * s,
                y: PAD + (drawH - spanZ * s) / 2 + (bounds.maxZ - z) * s,
                scale: s,
            };
        }

        function addToTrail(x, z) {
            var ix = Math.round(x / GRID);
            var iz = Math.round(z / GRID);
            var k = ix + ',' + iz;
            if (trail.has(k)) {
                return;
            }
            trail.set(k, true);
            trackDirty = true;
            var qx = ix * GRID;
            var qz = iz * GRID;
            if (!bounds) {
                bounds = { minX: qx, maxX: qx, minZ: qz, maxZ: qz };
                return;
            }
            if (qx < bounds.minX) bounds.minX = qx;
            if (qx > bounds.maxX) bounds.maxX = qx;
            if (qz < bounds.minZ) bounds.minZ = qz;
            if (qz > bounds.maxZ) bounds.maxZ = qz;
        }

        /** Fitted circuit outline from the app (world metres, Core-side fit). Replaces
         *  the self-learning trail: bounds come from the real layout, so outline and
         *  dots share one fixed projection and positions read "correct" on the map. */
        function onLayout(msg) {
            if (!msg || !msg.points || !msg.points.length) {
                return;
            }
            layout = msg.points;
            layoutStartFinish = (msg.startX !== undefined && msg.startX !== null &&
                msg.startZ !== undefined && msg.startZ !== null)
                ? { x: msg.startX, z: msg.startZ }
                : null;
            bounds = { minX: layout[0][0], maxX: layout[0][0], minZ: layout[0][1], maxZ: layout[0][1] };
            layout.forEach(function (pt) {
                if (pt[0] < bounds.minX) bounds.minX = pt[0];
                if (pt[0] > bounds.maxX) bounds.maxX = pt[0];
                if (pt[1] < bounds.minZ) bounds.minZ = pt[1];
                if (pt[1] > bounds.maxZ) bounds.maxZ = pt[1];
            });
            trackDirty = true;
        }

        function drawTrack() {
            if (!trackDirty || !bounds) {
                return;
            }
            trackDirty = false;
            tctx.clearRect(0, 0, trackLayer.width, trackLayer.height);
            var c = colors();
            if (layout) {
                // Real circuit outline: closed polyline through the fitted points,
                // plus the start/finish tick when the app reported the lap line.
                tctx.strokeStyle = c.track;
                tctx.lineWidth = 2.5;
                tctx.lineJoin = 'round';
                tctx.beginPath();
                layout.forEach(function (pt, i) {
                    var p = project(pt[0], pt[1]);
                    if (i === 0) {
                        tctx.moveTo(p.x, p.y);
                    } else {
                        tctx.lineTo(p.x, p.y);
                    }
                });
                tctx.closePath();
                tctx.stroke();
                drawStartFinish();
                return;
            }
            var cellPx = Math.max(GRID * project(0, 0).scale, 4); // solid line, min 4px
            tctx.fillStyle = c.track;
            trail.forEach(function (_, k) {
                var parts = k.split(',');
                var p = project(parseInt(parts[0], 10) * GRID, parseInt(parts[1], 10) * GRID);
                tctx.fillRect(p.x - cellPx / 2, p.y - cellPx / 2, cellPx, cellPx);
            });
        }

        /** Start/finish tick across the outline at the reported lap-line crossing:
         *  short line perpendicular to the nearest outline segment, accent color. */
        function drawStartFinish() {
            if (!layoutStartFinish) {
                return;
            }
            var best = Infinity;
            var dirX = 1, dirZ = 0;
            for (var i = 0; i < layout.length; i++) {
                var a = layout[i];
                var b = layout[(i + 1) % layout.length];
                var mx = (a[0] + b[0]) / 2;
                var mz = (a[1] + b[1]) / 2;
                var dx = mx - layoutStartFinish.x;
                var dz = mz - layoutStartFinish.z;
                var dist = dx * dx + dz * dz;
                if (dist < best) {
                    best = dist;
                    var segLen = Math.sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
                    if (segLen > 1e-3) {
                        dirX = (b[0] - a[0]) / segLen;
                        dirZ = (b[1] - a[1]) / segLen;
                    }
                }
            }
            var HALF = 8; // metres each side across the track
            var p1 = project(layoutStartFinish.x - dirZ * HALF, layoutStartFinish.z + dirX * HALF);
            var p2 = project(layoutStartFinish.x + dirZ * HALF, layoutStartFinish.z - dirX * HALF);
            var accent = getComputedStyle(document.body).getPropertyValue('--overlay-accent').trim() || '#e10600';
            tctx.strokeStyle = accent;
            tctx.lineWidth = 2;
            tctx.beginPath();
            tctx.moveTo(p1.x, p1.y);
            tctx.lineTo(p2.x, p2.y);
            tctx.stroke();
        }

        function drawCar(p, c, suppressLabel) {
            // Select-aware: a page's sendSelect highlights that car like the player.
            var hi = p.isPlayer || p.carIndex === window.overlaySelectedIndex;
            var pt = project(p.x, p.z);
            var r = hi ? 6 : 4;
            ctx.beginPath();
            ctx.arc(pt.x, pt.y, r, 0, 6.2832);
            ctx.fillStyle = hi ? c.player : c.others;
            ctx.fill();
            if (hi) {
                ctx.beginPath();
                ctx.arc(pt.x, pt.y, r + 3, 0, 6.2832);
                ctx.strokeStyle = c.player;
                ctx.lineWidth = 1.5;
                ctx.stroke();
            }
            if (hi) {
                if (p.isPlayer && playerName) {
                    ctx.font = 'bold 10px "Segoe UI", system-ui, sans-serif';
                    ctx.textAlign = 'left';
                    ctx.textBaseline = 'bottom';
                    ctx.fillStyle = c.player;
                    ctx.fillText(playerName, pt.x + r + 3, pt.y - r - 1);
                }
            } else if (!suppressLabel) {
                var num = numbers[p.carIndex];
                if (num !== undefined) {
                    ctx.font = 'bold 8px "Cascadia Mono", Consolas, monospace';
                    ctx.textAlign = 'left';
                    ctx.textBaseline = 'bottom';
                    ctx.fillStyle = c.others;
                    ctx.fillText(String(num), pt.x + r + 2, pt.y - r - 1);
                }
            }
        }

        function draw(positions) {
            if (!positions || !positions.length) {
                titleEl.textContent = 'Waiting for telemetry…';
                return;
            }
            if (!layout) {
                // No fitted outline (yet): keep the self-learning trail going.
                positions.forEach(function (p) { addToTrail(p.x, p.z); });
            }
            drawTrack();
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.drawImage(trackLayer, 0, 0);
            var c = colors();
            // Project once per car: the label overlap check reuses these points.
            var pts = positions.map(function (p) { return project(p.x, p.z); });
            function isOut(idx) {
                var s = statusByCar[idx];
                return s === 'Retired' || s === 'DidNotFinish';
            }
            positions.forEach(function (p, i) {
                if (p.isPlayer || isOut(p.carIndex)) {
                    return; // player drawn last (on top); DNF cars leave the map
                }
                var overlapping = false;
                for (var j = 0; j < positions.length; j++) {
                    if (j !== i && !isOut(positions[j].carIndex) &&
                        Math.abs(pts[j].x - pts[i].x) < 14 && Math.abs(pts[j].y - pts[i].y) < 14) {
                        overlapping = true;
                        break;
                    }
                }
                drawCar(p, c, overlapping);
            });
            positions.forEach(function (p) { if (p.isPlayer) drawCar(p, c, false); }); // player on top
            var t = trackName || 'Track map';
            if (playerPos !== null && playerPos !== undefined) {
                t += ' · P' + playerPos + (playerLap ? ' · Lap ' + playerLap : '');
            }
            titleEl.textContent = t;
        }

        function onState(msg) {
            var newUid = msg.session && msg.session.sessionUid !== undefined ? msg.session.sessionUid : null;
            if (newUid !== uid) {
                uid = newUid;
                trail.clear();
                bounds = null;
                trackDirty = true;
                numbers = {};
                playerName = '';
                playerPos = null;
                playerLap = null;
                layout = null;            // new session → outline comes with the next fit
                layoutStartFinish = null;
                statusByCar = {};
                trackName = '';
            }
            if (msg.session && msg.session.track) {
                trackName = msg.session.track;
            }
            if (msg.standings) {
                msg.standings.forEach(function (row) {
                    numbers[row.carIndex] = row.raceNumber;
                    statusByCar[row.carIndex] = row.resultStatus;
                    if (row.isPlayer) {
                        playerName = row.name;
                        playerPos = row.position;
                        playerLap = row.currentLapNum;
                    }
                });
            }
        }

        /** Canvas size: ?w= and &h= URL params win, otherwise the element's CSS
         *  size (getBoundingClientRect). Re-projects on change via trackDirty. */
        function resize() {
            var params = new URLSearchParams(location.search);
            var w = parseInt(params.get('w'), 10);
            var h = parseInt(params.get('h'), 10);
            if (!w || w < 80) w = 0;
            if (!h || h < 60) h = 0;
            if (!w || !h) {
                var rect = canvas.getBoundingClientRect();
                w = w || Math.round(rect.width) || 340;
                h = h || Math.round(rect.height) || 200;
            }
            if (canvas.width === w && canvas.height === h) {
                return;
            }
            canvas.width = w;
            canvas.height = h;
            canvas.style.width = w + 'px';
            canvas.style.height = h + 'px';
            trackLayer.width = w;
            trackLayer.height = h;
            trackDirty = true;
        }
        window.addEventListener('resize', resize);
        resize();

        return { onState: onState, onMap: draw, onLayout: onLayout };
    }

    window.connectOverlay = connectOverlay;
    window.overlayMap = { create: createMap };
    window.overlayFormat = {
        lapTime: formatLapTime,
        gap: formatGap,
        pct: pct,
    };
})();