namespace ClockIn.Api.Services.Time;

public sealed class AuthoritativeTimeOptions
{
    public const string SectionName = "AuthoritativeTime";

    public string ProviderUrl { get; set; } = "https://worldtime.timezone.io/api/timezone/Europe/Zurich";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
