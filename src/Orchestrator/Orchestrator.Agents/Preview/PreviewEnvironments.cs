using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Orchestrator.Agents.Tools;

namespace Orchestrator.Agents.Preview;

public sealed class PreviewOptions
{
    public const string SectionName = "Orchestrator:Preview";

    public bool Enabled { get; set; } = true;

    public int PortRangeStart { get; set; } = 5100;

    public int PortCount { get; set; } = 10;

    public string BindHost { get; set; } = "localhost";

    public string PublicHost { get; set; } = "localhost";

    public int StartupTimeoutSeconds { get; set; } = 240;

    public string Project { get; set; } = "src/UrlShortener/UrlShortener.Api";
}

public sealed record PreviewInfo(string RunId, Uri BaseUrl, int Port, DateTimeOffset StartedAt, string LogFile)
{
    public Uri AppUrl => new(BaseUrl, "ui/");

    public Uri SwaggerUrl => new(BaseUrl, "swagger");

    public Uri HealthUrl => new(BaseUrl, "health/ready");
}

public sealed class PreviewEnvironments(IOptions<PreviewOptions> options, IHttpClientFactory httpClients, TimeProvider clock) : IDisposable
{
    private readonly ConcurrentDictionary<string, RunningPreview> _previews = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _portGate = new(1, 1);

    public PreviewOptions Options => options.Value;

    public IReadOnlyCollection<PreviewInfo> Active => [.. _previews.Values.Where(p => !p.Process.HasExited).Select(p => p.Info)];

    public PreviewInfo? Find(string runId) =>
        _previews.TryGetValue(runId, out var preview) && !preview.Process.HasExited ? preview.Info : null;

    public async Task<PreviewInfo> StartAsync(string runId, string workspaceRoot, CancellationToken cancellationToken)
    {
        await StopAsync(runId);
        var settings = options.Value;
        var previewDirectory = Path.Combine(workspaceRoot, ".preview");
        Directory.CreateDirectory(previewDirectory);
        var log = Path.Combine(previewDirectory, "preview.log");

        await _portGate.WaitAsync(cancellationToken);
        RunningPreview preview;
        try
        {
            var port = AllocatePort(settings);
            var baseUrl = new Uri($"http://{settings.PublicHost}:{port}/");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workspaceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "run", "--project", settings.Project, "--no-launch-profile" })
            {
                start.ArgumentList.Add(argument);
            }

            ProcessToolbox.Sanitize(start);
            start.Environment["ASPNETCORE_URLS"] = $"http://{settings.BindHost}:{port}";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["Database__Provider"] = "Sqlite";
            start.Environment["Database__InitializeOnStartup"] = "true";
            start.Environment["ConnectionStrings__Shortener"] = $"Data Source={Path.Combine(previewDirectory, "shortener.db")}";
            start.Environment["ConnectionStrings__Redis"] = string.Empty;
            start.Environment["Shortener__PublicBaseUrl"] = baseUrl.AbsoluteUri.TrimEnd('/');
            start.Environment["Analytics__FingerprintKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var writer = new StreamWriter(log, append: false) { AutoFlush = true };
            process.OutputDataReceived += (_, e) => Write(writer, e.Data);
            process.ErrorDataReceived += (_, e) => Write(writer, e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            preview = new RunningPreview(new PreviewInfo(runId, baseUrl, port, clock.GetUtcNow(), log), process, writer);
            _previews[runId] = preview;
        }
        finally
        {
            _portGate.Release();
        }

        try
        {
            await WaitUntilHealthyAsync(preview, TimeSpan.FromSeconds(settings.StartupTimeoutSeconds), cancellationToken);
        }
        catch
        {
            await StopAsync(runId);
            throw;
        }

        return preview.Info;
    }

    public Task StopAsync(string runId)
    {
        if (_previews.TryRemove(runId, out var preview))
        {
            Terminate(preview);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var preview in _previews.Values)
        {
            Terminate(preview);
        }

        _previews.Clear();
        _portGate.Dispose();
    }

    private async Task WaitUntilHealthyAsync(RunningPreview preview, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = httpClients.CreateClient(nameof(PreviewEnvironments));
        var deadline = clock.GetUtcNow() + timeout;
        var probe = new Uri($"http://localhost:{preview.Info.Port}/health/ready");
        while (clock.GetUtcNow() < deadline)
        {
            if (preview.Process.HasExited)
            {
                throw new InvalidOperationException($"Preview process exited with code {preview.Process.ExitCode}: {Tail(preview.Info.LogFile)}");
            }

            try
            {
                using var response = await http.GetAsync(probe, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken);
        }

        throw new TimeoutException($"Preview did not become healthy within {timeout.TotalSeconds:F0}s: {Tail(preview.Info.LogFile)}");
    }

    private int AllocatePort(PreviewOptions settings)
    {
        var used = _previews.Values.Select(p => p.Info.Port).ToHashSet();
        for (var port = settings.PortRangeStart; port < settings.PortRangeStart + settings.PortCount; port++)
        {
            if (!used.Contains(port) && IsFree(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No free preview port in {settings.PortRangeStart}-{settings.PortRangeStart + settings.PortCount - 1}; stop an existing preview.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void Write(StreamWriter writer, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (writer)
        {
            writer.WriteLine(line);
        }
    }

    private static void Terminate(RunningPreview preview)
    {
        try
        {
            if (!preview.Process.HasExited)
            {
                preview.Process.Kill(entireProcessTree: true);
                preview.Process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            preview.Process.Dispose();
            lock (preview.Log)
            {
                preview.Log.Dispose();
            }
        }
    }

    private static string Tail(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return string.Join(" | ", reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6));
        }
        catch (IOException)
        {
            return "no log";
        }
    }

    private sealed record RunningPreview(PreviewInfo Info, Process Process, StreamWriter Log);
}
