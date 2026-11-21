using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClockIn.Api.Data;
using ClockIn.Api.Models;
using ClockIn.Api.Services.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ClockIn.Api.Tests;

public sealed class AttendanceApiTests(AttendanceSqlServerFixture database)
    : IClassFixture<AttendanceSqlServerFixture>
{
    private const string PrivateProviderMessage = "Private provider diagnostic";
    private const string PrivateSqlMessage = "Server=internal;Password=private-test-value";
    private static readonly DateTimeOffset EntryTime = new DateTimeOffset(
        2026, 11, 21, 23, 30, 0, TimeSpan.FromHours(1)).AddTicks(1_234_567);

    [SqlServerFact]
    public async Task List_returns_selectable_employee_fields_ordered_by_employee_number()
    {
        await using var db = database.OpenContext();
        db.Employees.Add(new Employee { EmployeeNumber = "2000", FullName = "Later employee number" });
        await db.SaveChangesAsync();
        db.Employees.Add(new Employee { EmployeeNumber = "0999", FullName = "Earlier employee number" });
        await db.SaveChangesAsync();
        using var host = CreateHost(UnexpectedTime);
        using var client = CreateClient(host);

        var employees = await client.GetFromJsonAsync<JsonElement>("/api/employees");

        var firstDemoEmployee = Assert.Single(employees.EnumerateArray(),
            employee => employee.GetProperty("employeeNumber").GetString() == "1001");
        Assert.Equal(1, firstDemoEmployee.GetProperty("id").GetInt32());
        Assert.Equal("Demo Employee 1001", firstDemoEmployee.GetProperty("fullName").GetString());
        Assert.Equal(3, firstDemoEmployee.EnumerateObject().Count());
        var employeeNumbers = employees.EnumerateArray()
            .Select(employee => employee.GetProperty("employeeNumber").GetString())
            .Where(number => number is "0999" or "1001" or "1002" or "1003" or "2000");
        Assert.Equal(["0999", "1001", "1002", "1003", "2000"], employeeNumbers);
    }

    [SqlServerFact]
    public async Task Actions_return_the_resulting_shift_and_state_excludes_completed_shifts()
    {
        var employeeId = await database.CreateEmployeeAsync();
        var path = AttendancePath(employeeId);
        var times = new Queue<DateTimeOffset>([EntryTime, EntryTime.AddHours(2), EntryTime.AddDays(1), EntryTime.AddDays(2)]);
        using var host = CreateHost(_ => Task.FromResult(AuthoritativeTimeResult.Success(times.Dequeue())));
        using var client = CreateClient(host);

        var empty = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("openShift").ValueKind);
        Assert.Empty(empty.GetProperty("missingClockOutShifts").EnumerateArray());

        var opened = await PostShiftAsync(client, path, "clock-in");
        var openedId = AssertShift(opened, employeeId, "Open", EntryTime);
        var closed = await PostShiftAsync(client, path, "clock-out");
        Assert.Equal(openedId, AssertShift(closed, employeeId, "Completed", EntryTime, EntryTime.AddHours(2)));

        var next = await PostShiftAsync(client, path, "clock-in");
        var nextId = AssertShift(next, employeeId, "Open", EntryTime.AddDays(1));
        var marked = await PostShiftAsync(client, path, "missing-clock-out");
        Assert.Equal(nextId, AssertShift(marked, employeeId, "MissingClockOut", EntryTime.AddDays(1)));
        var replacement = await PostShiftAsync(client, path, "clock-in");
        var replacementId = AssertShift(replacement, employeeId, "Open", EntryTime.AddDays(2));

        var state = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(replacementId, AssertShift(state.GetProperty("openShift"), employeeId, "Open", EntryTime.AddDays(2)));
        var missing = Assert.Single(state.GetProperty("missingClockOutShifts").EnumerateArray());
        Assert.Equal(nextId, AssertShift(missing, employeeId, "MissingClockOut", EntryTime.AddDays(1)));
        Assert.Empty(times);
    }

    [SqlServerFact]
    public async Task Unknown_employee_returns_the_same_problem_for_state_and_all_actions()
    {
        using var host = CreateHost(UnexpectedTime);
        using var client = CreateClient(host);
        var path = AttendancePath(-1);

        using var state = await client.GetAsync(path);
        await AssertProblemAsync(state, HttpStatusCode.NotFound, "EMPLOYEE_NOT_FOUND");
        foreach (var action in new[] { "clock-in", "clock-out", "missing-clock-out" })
        {
            using var response = await client.PostAsync($"{path}/{action}", null);
            await AssertProblemAsync(response, HttpStatusCode.NotFound, "EMPLOYEE_NOT_FOUND");
        }
    }

    [SqlServerTheory]
    [InlineData("not-an-id", "")]
    [InlineData("2147483648", "")]
    [InlineData("not-an-id", "/clock-in")]
    public async Task Invalid_employee_id_returns_a_not_found_problem(string employeeId, string action)
    {
        using var host = CreateHost(UnexpectedTime);
        using var client = CreateClient(host);
        var path = $"/api/employees/{employeeId}/attendance{action}";

        using var response = action.Length == 0
            ? await client.GetAsync(path)
            : await client.PostAsync(path, null);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "EMPLOYEE_NOT_FOUND");
    }

    [SqlServerTheory]
    [InlineData("POST", "/api/employees/not-an-id/attendance")]
    [InlineData("GET", "/api/employees/2147483648/attendance/clock-in")]
    public async Task Invalid_employee_id_takes_precedence_over_an_unsupported_method(string method, string path)
    {
        using var host = CreateHost(UnexpectedTime);
        using var client = CreateClient(host);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "EMPLOYEE_NOT_FOUND");
        Assert.Empty(response.Content.Headers.Allow);
    }

    [SqlServerFact]
    public async Task Get_on_an_action_returns_a_method_not_allowed_problem_and_preserves_allow()
    {
        using var host = CreateHost(UnexpectedTime);
        using var client = CreateClient(host);

        using var response = await client.GetAsync($"{AttendancePath(1)}/clock-in");

        await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
        Assert.Equal(["POST"], response.Content.Headers.Allow);
    }

    [SqlServerFact]
    public async Task The_attendance_fallback_leaves_an_unrelated_not_found_response_empty()
    {
        using var host = CreateHost(UnexpectedTime);
        using var client = CreateClient(host);

        using var response = await client.GetAsync($"{AttendancePath(1)}/unmapped");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [SqlServerFact]
    public async Task Rejected_transitions_return_distinct_conflicts()
    {
        var employeeId = await database.CreateEmployeeAsync();
        var path = AttendancePath(employeeId);
        var times = new Queue<DateTimeOffset>([EntryTime, EntryTime]);
        using var host = CreateHost(_ => Task.FromResult(AuthoritativeTimeResult.Success(times.Dequeue())));
        using var client = CreateClient(host);

        using var noShift = await client.PostAsync($"{path}/clock-out", null);
        await AssertProblemAsync(noShift, HttpStatusCode.Conflict, "NO_OPEN_SHIFT");
        using var noShiftToMark = await client.PostAsync($"{path}/missing-clock-out", null);
        await AssertProblemAsync(noShiftToMark, HttpStatusCode.Conflict, "NO_OPEN_SHIFT");

        await PostShiftAsync(client, path, "clock-in");
        using var duplicate = await client.PostAsync($"{path}/clock-in", null);
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "OPEN_SHIFT_ALREADY_EXISTS");
        using var invalidExit = await client.PostAsync($"{path}/clock-out", null);
        await AssertProblemAsync(invalidExit, HttpStatusCode.Conflict, "CLOCK_OUT_NOT_AFTER_CLOCK_IN");
        Assert.Empty(times);
    }

    [SqlServerFact]
    public async Task A_clock_out_that_loses_to_a_missing_clock_out_returns_shift_changed()
    {
        var employeeId = await database.CreateEmployeeAsync();
        var path = AttendancePath(employeeId);
        var timeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTime = new TaskCompletionSource<AuthoritativeTimeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(15);
        using var clockInHost = CreateHost(_ => Task.FromResult(AuthoritativeTimeResult.Success(EntryTime)));
        using var clockInClient = CreateClient(clockInHost);
        await PostShiftAsync(clockInClient, path, "clock-in");
        using var host = CreateHost(cancellationToken =>
        {
            timeRequested.SetResult();
            return releaseTime.Task.WaitAsync(timeout, cancellationToken);
        });
        using var client = CreateClient(host);

        var pendingClockOut = client.PostAsync($"{path}/clock-out", null);
        try
        {
            await timeRequested.Task.WaitAsync(timeout);
            await PostShiftAsync(client, path, "missing-clock-out");
        }
        finally
        {
            releaseTime.SetResult(AuthoritativeTimeResult.Success(EntryTime.AddHours(2)));
        }

        using var response = await pendingClockOut;
        await AssertProblemAsync(response, HttpStatusCode.Conflict, "OPEN_SHIFT_CHANGED");
    }

    [SqlServerFact]
    public async Task Time_failure_returns_the_saved_reference_and_does_not_create_a_shift()
    {
        var employeeId = await database.CreateEmployeeAsync();
        using var host = CreateHost(_ => FailedTime(AuthoritativeTimeFailureCode.Timeout));
        using var client = CreateClient(host);

        using var response = await client.PostAsync($"{AttendancePath(employeeId)}/clock-in", null);

        await using var db = database.OpenContext();
        var attempt = await db.FailedPunchAttempts.SingleAsync(attempt => attempt.EmployeeId == employeeId);
        Assert.Equal(PunchAction.In, attempt.Action);
        Assert.Equal("TIMEOUT", attempt.FailureCode);
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "TIMEOUT", attempt.Id);
        Assert.False(await db.Shifts.AnyAsync(shift => shift.EmployeeId == employeeId));
    }

    [SqlServerFact]
    public async Task Invalid_time_on_clock_out_returns_the_saved_reference_and_leaves_the_shift_open()
    {
        var employeeId = await database.CreateEmployeeAsync();
        var path = AttendancePath(employeeId);
        var times = new Queue<AuthoritativeTimeResult>(
        [
            AuthoritativeTimeResult.Success(EntryTime),
            AuthoritativeTimeResult.Failure(AuthoritativeTimeFailureCode.InvalidResponse, PrivateProviderMessage)
        ]);
        using var host = CreateHost(_ => Task.FromResult(times.Dequeue()));
        using var client = CreateClient(host);
        var opened = await PostShiftAsync(client, path, "clock-in");
        var openedId = AssertShift(opened, employeeId, "Open", EntryTime);

        using var response = await client.PostAsync($"{path}/clock-out", null);

        await using var db = database.OpenContext();
        var attempt = await db.FailedPunchAttempts.SingleAsync(attempt => attempt.EmployeeId == employeeId);
        Assert.Equal(PunchAction.Out, attempt.Action);
        Assert.Equal("INVALID_RESPONSE", attempt.FailureCode);
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "INVALID_RESPONSE", attempt.Id);
        var shift = await db.Shifts.SingleAsync(shift => shift.EmployeeId == employeeId);
        Assert.Equal(openedId, shift.Id);
        Assert.Equal(ShiftStatus.Open, shift.Status);
        Assert.True(EntryTime.ToUniversalTime().EqualsExact(shift.ClockInAtUtc));
        Assert.Null(shift.ClockOutAtUtc);
    }

    [SqlServerFact]
    public async Task Time_failure_omits_the_reference_when_its_diagnostic_cannot_be_saved()
    {
        var employeeId = await database.CreateEmployeeAsync();
        using var host = CreateHost(_ => FailedTime(AuthoritativeTimeFailureCode.ProviderUnavailable), new RejectWrites());
        using var client = CreateClient(host);

        using var response = await client.PostAsync($"{AttendancePath(employeeId)}/clock-in", null);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "PROVIDER_UNAVAILABLE");
        await using var db = database.OpenContext();
        Assert.False(await db.FailedPunchAttempts.AnyAsync(attempt => attempt.EmployeeId == employeeId));
        Assert.False(await db.Shifts.AnyAsync(shift => shift.EmployeeId == employeeId));
    }

    [SqlServerTheory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Unexpected_database_failure_returns_a_generic_500_in_development(string accept)
    {
        var employeeId = await database.CreateEmployeeAsync();
        using var host = CreateHost(_ => Task.FromResult(AuthoritativeTimeResult.Success(EntryTime)), new RejectWrites());
        using var client = CreateClient(host);
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);

        using var response = await client.PostAsync($"{AttendancePath(employeeId)}/clock-in", null);

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        await using var db = database.OpenContext();
        Assert.False(await db.FailedPunchAttempts.AnyAsync(attempt => attempt.EmployeeId == employeeId));
        Assert.False(await db.Shifts.AnyAsync(shift => shift.EmployeeId == employeeId));
    }

    [SqlServerFact]
    public async Task Attendance_read_failure_returns_a_sanitized_internal_error()
    {
        var employeeId = await database.CreateEmployeeAsync();
        var interceptor = new RejectReads();
        using var host = CreateHost(UnexpectedTime, interceptor);
        using var client = CreateClient(host);

        using var response = await client.GetAsync(AttendancePath(employeeId));

        Assert.True(interceptor.ReadAttempted);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
    }

    private WebApplicationFactory<Program> CreateHost(
        Func<CancellationToken, Task<AuthoritativeTimeResult>> getTime, IInterceptor? interceptor = null)
    {
        using var db = database.OpenContext();
        var connectionString = db.Database.GetConnectionString();
        return new AttendanceApiHost(connectionString!, new ControlledTimeClient(getTime), interceptor);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static string AttendancePath(int employeeId) => $"/api/employees/{employeeId}/attendance";

    private static Task<AuthoritativeTimeResult> UnexpectedTime(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected authoritative time request.");

    private static Task<AuthoritativeTimeResult> FailedTime(AuthoritativeTimeFailureCode code) =>
        Task.FromResult(AuthoritativeTimeResult.Failure(code, PrivateProviderMessage));

    private static async Task<JsonElement> PostShiftAsync(HttpClient client, string path, string action)
    {
        using var response = await client.PostAsync($"{path}/{action}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int AssertShift(
        JsonElement shift, int employeeId, string status, DateTimeOffset clockIn, DateTimeOffset? clockOut = null)
    {
        Assert.Equal(employeeId, shift.GetProperty("employeeId").GetInt32());
        Assert.Equal(status, shift.GetProperty("status").GetString());
        Assert.True(clockIn.ToUniversalTime().EqualsExact(shift.GetProperty("clockInAtUtc").GetDateTimeOffset()));
        var actualClockOut = shift.GetProperty("clockOutAtUtc");
        if (clockOut is { } expected)
        {
            Assert.True(expected.ToUniversalTime().EqualsExact(actualClockOut.GetDateTimeOffset()));
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, actualClockOut.ValueKind);
        }

        var id = shift.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        return id;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, int? failedAttemptReferenceId = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.Equal(code, problem.GetProperty("code").GetString());
        var hasReference = problem.TryGetProperty("failedAttemptReferenceId", out var reference);
        Assert.Equal(failedAttemptReferenceId.HasValue, hasReference);
        if (failedAttemptReferenceId is { } expected)
        {
            Assert.Equal(expected, reference.GetInt32());
        }

        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.DoesNotContain(PrivateProviderMessage, problem.GetRawText());
        Assert.DoesNotContain(PrivateSqlMessage, problem.GetRawText());
    }

    private sealed class AttendanceApiHost(string connectionString, IAuthoritativeTimeClient time, IInterceptor? interceptor)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:ClockIn"] = connectionString }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthoritativeTimeClient>();
                services.AddSingleton(time);
                if (interceptor is not null)
                {
                    services.ConfigureDbContext<ClockInDbContext>(options => options.AddInterceptors(interceptor));
                }
            });
        }
    }

    private sealed class ControlledTimeClient(Func<CancellationToken, Task<AuthoritativeTimeResult>> getTime)
        : IAuthoritativeTimeClient
    {
        public Task<AuthoritativeTimeResult> GetCurrentTimeAsync(CancellationToken cancellationToken) =>
            getTime(cancellationToken);
    }

    private sealed class RejectWrites : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException(PrivateSqlMessage);
    }

    private sealed class RejectReads : DbCommandInterceptor
    {
        public bool ReadAttempted { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            throw new InvalidOperationException(PrivateSqlMessage);
        }
    }
}
