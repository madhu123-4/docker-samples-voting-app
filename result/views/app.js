/**
 * views/app.js  —  Result frontend
 *
 * What changed from the original and WHY:
 *
 * 1. REMOVED AngularJS dependency
 *    Original used Angular 1.4.5 just for {{ }} bindings and ng-if.
 *    AngularJS 1.x is End of Life since Dec 2021.
 *    Replaced with ~30 lines of vanilla JS — cleaner, zero frameworks.
 *
 * 2. socket.io CLIENT VERSION MISMATCH FIXED
 *    Original loaded the vendored socket.io.js (v1.x) from views/.
 *    The server now runs socket.io 4.x — v1 client cannot connect to v4 server.
 *    The client is now loaded from /socket.io/socket.io.js (served by the server
 *    itself), which guarantees client and server versions always match.
 *
 * 3. io.connect() OPTIONS
 *    Original: io.connect({transports:['polling']})
 *    The named option changed in socket.io 2+. Now uses the standard
 *    { transports: [...] } at connection time.
 *
 * 4. TOTAL VOTE LABEL
 *    Original had 3 ng-if directives for 0 / 1 / ≥2 votes.
 *    Replaced with a single updateTotal() function.
 */

'use strict';

// DOM references
const bg1       = document.getElementById('background-stats-1');
const bg2       = document.getElementById('background-stats-2');
const pctA      = document.getElementById('pct-a');
const pctB      = document.getElementById('pct-b');
const totalLabel = document.getElementById('total-label');

// ── Socket.IO 4 connection ────────────────────────────────────────────────────
// io() is available globally because /socket.io/socket.io.js is loaded in HTML.
// `transports` option replaces the old io.set('transports', ...) server-side call.
const socket = io({ transports: ['polling', 'websocket'] });

// ── Helpers ───────────────────────────────────────────────────────────────────

function getPercentages(a, b) {
  if (a + b === 0) return { a: 50, b: 50 };
  const pA = Math.round(a / (a + b) * 100);
  return { a: pA, b: 100 - pA };
}

function updateTotal(total) {
  if (total === 0)      totalLabel.textContent = 'No votes yet';
  else if (total === 1) totalLabel.textContent = '1 vote';
  else                  totalLabel.textContent = `${total} votes`;
}

function updateUI(a, b) {
  const pct = getPercentages(a, b);

  // Background split bars
  bg1.style.width = pct.a + '%';
  bg2.style.width = pct.b + '%';

  // Percentage text
  pctA.textContent = pct.a.toFixed(1) + '%';
  pctB.textContent = pct.b.toFixed(1) + '%';

  updateTotal(a + b);
}

// ── Socket.IO event handlers ──────────────────────────────────────────────────

socket.on('connect', () => {
  // Make body visible once connected (matches original opacity transition)
  document.body.style.opacity = 1;
});

socket.on('message', () => {
  // Server sends 'message' on connection — original used this to trigger init
  document.body.style.opacity = 1;
});

socket.on('scores', (json) => {
  const data = JSON.parse(json);
  const a = parseInt(data.a || 0, 10);
  const b = parseInt(data.b || 0, 10);
  updateUI(a, b);
});

socket.on('disconnect', () => {
  console.warn('Disconnected from result server — attempting to reconnect…');
});