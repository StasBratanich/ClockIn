using System.Data.Common;
using ClockIn.Api.Data;
using ClockIn.Api.Models;
using ClockIn.Api.Services.Attendance;
using ClockIn.Api.Services.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClockIn.Api.Tests;

public sealed class AttendanceServiceTests(AttendanceSqlServerFixture database)
    : IClassFixture<AttendanceSqlServerFixture>
{
    private static readonly DateTimeOffset EntryTime = new DateTimeOffset(
        2026, 11, 21, 23, 30, 0, TimeSpan.FromHours(1)).AddTicks(1_234_567);

    [SqlServerFact]
    public async Task Clock_out_completes_a_shift_across_Zurich_midnight()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var db = database.OpenContext();

        var opened = await CreateService(db, TimeAt(EntryTime)).ClockInAsync(employeeId);
        var exitTime = EntryTime.AddHours(2);
        var closed = await CreateService(db, TimeAt(exitTime)).ClockOutAsync(employeeId);

        Assert.Equal(AttendanceOutcome.Succeeded, opened.Outcome);
        Assert.Equal(AttendanceOutcome.Succeeded, closed.Outcome);
        Assert.Equal(opened.Shift!.Id, closed.Shift!.Id);
        var shift = await db.Shifts.AsNoTracking().SingleAsync(shift => shift.EmployeeId == employeeId);
        Assert.Equal(ShiftStatus.Completed, shift.Status);
        Assert.True(EntryTime.ToUniversalTime().EqualsExact(shift.ClockInAtUtc));
        Assert.True(exitTime.ToUniversalTime().EqualsExact(shift.ClockOutAtUtc!.Value));
        var state = await CreateService(db).GetStateAsync(employeeId);
        Assert.Null(state!.OpenShift);
        Assert.Empty(state.MissingClockOutShifts);
    }

    [SqlServerFact]
    public async Task Rejected_transitions_and_missing_clock_out_do_not_request_time()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var db = database.OpenContext();
        var service = CreateService(db);

        Assert.Null(await service.GetStateAsync(-1));
        Assert.Equal(AttendanceOutcome.EmployeeNotFound, (await service.ClockInAsync(-1)).Outcome);
        Assert.Equal(AttendanceOutcome.EmployeeNotFound, (await service.ClockOutAsync(-1)).Outcome);
        Assert.Equal(AttendanceOutcome.EmployeeNotFound, (await service.MarkMissingClockOutAsync(-1)).Outcome);
        Assert.Equal(AttendanceOutcome.NoOpenShift, (await service.ClockOutAsync(employeeId)).Outcome);
        Assert.Equal(AttendanceOutcome.NoOpenShift, (await service.MarkMissingClockOutAsync(employeeId)).Outcome);

        var opened = await CreateService(db, TimeAt(EntryTime)).ClockInAsync(employeeId);
        Assert.Equal(AttendanceOutcome.OpenShiftAlreadyExists, (await service.ClockInAsync(employeeId)).Outcome);
        var marked = await service.MarkMissingClockOutAsync(employeeId);
        Assert.Equal(AttendanceOutcome.Succeeded, marked.Outcome);
        Assert.Equal(opened.Shift!.Id, marked.Shift!.Id);
        Assert.Equal(AttendanceOutcome.NoOpenShift, (await service.MarkMissingClockOutAsync(employeeId)).Outcome);
        var reopened = await CreateService(db, TimeAt(EntryTime.AddDays(1))).ClockInAsync(employeeId);

        var state = await service.GetStateAsync(employeeId);
        Assert.Equal(reopened.Shift!.Id, state!.OpenShift!.Id);
        var missing = Assert.Single(state.MissingClockOutShifts);
        Assert.Equal(opened.Shift.Id, missing.Id);
        Assert.Equal(ShiftStatus.MissingClockOut, missing.Status);
        Assert.Null(missing.ClockOutAtUtc);
        Assert.False(await db.FailedPunchAttempts.AnyAsync(attempt => attempt.EmployeeId == employeeId));
    }

    [SqlServerTheory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Exit_must_be_strictly_later_than_entry(int ticksAfterEntry)
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var db = database.OpenContext();
        await CreateService(db, TimeAt(EntryTime)).ClockInAsync(employeeId);

        var result = await CreateService(db, TimeAt(EntryTime.AddTicks(ticksAfterEntry))).ClockOutAsync(employeeId);

        Assert.Equal(AttendanceOutcome.ClockOutNotAfterClockIn, result.Outcome);
        var shift = await db.Shifts.AsNoTracking().SingleAsync(shift => shift.EmployeeId == employeeId);
        Assert.Equal(ShiftStatus.Open, shift.Status);
        Assert.Null(shift.ClockOutAtUtc);
    }

    [SqlServerFact]
    public async Task Competing_clock_ins_create_one_open_shift()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var firstDb = database.OpenContext();
        await using var secondDb = database.OpenContext();
        var time = new TimeGate(2, EntryTime);

        var first = CreateService(firstDb, time).ClockInAsync(employeeId);
        var second = CreateService(secondDb, time).ClockInAsync(employeeId);
        await time.WaitForClientsAsync();
        Assert.Null(firstDb.Database.CurrentTransaction);
        Assert.Null(secondDb.Database.CurrentTransaction);
        time.Release();
        var results = await Task.WhenAll(first, second);

        var success = Assert.Single(results, result => result.Outcome == AttendanceOutcome.Succeeded);
        Assert.Single(results, result => result.Outcome == AttendanceOutcome.OpenShiftAlreadyExists);
        var shift = await firstDb.Shifts.AsNoTracking().SingleAsync(shift => shift.EmployeeId == employeeId);
        Assert.Equal(success.Shift!.Id, shift.Id);
        Assert.Equal(success.Shift.ClockInAtUtc, shift.ClockInAtUtc);
        Assert.Equal(ShiftStatus.Open, shift.Status);
    }

    [SqlServerFact]
    public async Task Competing_and_repeated_clock_outs_close_the_shift_once()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var firstDb = database.OpenContext();
        await using var secondDb = database.OpenContext();
        await CreateService(firstDb, TimeAt(EntryTime)).ClockInAsync(employeeId);
        var time = new TimeGate(2, EntryTime.AddHours(1));

        var first = CreateService(firstDb, time).ClockOutAsync(employeeId);
        var second = CreateService(secondDb, time).ClockOutAsync(employeeId);
        await time.WaitForClientsAsync();
        Assert.Null(firstDb.Database.CurrentTransaction);
        Assert.Null(secondDb.Database.CurrentTransaction);
        time.Release();
        var results = await Task.WhenAll(first, second);

        var success = Assert.Single(results, result => result.Outcome == AttendanceOutcome.Succeeded);
        Assert.Single(results, result => result.Outcome == AttendanceOutcome.OpenShiftChanged);
        Assert.Equal(AttendanceOutcome.NoOpenShift, (await CreateService(firstDb).ClockOutAsync(employeeId)).Outcome);
        var shift = await firstDb.Shifts.AsNoTracking().SingleAsync(shift => shift.EmployeeId == employeeId);
        Assert.Equal(ShiftStatus.Completed, shift.Status);
        Assert.Equal(success.Shift!.ClockOutAtUtc, shift.ClockOutAtUtc);
    }

    [SqlServerFact]
    public async Task A_delayed_clock_out_cannot_close_a_replacement_shift()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var firstDb = database.OpenContext();
        await using var secondDb = database.OpenContext();
        var opened = await CreateService(firstDb, TimeAt(EntryTime)).ClockInAsync(employeeId);
        var time = new TimeGate(1, EntryTime.AddHours(3));
        var pendingClockOut = CreateService(firstDb, time).ClockOutAsync(employeeId);
        await time.WaitForClientsAsync();

        var marked = await CreateService(secondDb).MarkMissingClockOutAsync(employeeId);
        var replacement = await CreateService(secondDb, TimeAt(EntryTime.AddHours(2))).ClockInAsync(employeeId);
        time.Release();

        Assert.Equal(AttendanceOutcome.Succeeded, marked.Outcome);
        Assert.Equal(AttendanceOutcome.Succeeded, replacement.Outcome);
        Assert.Equal(AttendanceOutcome.OpenShiftChanged, (await pendingClockOut).Outcome);
        var state = await CreateService(firstDb).GetStateAsync(employeeId);
        Assert.Equal(replacement.Shift!.Id, state!.OpenShift!.Id);
        Assert.Null(state.OpenShift.ClockOutAtUtc);
        var missing = Assert.Single(state.MissingClockOutShifts);
        Assert.Equal(opened.Shift!.Id, missing.Id);
        Assert.Null(missing.ClockOutAtUtc);
    }

    [SqlServerTheory]
    [InlineData(PunchAction.In, AuthoritativeTimeFailureCode.Timeout, "TIMEOUT")]
    [InlineData(PunchAction.Out, AuthoritativeTimeFailureCode.ProviderUnavailable, "PROVIDER_UNAVAILABLE")]
    [InlineData(PunchAction.Out, AuthoritativeTimeFailureCode.InvalidResponse, "INVALID_RESPONSE")]
    public async Task Failed_time_requests_record_only_diagnostics(
        PunchAction action, AuthoritativeTimeFailureCode failure, string expectedCode)
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var db = database.OpenContext();
        if (action == PunchAction.Out)
        {
            await CreateService(db, TimeAt(EntryTime)).ClockInAsync(employeeId);
        }

        var service = CreateService(db, FailingTime(failure));
        var before = DateTimeOffset.UtcNow;
        var result = action == PunchAction.In
            ? await service.ClockInAsync(employeeId)
            : await service.ClockOutAsync(employeeId);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(AttendanceOutcome.TimeUnavailable, result.Outcome);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Null(result.Shift);
        var attempt = await db.FailedPunchAttempts.AsNoTracking().SingleAsync(attempt => attempt.EmployeeId == employeeId);
        Assert.Equal(attempt.Id, result.FailedAttemptReferenceId);
        Assert.Equal(expectedCode, attempt.FailureCode);
        Assert.Equal(action, attempt.Action);
        Assert.Equal(TimeSpan.Zero, attempt.ReceivedAtServerUtc.Offset);
        Assert.InRange(attempt.ReceivedAtServerUtc, before, after);
        var state = await service.GetStateAsync(employeeId);
        Assert.Empty(state!.MissingClockOutShifts);
        var shifts = db.Shifts.AsNoTracking().Where(shift => shift.EmployeeId == employeeId);
        if (action == PunchAction.In)
        {
            Assert.False(await shifts.AnyAsync());
        }
        else
        {
            var original = await shifts.SingleAsync();
            Assert.Equal(ShiftStatus.Open, original.Status);
            Assert.Equal(EntryTime, original.ClockInAtUtc);
            Assert.Null(original.ClockOutAtUtc);
        }
    }

    [SqlServerFact]
    public async Task A_failed_shift_insert_is_not_saved_with_a_later_time_failure()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var sqlDb = database.OpenContext();
        var insertFailure = new DbUpdateException("Simulated shift insert failure.");
        var options = new DbContextOptionsBuilder<ClockInDbContext>()
            .UseSqlServer(sqlDb.Database.GetConnectionString())
            .AddInterceptors(new RejectFirstShiftInsert(insertFailure))
            .Options;
        await using var db = new ClockInDbContext(options);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreateService(db, TimeAt(EntryTime)).ClockInAsync(employeeId));
        Assert.Same(insertFailure, thrown);

        var result = await CreateService(db, FailingTime(AuthoritativeTimeFailureCode.Timeout)).ClockInAsync(employeeId);

        Assert.Equal(AttendanceOutcome.TimeUnavailable, result.Outcome);
        Assert.Equal("TIMEOUT", result.FailureCode);
        Assert.Null(result.Shift);
        var attempt = await sqlDb.FailedPunchAttempts.AsNoTracking().SingleAsync(attempt => attempt.EmployeeId == employeeId);
        Assert.Equal(attempt.Id, result.FailedAttemptReferenceId);
        Assert.Equal(PunchAction.In, attempt.Action);
        Assert.Equal("TIMEOUT", attempt.FailureCode);
        Assert.False(await sqlDb.Shifts.AnyAsync(shift => shift.EmployeeId == employeeId));
    }

    [SqlServerFact]
    public async Task A_failed_diagnostic_connection_returns_the_time_failure_without_a_reference()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var sqlDb = database.OpenContext();
        var opened = await CreateService(sqlDb, TimeAt(EntryTime)).ClockInAsync(employeeId);
        Assert.Equal(AttendanceOutcome.Succeeded, opened.Outcome);
        var options = new DbContextOptionsBuilder<ClockInDbContext>()
            .UseSqlServer(sqlDb.Database.GetConnectionString())
            .AddInterceptors(new RejectFailedAttemptConnection())
            .Options;
        await using var db = new ClockInDbContext(options);

        var result = await CreateService(db, FailingTime(AuthoritativeTimeFailureCode.ProviderUnavailable))
            .ClockOutAsync(employeeId);

        Assert.Equal(AttendanceOutcome.TimeUnavailable, result.Outcome);
        Assert.Equal("PROVIDER_UNAVAILABLE", result.FailureCode);
        Assert.Null(result.FailedAttemptReferenceId);
        Assert.Null(result.Shift);
        Assert.Empty(db.ChangeTracker.Entries<FailedPunchAttempt>());
        Assert.False(await sqlDb.FailedPunchAttempts.AnyAsync(attempt => attempt.EmployeeId == employeeId));
        var shift = await sqlDb.Shifts.AsNoTracking().SingleAsync(shift => shift.EmployeeId == employeeId);
        Assert.Equal(opened.Shift!.Id, shift.Id);
        Assert.Equal(ShiftStatus.Open, shift.Status);
        Assert.Equal(opened.Shift.ClockInAtUtc, shift.ClockInAtUtc);
        Assert.Null(shift.ClockOutAtUtc);
    }

    [SqlServerFact]
    public async Task A_failed_diagnostic_write_returns_no_reference_and_does_not_poison_a_retry()
    {
        var employeeId = await database.CreateEmployeeAsync();
        await using var sqlDb = database.OpenContext();
        var options = new DbContextOptionsBuilder<ClockInDbContext>()
            .UseSqlServer(sqlDb.Database.GetConnectionString())
            .AddInterceptors(new RejectFailedAttemptWrites())
            .Options;
        await using var db = new ClockInDbContext(options);

        var failed = await CreateService(db, FailingTime(AuthoritativeTimeFailureCode.Timeout)).ClockInAsync(employeeId);
        Assert.Equal(AttendanceOutcome.TimeUnavailable, failed.Outcome);
        Assert.Null(failed.FailedAttemptReferenceId);
        Assert.Null((await CreateService(db).GetStateAsync(employeeId))!.OpenShift);

        var retry = await CreateService(db, TimeAt(EntryTime)).ClockInAsync(employeeId);
        Assert.Equal(AttendanceOutcome.Succeeded, retry.Outcome);
        Assert.False(await sqlDb.FailedPunchAttempts.AnyAsync(attempt => attempt.EmployeeId == employeeId));
    }

    private static AttendanceService CreateService(ClockInDbContext db, IAuthoritativeTimeClient? time = null) =>
        new(db, time ?? new StubTimeClient(null), NullLogger<AttendanceService>.Instance);

    private static IAuthoritativeTimeClient TimeAt(DateTimeOffset instant) =>
        new StubTimeClient(AuthoritativeTimeResult.Success(instant));

    private static IAuthoritativeTimeClient FailingTime(AuthoritativeTimeFailureCode failure) =>
        new StubTimeClient(AuthoritativeTimeResult.Failure(failure, "Raw provider diagnostic"));

    private sealed class StubTimeClient(AuthoritativeTimeResult? result) : IAuthoritativeTimeClient
    {
        public Task<AuthoritativeTimeResult> GetCurrentTimeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result ?? throw new InvalidOperationException("Unexpected authoritative time call."));
    }

    private sealed class TimeGate(int expectedClients, DateTimeOffset instant) : IAuthoritativeTimeClient
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);
        private readonly TaskCompletionSource clientsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int clients;

        public Task WaitForClientsAsync() => clientsReady.Task.WaitAsync(WaitTimeout);

        public void Release() => release.SetResult();

        public async Task<AuthoritativeTimeResult> GetCurrentTimeAsync(CancellationToken cancellationToken)
        {
            var clientNumber = Interlocked.Increment(ref clients);
            if (clientNumber == expectedClients)
            {
                clientsReady.SetResult();
            }

            await release.Task.WaitAsync(WaitTimeout, cancellationToken);
            return AuthoritativeTimeResult.Success(instant.AddTicks(clientNumber - 1));
        }
    }

    private sealed class RejectFirstShiftInsert(DbUpdateException failure) : SaveChangesInterceptor
    {
        private bool rejected;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!rejected && eventData.Context!.ChangeTracker.Entries<Shift>().Any(entry => entry.State == EntityState.Added))
            {
                rejected = true;
                throw failure;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RejectFailedAttemptConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<FailedPunchAttempt>().Any(entry => entry.State == EntityState.Added))
            {
                throw new InvalidOperationException("Simulated timeout acquiring a SQL Server connection.");
            }

            return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
        }
    }

    private sealed class RejectFailedAttemptWrites : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<FailedPunchAttempt>().Any(entry => entry.State == EntityState.Added))
            {
                throw new DbUpdateException("Simulated diagnostic write failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
