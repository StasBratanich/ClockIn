namespace ClockIn.Api.Services.Time;

public enum AuthoritativeTimeFailureCode
{
    Timeout,
    ProviderUnavailable,
    InvalidResponse
}

public sealed record AuthoritativeTimeResult(
    DateTimeOffset? Instant,
    AuthoritativeTimeFailureCode? FailureCode,
    string? FailureMessage)
{
    public bool IsSuccess => Instant.HasValue;

    public static AuthoritativeTimeResult Success(DateTimeOffset instant) =>
        new(instant, null, null);

    public static AuthoritativeTimeResult Failure(
        AuthoritativeTimeFailureCode code,
        string message) => new(null, code, message);
}
