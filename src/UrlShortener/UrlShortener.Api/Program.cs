using UrlShortener.Infrastructure.Persistence;
using UrlShortener.Presentation;

var builder = WebApplication.CreateBuilder(args);

builder.AddShortenerWebHost();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseShortenerPipeline();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "URL Shortener API v1");
    options.DocumentTitle = "URL Shortener API";
});

app.MapLinkManagementEndpoints();
if (app.Configuration.GetValue("Hosting:ServeRedirects", true))
{
    app.MapRedirectEndpoints();
}

await app.Services.InitializeShortenerDatabaseAsync();
await app.RunAsync();

public partial class Program;
