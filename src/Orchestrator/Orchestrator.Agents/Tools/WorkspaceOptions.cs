namespace Orchestrator.Agents.Tools;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Orchestrator:Workspace";

    public string WorkspacesRoot { get; set; } = "workspaces";

    public string? SourceRoot { get; set; }

    public IList<string> RepositoryBaseline { get; } =
    [
        "global.json",
        "Directory.Build.props",
        ".editorconfig",
        "src/Shared",
        "src/UrlShortener/UrlShortener.Domain",
        "src/UrlShortener/UrlShortener.Application",
        "src/UrlShortener/UrlShortener.Infrastructure",
        "src/UrlShortener/UrlShortener.Presentation",
        "src/UrlShortener/UrlShortener.Api",
        "tests/UrlShortener.UnitTests",
        "tests/UrlShortener.IntegrationTests",
    ];

    public IList<string> EmptyBaseline { get; } = ["global.json", "Directory.Build.props", ".editorconfig"];

    public string ResolveSourceRoot()
    {
        if (!string.IsNullOrWhiteSpace(SourceRoot))
        {
            return Path.GetFullPath(SourceRoot);
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AgenticSdlc.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Could not locate the source repository; set Orchestrator:Workspace:SourceRoot.");
    }
}
