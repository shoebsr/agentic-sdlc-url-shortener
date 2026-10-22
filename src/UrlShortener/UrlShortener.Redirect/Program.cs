using UrlShortener.Presentation;

var builder = WebApplication.CreateBuilder(args);

builder.AddShortenerWebHost();

var app = builder.Build();

app.UseShortenerPipeline();
app.MapRedirectEndpoints();

await app.RunAsync();
