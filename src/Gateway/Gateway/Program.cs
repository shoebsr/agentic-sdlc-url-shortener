using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Ocelot.Provider.Polly;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
builder.Configuration
    .AddJsonFile("ocelot.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"ocelot.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

builder.Services.AddOcelot(builder.Configuration).AddPolly();

var app = builder.Build();

app.Map("/gateway/health", branch => branch.Run(context =>
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync("""{"status":"Healthy"}""");
}));

await app.UseOcelot();
await app.RunAsync();
