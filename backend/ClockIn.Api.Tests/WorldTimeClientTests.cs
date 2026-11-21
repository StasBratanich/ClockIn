using System.Net;
using System.Net.Http.Json;
using ClockIn.Api.Services.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClockIn.Api.Tests;

public sealed class WorldTimeClientTests
{
    [Theory]
    [InlineData("2026-01-15T12:34:56.123456+01:00", "2026-01-15T11:34:56.123456+00:00", "+01:00")]
    [InlineData("2026-07-15T12:34:56.123456+02:00", "2026-07-15T10:34:56.123456+00:00", "+02:00")]
    [InlineData("2026-07-15T12:34:56.1234567+02:00", "2026-07-15T10:34:56.1234567Z", "+02:00")]
    [InlineData("2026-01-15T12:00:00+01:00", "2026-01-15T11:00:00Z", "+01:00")]
    public async Task Valid_response_returns_utc_instant(string local, string utc, string offset)
    {
        var client = CreateClient(new { timezone = "Europe/Zurich", datetime = local, utc_datetime = utc, utc_offset = offset });

        var result = await client.GetCurrentTimeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(DateTimeOffset.Parse(utc), result.Instant);
    }

    [Fact]
    public async Task Wrong_zone_is_invalid() => await AssertInvalid(new { timezone = "Europe/Paris", datetime = "2026-01-15T12:00:00+01:00", utc_datetime = "2026-01-15T11:00:00+00:00", utc_offset = "+01:00" });

    [Theory]
    [InlineData("not-a-date", "2026-01-15T11:00:00+00:00", "+01:00")]
    [InlineData("2026-01-15T12:00:00+01:00", "2026-01-15T12:00:00+00:00", "+01:00")]
    [InlineData("2026-01-15T12:00:00+01:00", "2026-01-15T11:00:00+00:00", "+02:00")]
    [InlineData("2026-01-15T12:00:00+01:00", "2026-01-15T11:00:00+00:00", "++01:00")]
    [InlineData("2026-01-15T12:00:00", "2026-01-15T11:00:00+00:00", "+01:00")]
    [InlineData("OCTOBER 15 2026 12:00:00+01:00", "2026-10-15T11:00:00Z", "+01:00")]
    [InlineData("2026-01-15T12:00:00+01:00", "2026-01-15T11:00:00", "+01:00")]
    public async Task Malformed_or_inconsistent_response_is_invalid(string local, string utc, string offset) => await AssertInvalid(new { timezone = "Europe/Zurich", datetime = local, utc_datetime = utc, utc_offset = offset });

    [Fact]
    public async Task Date_less_timestamps_are_invalid()
    {
        await AssertInvalid(new
        {
            timezone = "Europe/Zurich",
            datetime = "SATURDAY 12:00:00+01:00",
            utc_datetime = "SATURDAY 11:00:00+00:00",
            utc_offset = "+01:00"
        });
    }

    [Theory]
    [InlineData("2026-01-15T12:00:00.12345671+01:00", "2026-01-15T11:00:00.12345672Z")]
    [InlineData("2026-01-15T12:00:00.12345671+01:00", "2026-01-15T11:00:00.1234567Z")]
    [InlineData("2026-01-15T12:00:00.1234567+01:00", "2026-01-15T11:00:00.12345672Z")]
    public async Task Timestamps_with_more_than_seven_fractional_digits_are_invalid(string local, string utc)
    {
        await AssertInvalid(new
        {
            timezone = "Europe/Zurich",
            datetime = local,
            utc_datetime = utc,
            utc_offset = "+01:00"
        });
    }

    [Fact]
    public async Task Non_success_response_is_provider_failure()
    {
        var client = CreateClient(HttpStatusCode.ServiceUnavailable);
        var result = await client.GetCurrentTimeAsync();
        Assert.Equal(AuthoritativeTimeFailureCode.ProviderUnavailable, result.FailureCode);
    }

    [Fact]
    public async Task Timeout_is_typed()
    {
        var client = CreateClient(new TaskCanceledException());
        var result = await client.GetCurrentTimeAsync();
        Assert.Equal(AuthoritativeTimeFailureCode.Timeout, result.FailureCode);
    }

    private static async Task AssertInvalid(object payload)
    {
        var result = await CreateClient(payload).GetCurrentTimeAsync();
        Assert.Equal(AuthoritativeTimeFailureCode.InvalidResponse, result.FailureCode);
    }

    private static WorldTimeClient CreateClient(object payload) =>
        CreateClient(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });

    private static WorldTimeClient CreateClient(HttpStatusCode status) =>
        CreateClient(new HttpResponseMessage(status));

    private static WorldTimeClient CreateClient(Exception exception) =>
        new(new HttpClient(new ThrowingHandler(exception)), Options.Create(new AuthoritativeTimeOptions()), NullLogger<WorldTimeClient>.Instance);

    private static WorldTimeClient CreateClient(HttpResponseMessage response) =>
        new(new HttpClient(new StaticHandler(response)), Options.Create(new AuthoritativeTimeOptions()), NullLogger<WorldTimeClient>.Instance);

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(exception);
    }
}
