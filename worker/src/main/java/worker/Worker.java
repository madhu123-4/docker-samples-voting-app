package worker;

/*
 * Worker.java — Java vote worker
 *
 * What changed from the original and WHY:
 *
 * 1. REDIS RECONNECTION
 *    Original: connected once; if Redis dropped, the process crashed.
 *    Fix: reconnectToRedis() is called inside the main loop whenever a
 *    JedisConnectionException is caught. Worker self-heals without a restart.
 *
 * 2. DB RECONNECTION
 *    Original: no reconnection logic for Postgres at all.
 *    Fix: if updateVote() throws SQLException (connection dropped, timeout, etc.)
 *    the main loop calls reconnectToDB() and retries the vote once.
 *
 * 3. UPSERT INSTEAD OF TRY/CATCH INSERT→UPDATE
 *    Original pattern:  try { INSERT } catch (SQLException) { UPDATE }
 *    Problems: two round-trips, masks all SQLExceptions including real ones.
 *    Fix: single atomic  INSERT … ON CONFLICT (id) DO UPDATE SET vote = ?
 *
 * 4. STRUCTURED LOGGING
 *    Original: System.err.printf scattered everywhere with no levels.
 *    Fix: java.util.logging (JUL) with INFO / WARNING / SEVERE levels.
 *    Can be replaced with SLF4J/Logback by swapping the Logger lines only.
 *
 * 5. GRACEFUL SHUTDOWN
 *    Original: infinite loop with no way to stop cleanly.
 *    Fix: Runtime.getRuntime().addShutdownHook() sets a volatile boolean flag
 *    so the loop exits cleanly on SIGTERM (docker stop) or Ctrl-C.
 *
 * 6. CONFIGURATION FROM ENVIRONMENT VARIABLES
 *    Original: hard-coded hostnames "redis" and "db", empty password.
 *    Fix: reads REDIS_HOST, DB_HOST, DB_USER, DB_PASS from env,
 *    falling back to the original defaults so existing deployments still work.
 *
 * 7. TRY-WITH-RESOURCES ON PREPARED STATEMENTS
 *    Original: PreparedStatement never closed → connection handle leak over time.
 *    Fix: all PreparedStatement objects wrapped in try-with-resources.
 */

import redis.clients.jedis.Jedis;
import redis.clients.jedis.exceptions.JedisConnectionException;

import java.sql.*;
import java.util.logging.Level;
import java.util.logging.Logger;

import org.json.JSONObject;

class Worker {

    // ── Logger ────────────────────────────────────────────────────────────────
    private static final Logger log = Logger.getLogger(Worker.class.getName());

    // ── Config from environment variables ────────────────────────────────────
    private static final String REDIS_HOST = env("REDIS_HOST", "redis");
    private static final String DB_HOST    = env("DB_HOST",    "db");
    private static final String DB_USER    = env("DB_USER",    "postgres");
    private static final String DB_PASS    = env("DB_PASS",    "");
    private static final String DB_NAME    = env("DB_NAME",    "postgres");

    // ── Shutdown flag (set by SIGTERM / SIGINT shutdown hook) ─────────────────
    private static volatile boolean running = true;

    // ─────────────────────────────────────────────────────────────────────────

    public static void main(String[] args) {
        // Register graceful shutdown hook — called on SIGTERM (docker stop) or Ctrl-C
        Runtime.getRuntime().addShutdownHook(new Thread(() -> {
            log.info("Shutdown signal received, stopping worker…");
            running = false;
        }));

        Jedis     redis  = connectToRedis(REDIS_HOST);
        Connection dbConn = connectToDB(DB_HOST);

        log.info("Watching vote queue…");

        while (running) {
            try {
                // Reconnect Redis if the connection dropped
                if (!redis.isConnected()) {
                    log.warning("Redis connection lost — reconnecting…");
                    redis = connectToRedis(REDIS_HOST);
                }

                // BLPOP: blocks until an item arrives (0 = no timeout).
                // Returns [key, value]; we want index 1.
                String voteJSON = redis.blpop(0, "votes").get(1);

                JSONObject voteData = new JSONObject(voteJSON);
                String voterID = voteData.getString("voter_id");
                String vote    = voteData.getString("vote");

                log.info(String.format("Processing vote for '%s' by '%s'", vote, voterID));

                try {
                    updateVote(dbConn, voterID, vote);
                } catch (SQLException e) {
                    // DB connection dropped — reconnect and retry this vote once
                    log.log(Level.WARNING, "DB error — reconnecting and retrying vote…", e);
                    dbConn = connectToDB(DB_HOST);
                    updateVote(dbConn, voterID, vote);
                }

            } catch (JedisConnectionException e) {
                log.log(Level.WARNING, "Redis connection error — reconnecting…", e);
                redis = connectToRedis(REDIS_HOST);

            } catch (SQLException e) {
                // Retry also failed — log and continue (vote is NOT re-queued here;
                // add a dead-letter queue for production use)
                log.log(Level.SEVERE, "Failed to persist vote after reconnect", e);
            }
        }

        // Clean up on exit
        try { dbConn.close(); }  catch (SQLException ignored) {}
        redis.close();
        log.info("Worker stopped cleanly.");
    }

    // ── Vote persistence ──────────────────────────────────────────────────────

    /**
     * Upserts a vote: inserts if the voter is new, updates if they changed their mind.
     *
     * Uses PostgreSQL's  ON CONFLICT (id) DO UPDATE  syntax (one round-trip, atomic).
     *
     * Original pattern was:
     *   try { INSERT } catch (SQLException) { UPDATE }
     * — which required two round-trips and swallowed all SQLExceptions.
     */
    static void updateVote(Connection dbConn, String voterID, String vote)
            throws SQLException {
        final String sql =
            "INSERT INTO votes (id, vote) VALUES (?, ?) " +
            "ON CONFLICT (id) DO UPDATE SET vote = EXCLUDED.vote";

        // try-with-resources: PreparedStatement is always closed, even on exception
        try (PreparedStatement st = dbConn.prepareStatement(sql)) {
            st.setString(1, voterID);
            st.setString(2, vote);
            st.executeUpdate();
        }
    }

    // ── Connection helpers ────────────────────────────────────────────────────

    /**
     * Connects to Redis, retrying every second until successful.
     * Uses PING to verify the connection before returning.
     */
    static Jedis connectToRedis(String host) {
        while (running) {
            try {
                Jedis conn = new Jedis(host);
                conn.ping();   // throws JedisConnectionException if unreachable
                log.info("Connected to Redis at " + host);
                return conn;
            } catch (JedisConnectionException e) {
                log.warning("Waiting for Redis at " + host + "… (" + e.getMessage() + ")");
                sleep(1000);
            }
        }
        throw new IllegalStateException("Worker stopped before Redis became available.");
    }

    /**
     * Connects to PostgreSQL, retrying every second until successful.
     * Also ensures the votes table exists (CREATE TABLE IF NOT EXISTS).
     */
    static Connection connectToDB(String host) {
        final String url = "jdbc:postgresql://" + host + "/" + DB_NAME;
        Connection conn  = null;

        while (running && conn == null) {
            try {
                conn = DriverManager.getConnection(url, DB_USER, DB_PASS);
                log.info("Connected to Postgres at " + host);
            } catch (SQLException e) {
                log.warning("Waiting for Postgres at " + host + "… (" + e.getMessage() + ")");
                sleep(1000);
            }
        }

        if (conn == null) {
            throw new IllegalStateException("Worker stopped before Postgres became available.");
        }

        // Create schema — idempotent, safe to run on every startup
        try (PreparedStatement st = conn.prepareStatement(
                "CREATE TABLE IF NOT EXISTS votes (" +
                "  id   VARCHAR(255) NOT NULL UNIQUE, " +
                "  vote VARCHAR(255) NOT NULL" +
                ")")) {
            st.executeUpdate();
            log.info("Schema ready.");
        } catch (SQLException e) {
            log.log(Level.SEVERE, "Failed to create schema", e);
            System.exit(1);
        }

        return conn;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            running = false;
        }
    }

    static String env(String key, String fallback) {
        String val = System.getenv(key);
        return (val != null && !val.isBlank()) ? val : fallback;
    }
}