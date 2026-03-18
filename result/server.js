/**
 * result/server.js
 *
 * What changed from the original and WHY:
 *
 * 1. `path` WAS NEVER IMPORTED — RUNTIME CRASH
 *    Original: app.get('/') called path.resolve() but `path` was never required.
 *    Any GET to "/" crashed the server with "ReferenceError: path is not defined".
 *    Fix: added `const path = require('path');`
 *
 * 2. `console.err` → `console.error` — SILENT BUG
 *    Original: console.err("Giving up") — `console.err` does not exist.
 *    It fails silently; the error is swallowed with no output.
 *    Fix: console.error() — the correct method name.
 *
 * 3. `io.set('transports', ['polling'])` REMOVED IN SOCKET.IO 2+
 *    Original used the Socket.IO 1.x API `io.set()` which was deleted in v2.
 *    Running this with socket.io 4 throws "io.set is not a function" on startup.
 *    Fix: transports are now configured in the io() constructor options object.
 *
 * 4. `pg` CALLBACK API → POOL + ASYNC/AWAIT (pg 8.x)
 *    Original used pg.connect() (pg 4.x global pool / callback style).
 *    The global `pg.connect` was removed in pg 7+. pg 8 uses `new Pool()`.
 *    Fix: switched to Pool with async/await — cleaner, no callback nesting.
 *
 * 5. `async.retry` REPLACED WITH SIMPLE RETRY LOOP
 *    Original pulled in the `async` library just for retry logic.
 *    pg 8's Pool handles reconnection internally; a simple async loop
 *    does the same job without an extra dependency.
 *
 * 6. `bodyParser()` CALLED WITHOUT ARGUMENTS — DEPRECATED WARNING
 *    Original: app.use(bodyParser()) — deprecated since Express 4.16.
 *    Fix: app.use(bodyParser.json()) + app.use(bodyParser.urlencoded(...))
 *    which are explicit and don't produce deprecation warnings.
 *
 * 7. GRACEFUL SHUTDOWN
 *    Original had no shutdown handling. `docker stop` would SIGKILL after 10s.
 *    Fix: SIGTERM handler closes the DB pool and HTTP server cleanly.
 *
 * 8. ENVIRONMENT VARIABLE CONFIGURATION
 *    Original hard-coded 'postgres://postgres@db/postgres'.
 *    Fix: reads DB_HOST, DB_USER, DB_PASS, DB_NAME, PORT from env.
 *    Same image works in dev, staging, prod with no rebuilds.
 */

'use strict';

const path           = require('path');          // ← was missing, caused crash
const express        = require('express');
const cookieParser   = require('cookie-parser');
const bodyParser     = require('body-parser');
const methodOverride = require('method-override');
const http           = require('http');
const { Server }     = require('socket.io');     // socket.io 4 named export
const { Pool }       = require('pg');            // pg 8 Pool API

// ── Configuration from environment variables ─────────────────────────────────
const PORT    = process.env.PORT    || 4000;
const DB_HOST = process.env.DB_HOST || 'db';
const DB_USER = process.env.DB_USER || 'postgres';
const DB_PASS = process.env.DB_PASS || 'postgres';
const DB_NAME = process.env.DB_NAME || 'postgres';

// ── Express + HTTP server ─────────────────────────────────────────────────────
const app    = express();
const server = http.createServer(app);

// ── Socket.IO 4 — transports configured here, NOT via io.set() ───────────────
// Original used io.set('transports', ['polling']) — removed in socket.io 2+.
const io = new Server(server, {
  transports: ['polling', 'websocket'],   // allow both; polling as fallback
  cors: {
    origin: '*',
    methods: ['GET', 'POST'],
  },
});

// ── Middleware ────────────────────────────────────────────────────────────────
app.use(cookieParser());
app.use(bodyParser.json());                          // explicit, no deprecation warning
app.use(bodyParser.urlencoded({ extended: false })); // explicit, no deprecation warning
app.use(methodOverride('X-HTTP-Method-Override'));
app.use((req, res, next) => {
  res.header('Access-Control-Allow-Origin',  '*');
  res.header('Access-Control-Allow-Headers', 'Origin, X-Requested-With, Content-Type, Accept');
  res.header('Access-Control-Allow-Methods', 'PUT, GET, POST, DELETE, OPTIONS');
  next();
});
app.use(express.static(path.join(__dirname, 'views')));

// ── Routes ────────────────────────────────────────────────────────────────────
app.get('/', (req, res) => {
  // `path` was never imported in the original — this crashed with ReferenceError
  res.sendFile(path.resolve(__dirname, 'views', 'index.html'));
});

// ── PostgreSQL pool (pg 8) ────────────────────────────────────────────────────
const pool = new Pool({
  host:     DB_HOST,
  user:     DB_USER,
  password: DB_PASS,
  database: DB_NAME,
  // Pool will retry connections automatically; we add startup retry below.
});

// ── Socket.IO connection ──────────────────────────────────────────────────────
io.on('connection', (socket) => {
  socket.emit('message', { text: 'Welcome!' });

  socket.on('subscribe', (data) => {
    socket.join(data.channel);
  });
});

// ── Vote polling ──────────────────────────────────────────────────────────────

/**
 * Collects vote rows into { a: N, b: N } shape.
 */
function collectVotesFromResult(result) {
  const votes = { a: 0, b: 0 };
  result.rows.forEach((row) => {
    votes[row.vote] = parseInt(row.count, 10);
  });
  return votes;
}

/**
 * Queries votes every 1 second and broadcasts to all connected clients.
 * Uses pg 8 pool.query() — no callback nesting, clean async/await.
 */
async function pollVotes() {
  while (true) {
    try {
      const result = await pool.query(
        'SELECT vote, COUNT(id) AS count FROM votes GROUP BY vote'
      );
      const votes = collectVotesFromResult(result);
      io.emit('scores', JSON.stringify(votes));
    } catch (err) {
      console.error('Error querying votes:', err.message);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

// ── Startup: wait for DB then begin polling ───────────────────────────────────

/**
 * Retries connecting to Postgres until successful.
 * pg 8 Pool doesn't surface connection errors until first query,
 * so we do an explicit test query here.
 */
async function connectWithRetry(maxAttempts = 1000, delayMs = 1000) {
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      const client = await pool.connect();
      client.release();
      console.log('Connected to db');
      return;
    } catch (err) {
      console.error(`Waiting for db (attempt ${attempt})…`);
      await new Promise((resolve) => setTimeout(resolve, delayMs));
    }
  }
  // original used `console.err` — that method doesn't exist, error was silent
  console.error('Giving up connecting to db after max attempts.');
  process.exit(1);
}

// ── Graceful shutdown ─────────────────────────────────────────────────────────
// Original had no shutdown handler — docker stop would SIGKILL after 10 seconds.
process.on('SIGTERM', async () => {
  console.log('SIGTERM received — shutting down gracefully…');
  server.close();
  await pool.end();
  process.exit(0);
});

// ── Main ──────────────────────────────────────────────────────────────────────
async function main() {
  await connectWithRetry();
  pollVotes(); // fire-and-forget loop

  server.listen(PORT, () => {
    console.log(`Result service running on port ${PORT}`);
  });
}

main().catch((err) => {
  console.error('Fatal error:', err);
  process.exit(1);
});