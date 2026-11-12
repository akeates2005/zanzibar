using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Poc.IntegrationTests.Fixtures;

/// <summary>Runs the pinned <c>spicedb</c> binary with the memory datastore on free loopback ports.</summary>
public sealed class NativeSpiceDbHost : ISpiceDbHost
{
    /// <summary>
    /// Every engine this process started and has not yet stopped. If the test run ends abnormally (an unhandled exception, an
    /// aborted run) the exit hook kills them, so a failed test can never leave an orphaned engine behind.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> Live = new();

    static NativeSpiceDbHost() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        foreach (var process in Live.Values)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        }
    };

    private readonly Process _process;
    private readonly StringBuilder _log = new();

    private NativeSpiceDbHost(Process process, int port, string key)
    {
        _process = process;
        Endpoint = $"http://127.0.0.1:{port}";
        PresharedKey = key;
    }

    public string Endpoint { get; }

    public string PresharedKey { get; }

    /// <summary>Freezes the engine process (SIGSTOP): it keeps its port open but answers nothing, like a paused container.</summary>
    public void Pause() => Signal("-STOP");

    /// <summary>Unfreezes the engine (SIGCONT).</summary>
    public void Resume() => Signal("-CONT");

    private void Signal(string signal)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill", [signal, _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not run kill.");
        kill.WaitForExit();
        if (kill.ExitCode != 0) throw new InvalidOperationException($"kill {signal} failed with exit code {kill.ExitCode}.");
    }

    public string Log
    {
        get
        {
            lock (_log) return _log.ToString();
        }
    }

    /// <summary>Starts the process and waits until its gRPC port accepts connections.</summary>
    public static async Task<NativeSpiceDbHost> StartAsync(int? fixedPort = null, string? fixedKey = null)
    {
        var binary = FindBinary();
        var port = fixedPort ?? FreePort();
        var key = fixedKey ?? "it-" + Guid.NewGuid().ToString("N");

        var info = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "serve", "--datastore-engine=memory", $"--grpc-addr=127.0.0.1:{port}", "--metrics-enabled=false",
                     "--telemetry-endpoint=", "--skip-release-check=true",
                 })
            info.ArgumentList.Add(arg);
        info.Environment["SPICEDB_GRPC_PRESHARED_KEY"] = key;

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{binary}'.");
        Live[process.Id] = process;
        var host = new NativeSpiceDbHost(process, port, key);
        process.OutputDataReceived += (_, e) => host.Append(e.Data);
        process.ErrorDataReceived += (_, e) => host.Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"spicedb exited during start-up (code {process.ExitCode}):\n{host.Log}");
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return host;
            }
            catch (SocketException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        await host.DisposeAsync().ConfigureAwait(false);
        throw new TimeoutException($"spicedb did not open port {port} within 20s:\n{host.Log}");
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Live.TryRemove(_process.Id, out _);
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        finally
        {
            _process.Dispose();
        }
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_log) _log.AppendLine(line);
    }

    /// <summary>Finds the binary or throws. Missing tooling fails the run; it never silently skips tests.</summary>
    private static string FindBinary()
    {
        var explicitPath = Environment.GetEnvironmentVariable("POC_SPICEDB_BIN");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir, "spicedb"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "spicedb"));

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "The spicedb binary was not found (set POC_SPICEDB_BIN, or put it on PATH). Integration tests need a real SpiceDB.");
    }
}
