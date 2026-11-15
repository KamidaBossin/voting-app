using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;

namespace Worker
{
    public class Program
    {
        private static NpgsqlConnection _pgsql;
        private static ConnectionMultiplexer _redisConn;
        private static volatile bool _dbOk;
        private static volatile bool _redisOk;

        public static int Main(string[] args)
        {
            try
            {
                string pgHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "db";
                string pgPort = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
                string pgUser = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
                string pgPass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
                string pgDb = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "postgres";
                string defaultConnectionString = $"Host={pgHost};Port={pgPort};Username={pgUser};Password={pgPass};Database={pgDb};";
                string connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
                    ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
                    ?? defaultConnectionString;

                string redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "redis";

                int healthPort = 8081;
                if (int.TryParse(Environment.GetEnvironmentVariable("HEALTH_PORT"), out int parsedHealthPort))
                {
                    healthPort = parsedHealthPort;
                }

                _pgsql = OpenDbConnection(connectionString);
                _redisConn = OpenRedisConnection(redisHost);
                var redis = _redisConn.GetDatabase();

                StartHealthEndpoint(healthPort);

                var definition = new { vote = "", voter_id = "" };
                DateTime lastKeepAlive = DateTime.UtcNow.AddSeconds(-5);

                while (true)
                {
                    Thread.Sleep(100);

                    // Se reconnecter à Redis si la connexion est perdue
                    if (_redisConn == null || !_redisConn.IsConnected)
                    {
                        Console.WriteLine("Reconnecting Redis");
                        _redisConn = OpenRedisConnection(redisHost);
                        redis = _redisConn.GetDatabase();
                    }

                    string json = null;
                    try
                    {
                        json = redis.ListLeftPopAsync("votes").Result;
                        _redisOk = true;
                    }
                    catch (Exception ex)
                    {
                        _redisOk = false;
                        Console.Error.WriteLine($"Redis unreachable: {ex.Message}");
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");

                        // Se reconnecter à PostgreSQL si la connexion est perdue
                        if (!_pgsql.State.Equals(ConnectionState.Open))
                        {
                            Console.WriteLine("Reconnecting DB");
                            RequeueVote(redis, json);
                            ReconnectDb(connectionString);
                        }
                        else
                        {
                            try
                            {
                                UpdateVote(_pgsql, vote.voter_id, vote.vote);
                                _dbOk = true;
                            }
                            catch (DbException ex)
                            {
                                Console.Error.WriteLine($"Vote '{vote.voter_id}' refused: {ex.Message}");
                                RequeueVote(redis, json);
                                ReconnectDb(connectionString);
                            }
                        }
                    }
                    else if ((DateTime.UtcNow - lastKeepAlive).TotalSeconds >= 5)
                    {
                        lastKeepAlive = DateTime.UtcNow;
                        if (!KeepAlive(_pgsql))
                        {
                            ReconnectDb(connectionString);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            NpgsqlConnection connection;

            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    break;
                }
                catch (SocketException)
                {
                    Console.Error.WriteLine("Waiting for db");
                    Thread.Sleep(1000);
                }
                catch (DbException)
                {
                    Console.Error.WriteLine("Waiting for db");
                    Thread.Sleep(1000);
                }
            }

            Console.Error.WriteLine("Connected to db");

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                            id VARCHAR(255) NOT NULL UNIQUE,
                                            vote VARCHAR(255) NOT NULL
                                        )";
                command.ExecuteNonQuery();
            }

            return connection;
        }

        private static void ReconnectDb(string connectionString)
        {
            try
            {
                _pgsql?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Closing old db connection failed: {ex.Message}");
            }

            _dbOk = false;
            _pgsql = OpenDbConnection(connectionString);
        }

        private static bool KeepAlive(NpgsqlConnection connection)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT 1";
                    command.ExecuteNonQuery();
                }

                _dbOk = true;
                return true;
            }
            catch (DbException ex)
            {
                Console.Error.WriteLine($"Keepalive failed: {ex.Message}");
                _dbOk = false;
                return false;
            }
        }

        private static void RequeueVote(IDatabase redis, string json)
        {
            try
            {
                redis.ListRightPushAsync("votes", json).Wait();
                Console.WriteLine("Vote pushed back to the queue");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Vote could not be pushed back: {ex.Message}");
            }
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            while (true)
            {
                try
                {
                    var ipAddress = GetIp(hostname);
                    Console.WriteLine($"Found redis at {ipAddress}");
                    Console.Error.WriteLine("Connecting to redis");
                    _redisOk = true;
                    return ConnectionMultiplexer.Connect(ipAddress);
                }
                catch (Exception ex) when (ex is RedisConnectionException || ex is SocketException || ex is AggregateException)
                {
                    Console.Error.WriteLine("Waiting for redis");
                    Thread.Sleep(1000);
                }
            }
        }

        private static string GetIp(string hostname)
            => Dns.GetHostAddressesAsync(hostname)
                .Result
                .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToString();

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO votes (id, vote) VALUES (@id, @vote)";
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                try
                {
                    command.ExecuteNonQuery();
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    command.CommandText = "UPDATE votes SET vote = @vote WHERE id = @id";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void StartHealthEndpoint(int port)
        {
            var thread = new Thread(() => HealthLoop(port));
            thread.IsBackground = true;
            thread.Start();
        }

        private static void HealthLoop(int port)
        {
            while (true)
            {
                try
                {
                    using (var listener = new HttpListener())
                    {
                        listener.Prefixes.Add($"http://*:{port}/");
                        listener.Start();
                        Console.WriteLine($"Health endpoint listening on port {port}");

                        while (true)
                        {
                            var context = listener.GetContext();
                            try
                            {
                                WriteHealth(context);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"Health handler failed: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Health endpoint unavailable: {ex.Message}");
                    Thread.Sleep(5000);
                }
            }
        }

        private static void WriteHealth(HttpListenerContext context)
        {
            string body = $"{{\"worker\":\"up\",\"redis\":\"{(_redisOk ? "ok" : "ko")}\",\"database\":\"{(_dbOk ? "ok" : "ko")}\"}}";
            byte[] payload = Encoding.UTF8.GetBytes(body);

            context.Response.StatusCode = (_redisOk && _dbOk) ? 200 : 503;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            context.Response.OutputStream.Write(payload, 0, payload.Length);
            context.Response.OutputStream.Close();
        }
    }
}
