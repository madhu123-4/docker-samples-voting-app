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
    private static readonly string RedisHost = Env("REDIS_HOST", "redis");
    private static readonly string DbHost    = Env("DB_HOST",    "db");
    private static readonly string DbUser    = Env("DB_USER",    "postgres");
    private static readonly string DbPass    = Env("DB_PASS",    "postgres");
    private static readonly string DbName    = Env("DB_NAME",    "postgres");

    private const int RetryDelayMs = 1000;

    private static readonly ILoggerFactory LoggerFactory =
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));

    private static readonly ILogger Log = LoggerFactory.CreateLogger<Program>();

    public static async Task<int> Main(string[] _)
    {
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
            Log.LogInformation("Worker cancelled.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.LogCritical(ex, "Unhandled exception - worker exiting.");
            return 1;
        }
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        var mux = await ConnectRedisAsync(ct);
        var db  = mux.GetDatabase();
        await using var pgConn = await ConnectPostgresAsync(ct);

        Log.LogInformation("Watching vote queue...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                RedisValue item = await db.ListLeftPopAsync("votes");

                if (item.IsNullOrEmpty)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                string json = item.ToString();

                var voteDoc = JsonSerializer.Deserialize<VoteMessage>(json);

                if (voteDoc is null)
                {
                    Log.LogWarning("Bad vote JSON: {Json}", json);
                    continue;
                }

                Log.LogInformation("Processing vote for {Option} by {VoterId}",
                    voteDoc.vote, voteDoc.voter_id);

                if (pgConn.State != System.Data.ConnectionState.Open)
                    await pgConn.OpenAsync(ct);

                await UpsertVoteAsync(pgConn, voteDoc.voter_id, voteDoc.vote, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.LogError(ex, "Error processing vote, retrying...");
                await Task.Delay(RetryDelayMs, ct);
            }
        }
    }

    private static async Task<NpgsqlConnection> ConnectPostgresAsync(CancellationToken ct)
    {
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
            catch (Exception ex) when (ex is NpgsqlException or
                                       System.Net.Sockets.SocketException)
            {
                Log.LogWarning("Waiting for Postgres... ({Msg})", ex.Message);
                await Task.Delay(RetryDelayMs, ct);
            }
        }

        await using var cmd = conn!.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
            id   VARCHAR(255) NOT NULL UNIQUE,
            vote VARCHAR(255) NOT NULL);";
        await cmd.ExecuteNonQueryAsync(ct);
        Log.LogInformation("Schema ready.");

        return conn;
    }

    private static async Task UpsertVoteAsync(
        NpgsqlConnection conn, string voterId, string vote, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO votes (id, vote) VALUES (@id, @vote)
            ON CONFLICT (id) DO UPDATE SET vote = EXCLUDED.vote;";
        cmd.Parameters.AddWithValue("@id",   voterId);
        cmd.Parameters.AddWithValue("@vote", vote);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IConnectionMultiplexer> ConnectRedisAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(RedisHost, ct);
                var ip = Array.Find(addresses,
                    a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (ip is null)
                    throw new InvalidOperationException($"No IPv4 for {RedisHost}");

                Log.LogInformation("Found Redis at {Ip}.", ip);
                var mux = await ConnectionMultiplexer.ConnectAsync(ip.ToString());
                Log.LogInformation("Connected to Redis.");
                return mux;
            }
            catch (Exception ex) when (ex is RedisConnectionException or
                                       System.Net.Sockets.SocketException or
                                       InvalidOperationException)
            {
                Log.LogWarning("Waiting for Redis... ({Msg})", ex.Message);
                await Task.Delay(RetryDelayMs, ct);
            }
        }
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) ?? fallback;
}

internal record VoteMessage(string voter_id, string vote);
