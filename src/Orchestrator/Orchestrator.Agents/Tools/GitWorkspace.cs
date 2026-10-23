using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Orchestrator.Core;
using Orchestrator.Core.Abstractions;
using Orchestrator.Core.Model;

namespace Orchestrator.Agents.Tools;

public sealed class GitWorkspaceProvider(IOptions<WorkspaceOptions> options) : IWorkspaceProvider
{
    private readonly ConcurrentDictionary<string, GitWorkspace> _workspaces = new(StringComparer.Ordinal);

    public IWorkspace For(WorkflowRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return _workspaces.GetOrAdd(run.Id, _ => new GitWorkspace(
            Path.Combine(Path.GetFullPath(options.Value.WorkspacesRoot), run.Id),
            options.Value,
            run.Settings.GetValueOrDefault(RunSettings.Baseline, "repository")));
    }
}

public sealed class GitWorkspace : IWorkspace, IDisposable
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] ExcludedDirectories = ["bin", "obj", "TestResults", ".vs"];

    private readonly WorkspaceOptions _options;
    private readonly string _baseline;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public GitWorkspace(string root, WorkspaceOptions options, string baseline)
    {
        Root = root;
        _options = options;
        _baseline = baseline;
    }

    public string Root { get; }

    public void Dispose() => _gate.Dispose();

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public async Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await File.ReadAllTextAsync(Resolve(relativePath), cancellationToken);
    }

    public async Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content.ReplaceLineEndings("\n"), cancellationToken);
    }

    public IEnumerable<string> Files(string pattern = "*")
    {
        EnsureInitializedAsync(CancellationToken.None).GetAwaiter().GetResult();
        return Directory.EnumerateFiles(Root, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal)
                && !ExcludedDirectories.Any(d => f.Contains($"/{d}/", StringComparison.Ordinal) || f.StartsWith(d + "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);
    }

    public async Task<string> CheckpointAsync(string label, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(cancellationToken, "add", "-A");
            await GitAsync(cancellationToken, "commit", "--allow-empty", "--quiet", "-m", label);
            return (await GitAsync(cancellationToken, "rev-parse", "HEAD")).Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(string checkpoint, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(cancellationToken, "reset", "--hard", "--quiet", checkpoint);
            await GitAsync(cancellationToken, "clean", "-fd", "--quiet");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChangeSet> DiffAsync(string fromCheckpoint, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(cancellationToken, "add", "-A");
            var numstat = await GitAsync(cancellationToken, "diff", "--cached", "--numstat", "--no-renames", fromCheckpoint);
            var nameStatus = await GitAsync(cancellationToken, "diff", "--cached", "--name-status", "--no-renames", fromCheckpoint);
            var patch = await GitAsync(cancellationToken, "diff", "--cached", "-U0", "--no-renames", fromCheckpoint);
            return Parse(numstat, nameStatus, patch);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> PatchAsync(string fromCheckpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GitAsync(cancellationToken, "diff", fromCheckpoint, "HEAD");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyPatchAsync(string patchFile, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(cancellationToken, "apply", "--check", "--whitespace=nowarn", patchFile);
            await GitAsync(cancellationToken, "apply", "--whitespace=nowarn", patchFile);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TagAsync(string tag, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(cancellationToken, "tag", "-f", tag);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteTagAsync(string tag, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(cancellationToken, "tag", "-d", tag);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> BaselineCommitAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return (await GitAsync(cancellationToken, "rev-list", "--max-parents=0", "HEAD")).Trim();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (!Directory.Exists(Path.Combine(Root, ".git")))
            {
                await SeedAsync(cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var source = _options.ResolveSourceRoot();
        var entries = _baseline.Equals("empty", StringComparison.OrdinalIgnoreCase) ? _options.EmptyBaseline : _options.RepositoryBaseline;
        foreach (var entry in entries)
        {
            Copy(Path.Combine(source, entry), Path.Combine(Root, entry));
        }

        await File.WriteAllTextAsync(Path.Combine(Root, ".gitignore"), "bin/\nobj/\nTestResults/\n.vs/\n.preview/\n", cancellationToken);
        await GitAsync(cancellationToken, "init", "--quiet", "--initial-branch=main");
        await GitAsync(cancellationToken, "config", "user.name", "sdlc-orchestrator");
        await GitAsync(cancellationToken, "config", "user.email", "orchestrator@sdlc.local");
        await GitAsync(cancellationToken, "config", "core.autocrlf", "false");
        await GitAsync(cancellationToken, "config", "commit.gpgsign", "false");
        await GitAsync(cancellationToken, "add", "-A");
        await GitAsync(cancellationToken, "commit", "--allow-empty", "--quiet", "-m", $"baseline ({_baseline})");
    }

    private static void Copy(string from, string to)
    {
        if (File.Exists(from))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, File.ReadAllText(from).ReplaceLineEndings("\n"));
            return;
        }

        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => ExcludedDirectories.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            Copy(file, Path.Combine(to, relative));
        }
    }

    private string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        return full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new UnauthorizedAccessException($"Path '{relativePath}' escapes the workspace.");
    }

    private async Task<string> GitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, Root, GitTimeout, start =>
        {
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        }, cancellationToken);

        return result.Succeeded
            ? result.StandardOutput
            : throw new InvalidOperationException($"git {arguments[0]} failed: {result.StandardError.Trim()}");
    }

    private static ChangeSet Parse(string numstat, string nameStatus, string patch)
    {
        var kinds = nameStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 2)
            .ToDictionary(p => p[1].Trim(), p => p[0].Trim() switch
            {
                "A" => FileChangeKind.Added,
                "D" => FileChangeKind.Deleted,
                _ => FileChangeKind.Modified,
            }, StringComparer.Ordinal);

        var added = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                current = line[6..].Trim();
                added[current] = new StringBuilder();
            }
            else if (current is not null && line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                added[current].AppendLine(line[1..]);
            }
        }

        var files = numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3)
            .Select(p => new FileChange(
                p[2].Trim(),
                kinds.GetValueOrDefault(p[2].Trim(), FileChangeKind.Modified),
                int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0,
                int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0,
                added.TryGetValue(p[2].Trim(), out var content) ? content.ToString() : string.Empty))
            .ToList();

        return new ChangeSet(files);
    }
}
