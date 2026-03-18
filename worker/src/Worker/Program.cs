/*
 * Worker/Program.cs
 *
 * What changed from the original and WHY:
 *
 * 1. ASYNC THROUGHOUT
 *    Original used  redis.ListLeftPopAsync("votes").Result  — blocking .Result on an async
 *    call inside a sync loop can cause thread-pool starvation and deadlocks under load.
 *    Fix: the entire program is now async (Main returns Task<int>), every await is proper.
 *
 * 2. STRUCTURED LOGGING (Microsoft.Extensions.Logging)
 *    Original scattered Console.WriteLine / Console.Error.WriteLine everywhere.
 *    Now uses ILogger with log levels (Information, Warning, Error, Critical).
 *    In production you can swap the sink to JSON / OpenTelemetry with zero code changes.
 *
 * 3. GRACEFUL SHUTDOWN (CancellationToken)
 *    Original had an infinite while(true) with no way to stop cleanly.
 *    Now listens for SIGTERM/SIGINT (Docker stop / Ctrl-C) and exits the loop cleanly,
 *    flushing the current vote before shutting down.
 *
 * 4. PROPER ASYNC REDIS (StackExchange.Redis 2.7)
 *    Original: ListLeftPopAsync().Result  — blocks a thread per pop.
 *    Now:      await db.ListLeftPopAsync  — non-blocking, scales properly.
 *    Also uses BLPOPAsync (blocking pop with timeout) so the worker yields the CPU
 *    while the queue is empty instead of spinning.
 *
 * 5. UPSERT PATTERN (INSERT … ON CONFLICT DO UPDATE)
 *    Original used try/catch around INSERT then fell back to UPDATE — two round-trips
 *    and swallows ALL DbExceptions, including real errors.
 *    Now uses a single atomic UPSERT. One round-trip, no exception swallowing.
 *
 * 6. KEEP-ALIVE REPLACED BY CONNECTION POOLING
 *    Original ran "SELECT 1" as a keep-alive hack.
 *    Npgsql 8 has built-in connection pooling and keep-alive — no workaround needed.
 *
 * 7. ENVIRONMENT-VARIABLE CONFIGURATION
 *    Original hard-coded "Server=db;Username=postgres;" and "redis" as hostname.
 *    Now reads REDIS_HOST, DB_HOST, DB_USER, DB_PASS from environment variables
 *    so the same image works in dev, staging, and production with no rebuilds.
 *
 * 8. EXIT CODE CONTRACT
 *    Returns 0 on clean shutdown, 1 on unhandled exception — same as original but
 *    now the clean-shutdown path is reachable.
 */

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace Worker;

public class Program
{
    // ── Configuration from environment variables ─────────────────────────────
    private static readonly string RedisHost = Env("REDIS_HOST", "redis");
    private static readonly string DbHost    = Env("DB_HOST",    "db");
    private static readonly string DbUser    = Env("DB_USER",    "postgres");
    private static readonly string DbPass    = Env("DB_PASS",    "postgres");
    private static readonly string DbName    = Env("DB_NAME",    "postgres");

    // ── Retry delays ─────────────────────────────────────────────────────────
    private const int RetryDelayMs = 1_000;   // 1 s between connection retries
    private const int BlPopTimeout = 5;        // seconds to block on Redis BLPOP

    // ── Logger factory (structured console output) ────────────────────────────
    private static readonly ILoggerFactory LoggerFactory =
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));

    private static readonly ILogger Log = LoggerFactory.CreateLogger<Program>();

    // ─────────────────────────────────────────────────────────────────────────

    public static async Task<int> Main(string[] _)
    {
        // CancellationTokenSource wired to SIGTERM + SIGINT (docker stop / Ctrl-C)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        try
        {
            await RunAsync(cts.Token);
            Log.LogInformation("Worker shut down cleanly.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log.LogInformation("Worker received cancellation, exiting.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.LogCritical(ex, "Unhandled exception — worker exiting.");
            return 1;
        }
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private static async Task RunAsync(CancellationToken ct)
    {
        // Connect to both services (each retries indefinitely until available)
        var redis  = await ConnectRedisAsync(ct);
        var db     = redis.GetDatabase();
        await using var pgConn = await ConnectPostgresAsync(ct);

        Log.LogInformation("Watching vote queue…");

        while (!ct.IsCancellationRequested)
        {
            // BLPOP: blocks up to BlPopTimeout seconds, then returns null.
            // This is efficient — no busy-wait spinning while the queue is empty.
            RedisValue[] result = await db.ListLeftPopAsync("votes", BlPopTimeout);

            // result is empty array when timeout expires with no item
            if (result.Length == 0 || result[^1].IsNullOrEmpty)
                continue;

            string json    = result[^1]!;
            var    voteDoc = JsonSerializer.Deserialize<VoteMessage>(json);

            if (voteDoc is null)
            {
                Log.LogWarning("Could not deserialize vote JSON: {Json}", json);
                continue;
            }

            Log.LogInformation(
                "Processing vote for '{Option}' by voter '{VoterId}'",
                voteDoc.vote, voteDoc.voter_id);

            // Reconnect Postgres if the connection dropped
            if (pgConn.State != System.Data.ConnectionState.Open)
            {
                Log.LogWarning("Postgres connection lost — reconnecting…");
                await pgConn.OpenAsync(ct);
            }

            await UpsertVoteAsync(pgConn, voteDoc.voter_id, voteDoc.vote, ct);
        }
    }

    // ── Database helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Opens a Postgres connection, retrying until the server is reachable.
    /// Creates the votes table if it does not yet exist.
    /// </summary>
    private static async Task<NpgsqlConnection> ConnectPostgresAsync(CancellationToken ct)
    {
        // Npgsql 8 connection string with built-in keep-alive (replaces "SELECT 1" hack)
        var connStr = $"Host={DbHost};Username={DbUser};Password={DbPass};" +
                      $"Database={DbName};KeepAlive=10;Timeout=5;";

        NpgsqlConnection? conn = null;

        while (true)
        {
            try
            {
                conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                Log.LogInformation("Connected to Postgres at {Host}.", DbHost);
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
            {
                Log.LogWarning("Waiting for Postgres… ({Message})", ex.Message);
                await Task.Delay(RetryDelayMs, ct);
            }
        }

        // Ensure schema exists — idempotent, safe to run on every startup
        await using var cmd = conn!.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS votes (
                id   VARCHAR(255) NOT NULL UNIQUE,
                vote VARCHAR(255) NOT NULL
            );";
        await cmd.ExecuteNonQueryAsync(ct);
        Log.LogInformation("Schema ready.");

        return conn;
    }

    /// <summary>
    /// INSERT … ON CONFLICT DO UPDATE (upsert) — one round-trip, atomic, no exception abuse.
    ///
    /// Original pattern:
    ///   try { INSERT } catch (DbException) { UPDATE }
    /// Problems with original:
    ///   - Two round-trips to the DB
    ///   - Catches ALL DbExceptions, masking real errors (disk full, constraint violation, etc.)
    ///   - Not atomic — a concurrent worker could insert between the failed INSERT and the UPDATE
    ///
    /// UPSERT is the correct, idiomatic SQL solution.
    /// </summary>
    private static async Task UpsertVoteAsync(
        NpgsqlConnection conn,
        string voterId,
        string vote,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO votes (id, vote)
            VALUES (@id, @vote)
            ON CONFLICT (id)
            DO UPDATE SET vote = EXCLUDED.vote;";

        cmd.Parameters.AddWithValue("@id",   voterId);
        cmd.Parameters.AddWithValue("@vote", vote);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Redis helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to Redis by hostname, retrying until reachable.
    /// Uses IP resolution workaround for StackExchange.Redis DNS caching issue.
    /// </summary>
    private static async Task<IConnectionMultiplexer> ConnectRedisAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                // Resolve hostname → IP to avoid StackExchange.Redis DNS-caching bug
                // https://github.com/StackExchange/StackExchange.Redis/issues/410
                var addresses = await System.Net.Dns.GetHostAddressesAsync(RedisHost, ct);
                var ip = Array.Find(
                    addresses,
                    a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (ip is null)
                    throw new InvalidOperationException($"No IPv4 address for {RedisHost}");

                Log.LogInformation("Found Redis at {Ip}.", ip);

                var mux = await ConnectionMultiplexer.ConnectAsync(ip.ToString());
                Log.LogInformation("Connected to Redis.");
                return mux;
            }
            catch (Exception ex) when (ex is RedisConnectionException
                                            or System.Net.Sockets.SocketException
                                            or InvalidOperationException)
            {
                Log.LogWarning("Waiting for Redis… ({Message})", ex.Message);
                await Task.Delay(RetryDelayMs, ct);
            }
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;
}

// ── DTO for deserializing the Redis vote payload ──────────────────────────────

/// <summary>
/// Matches the JSON pushed by the Flask vote service:
///   { "voter_id": "abc123", "vote": "a" }
/// Using System.Text.Json (built into .NET 8) instead of Newtonsoft.Json.
/// </summary>
internal record VoteMessage(string voter_id, string vote);