namespace ClockIn.Api.Services.Time;

public interface IAuthoritativeTimeClient
{
    Task<AuthoritativeTimeResult> GetCurrentTimeAsync(CancellationToken cancellationToken = default);
}
