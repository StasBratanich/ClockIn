using ClockIn.Api.Data;
using ClockIn.Api.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ClockIn.Api.Tests;

public sealed class AttendanceSqlServerFixture : IAsyncLifetime
{
    private const string ConnectionVariable = "CLOCKIN_TEST_SQLSERVER";
    private DbContextOptions<ClockInDbContext>? options;
    private bool createdDatabase;

    internal static string? ConnectionString => Environment.GetEnvironmentVariable(ConnectionVariable);
    internal const string SkipReason = "Set CLOCKIN_TEST_SQLSERVER to run the attendance SQL Server tests.";

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        var connection = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = $"ClockInTests_{Guid.NewGuid():N}",
            ConnectTimeout = 5
        };
        connection.Remove("AttachDbFilename");
        options = new DbContextOptionsBuilder<ClockInDbContext>()
            .UseSqlServer(connection.ConnectionString)
            .Options;

        await using var db = OpenContext();
        await db.GetService<IRelationalDatabaseCreator>().CreateAsync();
        createdDatabase = true;
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (createdDatabase)
        {
            await using var db = OpenContext();
            await db.Database.EnsureDeletedAsync();
        }
    }

    internal ClockInDbContext OpenContext() => new(options!);

    internal async Task<int> CreateEmployeeAsync()
    {
        await using var db = OpenContext();
        var employee = new Employee
        {
            EmployeeNumber = Guid.NewGuid().ToString("N")[..20],
            FullName = "Attendance integration test"
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }
}

internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(AttendanceSqlServerFixture.ConnectionString))
        {
            Skip = AttendanceSqlServerFixture.SkipReason;
        }
    }
}

internal sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(AttendanceSqlServerFixture.ConnectionString))
        {
            Skip = AttendanceSqlServerFixture.SkipReason;
        }
    }
}
