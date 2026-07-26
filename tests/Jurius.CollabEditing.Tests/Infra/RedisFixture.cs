using System.Diagnostics;
using System.Net.Sockets;
using StackExchange.Redis;
using Xunit;

namespace Jurius.CollabEditing.Tests.Infra;

/// <summary>
/// Sobe um `redis-server` de verdade para os testes. As garantias que interessam
/// aqui (ordem das operações, corte por quantidade, gravação concorrente) moram
/// em scripts Lua — testar contra um dublê não provaria nada.
///
/// Onde achar o binário, em ordem:
///   1. variável COLLAB_TEST_REDIS_SERVER (caminho completo)
///   2. `redis-server` no PATH
/// Sem binário, os testes que dependem dele são PULADOS (nunca "passam falso").
/// </summary>
public sealed class RedisFixture : IDisposable
{
    private readonly Process _process;
    private readonly string _workingDirectory;

    public string Endpoint { get; }
    public bool Available { get; }
    public string UnavailableReason { get; }

    public RedisFixture()
    {
        var binary = ResolveBinary();
        if (binary is null)
        {
            Available = false;
            UnavailableReason =
                "redis-server não encontrado. Instale-o ou aponte COLLAB_TEST_REDIS_SERVER para o binário.";
            Endpoint = string.Empty;
            return;
        }

        var port = FreePort();
        _workingDirectory = Path.Combine(Path.GetTempPath(), "collab-tests-redis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = binary,
            Arguments = $"--port {port} --save '' --appendonly no --databases 1",
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        Endpoint = $"127.0.0.1:{port}";

        if (!WaitUntilAccepting(port, TimeSpan.FromSeconds(15)))
        {
            Available = false;
            UnavailableReason = $"redis-server não respondeu em {Endpoint}.";
            return;
        }

        Available = true;
        UnavailableReason = string.Empty;
    }

    /// <summary>Cada teste começa com o Redis limpo — salas de um não vazam para o outro.</summary>
    public void Flush()
    {
        if (!Available) return;
        var options = ConfigurationOptions.Parse(Endpoint);
        options.AllowAdmin = true;
        using var connection = ConnectionMultiplexer.Connect(options);
        foreach (var endpoint in connection.GetEndPoints())
        {
            connection.GetServer(endpoint).FlushDatabase();
        }
    }

    private static string ResolveBinary()
    {
        var configured = Environment.GetEnvironmentVariable("COLLAB_TEST_REDIS_SERVER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "redis-server");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool WaitUntilAccepting(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.Dispose();
        }
        catch
        {
            // encerrar o processo de teste é o que importa
        }

        try
        {
            if (_workingDirectory is not null && Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, recursive: true);
            }
        }
        catch
        {
            // pasta temporária
        }
    }
}

[CollectionDefinition("redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
}
