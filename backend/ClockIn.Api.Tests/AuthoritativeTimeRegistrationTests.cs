using System.Globalization;
using ClockIn.Api.Services.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClockIn.Api.Tests;

public sealed class AuthoritativeTimeRegistrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/api/timezone/Europe/Zurich")]
    [InlineData("http://example.com/api/timezone/Europe/Zurich")]
    [InlineData("ftp://example.com/api/timezone/Europe/Zurich")]
    [InlineData("https:///api/timezone/Europe/Zurich")]
    [InlineData("https://example.com/path with spaces")]
    public void Invalid_provider_url_fails_startup_validation(string providerUrl)
    {
        using var services = CreateServices("ProviderUrl", providerUrl);

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("AuthoritativeTime:ProviderUrl must be an absolute HTTPS URL.", exception.Failures);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-TimeSpan.TicksPerMillisecond)]
    [InlineData(int.MaxValue * TimeSpan.TicksPerMillisecond + 1)]
    public void Invalid_timeout_fails_startup_validation(long ticks)
    {
        using var services = CreateServices("Timeout", TimeSpan.FromTicks(ticks).ToString("c", CultureInfo.InvariantCulture));

        var exception = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("AuthoritativeTime:Timeout must be positive and at most 2,147,483,647 milliseconds.", exception.Failures);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(int.MaxValue * TimeSpan.TicksPerMillisecond)]
    public void Finite_timeout_boundaries_are_applied_to_the_http_client(long ticks)
    {
        var timeout = TimeSpan.FromTicks(ticks);
        using var services = CreateServices("Timeout", timeout.ToString("c", CultureInfo.InvariantCulture));
        using var client = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IAuthoritativeTimeClient));

        Assert.Equal(timeout, client.Timeout);
    }

    [Fact]
    public void Registration_uses_configured_provider_and_timeout_with_redirects_disabled()
    {
        const string providerUrl = "https://example.com:8443/time?zone=Europe%2FZurich";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuthoritativeTime:ProviderUrl"] = providerUrl,
            ["AuthoritativeTime:Timeout"] = "00:00:02"
        }).Build();
        using var services = new ServiceCollection().AddAuthoritativeTime(configuration).BuildServiceProvider();

        services.GetRequiredService<IStartupValidator>().Validate();
        Assert.IsType<WorldTimeClient>(services.GetRequiredService<IAuthoritativeTimeClient>());
        Assert.Equal(providerUrl, services.GetRequiredService<IOptions<AuthoritativeTimeOptions>>().Value.ProviderUrl);

        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAuthoritativeTimeClient));
        Assert.Equal(TimeSpan.FromSeconds(2), client.Timeout);

        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IAuthoritativeTimeClient));
        while (handler is DelegatingHandler { InnerHandler: { } innerHandler })
        {
            handler = innerHandler;
        }

        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    private static ServiceProvider CreateServices(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"AuthoritativeTime:{key}"] = value
        }).Build();

        return new ServiceCollection().AddAuthoritativeTime(configuration).BuildServiceProvider();
    }
}
