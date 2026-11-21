using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ClockIn.Api.Services.Time;

public sealed class WorldTimeClient(
    HttpClient httpClient,
    IOptions<AuthoritativeTimeOptions> options,
    ILogger<WorldTimeClient> logger) : IAuthoritativeTimeClient
{
    private static readonly Regex IsoTimestampPattern = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z");

    public async Task<AuthoritativeTimeResult> GetCurrentTimeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(options.Value.ProviderUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return AuthoritativeTimeResult.Failure(
                    AuthoritativeTimeFailureCode.ProviderUnavailable,
                    $"The time provider returned HTTP {(int)response.StatusCode}.");
            }

            WorldTimeResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<WorldTimeResponse>(cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogWarning(exception, "The authoritative time provider returned an unsupported response format.");
                return AuthoritativeTimeResult.Failure(
                    AuthoritativeTimeFailureCode.InvalidResponse,
                    "The time provider returned an unsupported response format.");
            }
            return Validate(payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthoritativeTimeResult.Failure(
                AuthoritativeTimeFailureCode.Timeout,
                "The time provider request timed out.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "The authoritative time provider was unavailable.");
            return AuthoritativeTimeResult.Failure(
                AuthoritativeTimeFailureCode.ProviderUnavailable,
                "The time provider was unavailable.");
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "The authoritative time provider returned invalid JSON.");
            return AuthoritativeTimeResult.Failure(
                AuthoritativeTimeFailureCode.InvalidResponse,
                "The time provider returned invalid JSON.");
        }
    }

    private static AuthoritativeTimeResult Validate(WorldTimeResponse? payload)
    {
        if (payload is null ||
            !string.Equals(payload.Timezone, "Europe/Zurich", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(payload.DateTime) ||
            string.IsNullOrWhiteSpace(payload.UtcDateTime) ||
            string.IsNullOrWhiteSpace(payload.UtcOffset))
        {
            return Invalid("The provider response is missing required fields or has the wrong timezone.");
        }

        var offsetText = payload.UtcOffset.Trim();
        if (offsetText.Length != 6 ||
            (offsetText[0] != '+' && offsetText[0] != '-') ||
            offsetText[3] != ':' ||
            !char.IsDigit(offsetText[1]) || !char.IsDigit(offsetText[2]) ||
            !char.IsDigit(offsetText[4]) || !char.IsDigit(offsetText[5]))
        {
            return Invalid("The provider returned an invalid UTC offset.");
        }
        var offsetSign = offsetText.StartsWith("-", StringComparison.Ordinal) ? -1 : 1;
        var unsignedOffsetText = offsetText.TrimStart('+', '-');
        if (!TimeSpan.TryParseExact(unsignedOffsetText, @"hh\:mm", CultureInfo.InvariantCulture, out var unsignedOffset))
        {
            return Invalid("The provider returned an invalid UTC offset.");
        }

        var expectedOffset = unsignedOffset * offsetSign;
        if (
            expectedOffset < TimeSpan.FromHours(-14) || expectedOffset > TimeSpan.FromHours(14))
        {
            return Invalid("The provider returned an invalid UTC offset.");
        }

        if (!TryParseIsoTimestamp(payload.DateTime, out var localTime) ||
            !TryParseIsoTimestamp(payload.UtcDateTime, out var utcTime))
        {
            return Invalid("The provider returned an invalid timestamp.");
        }

        if (localTime.Offset != expectedOffset || utcTime.Offset != TimeSpan.Zero || localTime.ToUniversalTime() != utcTime)
        {
            return Invalid("The provider timestamps or offset are inconsistent.");
        }

        return AuthoritativeTimeResult.Success(utcTime);
    }

    private static AuthoritativeTimeResult Invalid(string message) =>
        AuthoritativeTimeResult.Failure(AuthoritativeTimeFailureCode.InvalidResponse, message);

    private static bool TryParseIsoTimestamp(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return IsoTimestampPattern.IsMatch(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    private sealed record WorldTimeResponse(
        [property: JsonPropertyName("timezone")] string? Timezone,
        [property: JsonPropertyName("datetime")] string? DateTime,
        [property: JsonPropertyName("utc_datetime")] string? UtcDateTime,
        [property: JsonPropertyName("utc_offset")] string? UtcOffset);
}
