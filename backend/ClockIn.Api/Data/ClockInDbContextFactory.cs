using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ClockIn.Api.Data;

public sealed class ClockInDbContextFactory : IDesignTimeDbContextFactory<ClockInDbContext>
{
    public ClockInDbContext CreateDbContext(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddUserSecrets<ClockInDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("ClockIn");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Configure ConnectionStrings:ClockIn in user secrets or through the ConnectionStrings__ClockIn environment variable before running EF tools.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<ClockInDbContext>();
        optionsBuilder.UseSqlServer(connectionString);
        return new ClockInDbContext(optionsBuilder.Options);
    }
}
