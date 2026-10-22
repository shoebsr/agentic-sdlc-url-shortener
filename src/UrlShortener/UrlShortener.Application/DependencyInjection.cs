using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Application.Links;

namespace UrlShortener.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddShortenerApplication(this IServiceCollection services)
    {
        services.AddOptions<ShortenerOptions>()
            .BindConfiguration(ShortenerOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ShortLinkService>();
        return services;
    }
}
