using Orchestrator.Host;
using Orchestrator.Host.Api;
using Orchestrator.Host.Cli;

if (args.Length > 0 && args[0] == "run")
{
    var cli = Host.CreateApplicationBuilder(args);
    cli.Logging.SetMinimumLevel(LogLevel.Warning);
    cli.Services.AddOrchestrator(cli.Configuration);
    using var host = cli.Build();
    return await CliRunner.RunAsync(host.Services, args);
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
builder.Services.AddOrchestrator(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOrchestratorApi();
app.MapGet("/health", () => TypedResults.Ok(new { status = "Healthy" }));

await app.RunAsync();
return 0;

public partial class Program;
