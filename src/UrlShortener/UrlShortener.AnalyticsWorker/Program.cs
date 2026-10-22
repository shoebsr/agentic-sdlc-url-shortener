using UrlShortener.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(DependencyInjection.RedisConnectionName)))
{
    throw new InvalidOperationException("The analytics worker consumes the Redis click stream; ConnectionStrings:Redis is required.");
}

builder.Services.AddShortenerInfrastructure(builder.Configuration);
builder.Services.AddClickStreamConsumer();

await builder.Build().RunAsync();
