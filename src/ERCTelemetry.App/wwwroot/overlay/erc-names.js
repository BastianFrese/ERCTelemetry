/**
 * erc-names.js — Fahrernamen-Matcher für die Overlay-Seiten.
 *
 * Lädt data/erc-drivers.json (vom Scraper tools/erc-drivers.js) und ordnet
 * Ingame-Namen aus der Telemetrie den ErdiHub-Profilen zu:
 *
 *   window.ercNames.lade(rueckruf)   // einmal laden (löst rueckruf sofort aus,
 *                                    //  auch wenn die Datei fehlt)
 *   window.ercNames.zuFahrer(name)   // -> {name, team, teamLogo, discord, saison,...} | null
 *
 * Matching-Stufen: exakt (ignoriert Groß/Klein) -> als Teilstring im
 * Ingame-Namen -> umgekehrt. "MarcelSchumi" findet "ERC | MarcelSchumi".
 */
(function () {
  'use strict';
  var daten = null;         // {fahrer:[...]} oder null
  var fertig = [];         // Rückrufe, die auf das Laden warten

  function normal(name) {
    return String(name || '').toLowerCase()
      .replace(/\(.*?\)/g, '')          // "(EA)"-Zusätze weg
      .replace(/[^a-z0-9äöüß| -]/gi, '')
      .replace(/\s+/g, ' ').trim();
  }

  function indexBauen() {
    var index = { exakt: {}, ingame: [] };
    (daten.fahrer || []).forEach(function (f) {
      if (f.name) index.exakt[normal(f.name)] = f;
      if (f.ingame) {
        index.exakt[normal(f.ingame)] = f;
        index.ingame.push({ f: f, n: normal(f.ingame) });
      }
    });
    return index;
  }
  var index = null;

  function zuFahrer(name) {
    if (!index || !name) return null;
    var n = normal(name);
    if (!n) return null;
    if (index.exakt[n]) return index.exakt[n];
    // Teilstring: "marcelschumi" in "erc | marcelschumi"?
    for (var i = 0; i < index.ingame.length; i++) {
      var e = index.ingame[i];
      if (e.n.indexOf(n) >= 0 || n.indexOf(e.n) >= 0) return e.f;
    }
    return null;
  }

  function lade(rueckruf) {
    if (daten) { rueckruf(daten); return; }
    fertig.push(rueckruf);
    if (fertig.length > 1) return; // es lädt schon
    fetch('data/erc-drivers.json', { cache: 'no-store' })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (d) {
        daten = d && d.fahrer ? d : { fahrer: [] };
        index = indexBauen();
        fertig.forEach(function (f) { try { f(daten); } catch (e) { /* Overlay darf nicht sterben */ } });
        fertig = [];
      })
      .catch(function () {
        // ohne Datei läuft alles mit reinen Telemetrie-Namen weiter
        daten = { fahrer: [] };
        index = indexBauen();
        fertig.forEach(function (f) { try { f(daten); } catch (e) { /* dito */ } });
        fertig = [];
      });
  }

  window.ercNames = { lade: lade, zuFahrer: zuFahrer };
})();