using Microsoft.Extensions.Options;

namespace ClockIn.Api.Services.Time;

public static class AuthoritativeTimeServiceCollectionExtensions
{
    public static IServiceCollection AddAuthoritativeTime(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthoritativeTimeOptions>()
            .Bind(configuration.GetSection(AuthoritativeTimeOptions.SectionName))
            .Validate(
                options => Uri.TryCreate(options.ProviderUrl, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && uri.IsWellFormedOriginalString(),
                "AuthoritativeTime:ProviderUrl must be an absolute HTTPS URL.")
            .Validate(
                options => options.Timeout > TimeSpan.Zero
                    && options.Timeout <= TimeSpan.FromMilliseconds(int.MaxValue),
                "AuthoritativeTime:Timeout must be positive and at most 2,147,483,647 milliseconds.")
            .ValidateOnStart();

        services.AddHttpClient<IAuthoritativeTimeClient, WorldTimeClient>((serviceProvider, client) =>
        {
            client.Timeout = serviceProvider.GetRequiredService<IOptions<AuthoritativeTimeOptions>>().Value.Timeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        return services;
    }
}
