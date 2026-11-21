using ClockIn.Api.Data;
using ClockIn.Api.Models;
using ClockIn.Api.Services.Time;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClockIn.Api.Services.Attendance;

public sealed class AttendanceService(
    ClockInDbContext db,
    IAuthoritativeTimeClient timeClient,
    ILogger<AttendanceService> logger)
{
    public Task<EmployeeSummary[]> ListEmployeesAsync(CancellationToken cancellationToken = default) =>
        db.Employees.AsNoTracking()
            .OrderBy(employee => employee.EmployeeNumber)
            .Select(employee => new EmployeeSummary(employee.Id, employee.EmployeeNumber, employee.FullName))
            .ToArrayAsync(cancellationToken);

    public async Task<AttendanceState?> GetStateAsync(
        int employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await db.Employees.AsNoTracking()
            .Include(employee => employee.Shifts
                .Where(shift => shift.Status == ShiftStatus.Open || shift.Status == ShiftStatus.MissingClockOut)
                .OrderByDescending(shift => shift.Id))
            .SingleOrDefaultAsync(employee => employee.Id == employeeId, cancellationToken);

        return employee is null
            ? null
            : new AttendanceState(
                employee.Shifts.SingleOrDefault(shift => shift.Status == ShiftStatus.Open),
                employee.Shifts.Where(shift => shift.Status == ShiftStatus.MissingClockOut).ToArray());
    }

    public async Task<AttendanceResult> ClockInAsync(
        int employeeId, CancellationToken cancellationToken = default)
    {
        if (!await EmployeeExistsAsync(employeeId, cancellationToken))
        {
            return new(AttendanceOutcome.EmployeeNotFound);
        }

        if (await OpenShifts(employeeId).AnyAsync(cancellationToken))
        {
            return new(AttendanceOutcome.OpenShiftAlreadyExists);
        }

        var time = await timeClient.GetCurrentTimeAsync(cancellationToken);
        if (time.Instant is not { } instant)
        {
            return await RecordTimeFailureAsync(employeeId, PunchAction.In, time.FailureCode, cancellationToken);
        }

        var shift = new Shift
        {
            EmployeeId = employeeId,
            ClockInAtUtc = instant.ToUniversalTime(),
            Status = ShiftStatus.Open
        };
        db.Shifts.Add(shift);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 } sqlException
            && sqlException.Message.Contains("IX_Shifts_EmployeeId_Open", StringComparison.Ordinal))
        {
            return new(AttendanceOutcome.OpenShiftAlreadyExists);
        }
        finally
        {
            db.Entry(shift).State = EntityState.Detached;
        }

        return new(AttendanceOutcome.Succeeded, shift);
    }

    public async Task<AttendanceResult> ClockOutAsync(
        int employeeId, CancellationToken cancellationToken = default)
    {
        var shift = await OpenShifts(employeeId).SingleOrDefaultAsync(cancellationToken);
        if (shift is null)
        {
            return await GetNoOpenShiftResultAsync(employeeId, cancellationToken);
        }

        var time = await timeClient.GetCurrentTimeAsync(cancellationToken);
        if (time.Instant is not { } instant)
        {
            return await RecordTimeFailureAsync(employeeId, PunchAction.Out, time.FailureCode, cancellationToken);
        }

        if (instant <= shift.ClockInAtUtc)
        {
            return new(AttendanceOutcome.ClockOutNotAfterClockIn);
        }

        return await CloseShiftAsync(shift, ShiftStatus.Completed, instant.ToUniversalTime(), cancellationToken);
    }

    public async Task<AttendanceResult> MarkMissingClockOutAsync(
        int employeeId, CancellationToken cancellationToken = default)
    {
        var shift = await OpenShifts(employeeId).SingleOrDefaultAsync(cancellationToken);
        return shift is null
            ? await GetNoOpenShiftResultAsync(employeeId, cancellationToken)
            : await CloseShiftAsync(shift, ShiftStatus.MissingClockOut, null, cancellationToken);
    }

    private IQueryable<Shift> OpenShifts(int employeeId) => db.Shifts.AsNoTracking()
        .Where(shift => shift.EmployeeId == employeeId && shift.Status == ShiftStatus.Open);

    private Task<bool> EmployeeExistsAsync(int employeeId, CancellationToken cancellationToken) =>
        db.Employees.AnyAsync(employee => employee.Id == employeeId, cancellationToken);

    private async Task<AttendanceResult> GetNoOpenShiftResultAsync(
        int employeeId, CancellationToken cancellationToken)
    {
        return new(await EmployeeExistsAsync(employeeId, cancellationToken)
            ? AttendanceOutcome.NoOpenShift
            : AttendanceOutcome.EmployeeNotFound);
    }

    private async Task<AttendanceResult> CloseShiftAsync(
        Shift shift, ShiftStatus status, DateTimeOffset? clockOutAtUtc, CancellationToken cancellationToken)
    {
        var updated = await OpenShifts(shift.EmployeeId)
            .Where(openShift => openShift.Id == shift.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(openShift => openShift.Status, status)
                .SetProperty(openShift => openShift.ClockOutAtUtc, clockOutAtUtc), cancellationToken);

        if (updated == 0)
        {
            return new(AttendanceOutcome.OpenShiftChanged);
        }

        shift.Status = status;
        shift.ClockOutAtUtc = clockOutAtUtc;
        return new(AttendanceOutcome.Succeeded, shift);
    }

    private async Task<AttendanceResult> RecordTimeFailureAsync(
        int employeeId, PunchAction action, AuthoritativeTimeFailureCode? failure, CancellationToken cancellationToken)
    {
        var attempt = new FailedPunchAttempt
        {
            EmployeeId = employeeId,
            Action = action,
            FailureCode = failure switch
            {
                AuthoritativeTimeFailureCode.Timeout => "TIMEOUT",
                AuthoritativeTimeFailureCode.ProviderUnavailable => "PROVIDER_UNAVAILABLE",
                _ => "INVALID_RESPONSE"
            },
            ReceivedAtServerUtc = DateTimeOffset.UtcNow
        };
        var attemptEntry = db.FailedPunchAttempts.Add(attempt);
        var connectionOpened = false;
        try
        {
            // SqlClient can report pool exhaustion as InvalidOperationException when opening a connection.
            await db.Database.OpenConnectionAsync(cancellationToken);
            connectionOpened = true;
            await db.SaveChangesAsync(cancellationToken);
            return new(AttendanceOutcome.TimeUnavailable, FailureCode: attempt.FailureCode,
                FailedAttemptReferenceId: attempt.Id);
        }
        catch (Exception exception) when (exception is DbUpdateException or SqlException
            || (!connectionOpened && exception is InvalidOperationException and not ObjectDisposedException))
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning(exception, "Could not record failed punch attempt for employee {EmployeeId}.", employeeId);
            return new(AttendanceOutcome.TimeUnavailable, FailureCode: attempt.FailureCode);
        }
        finally
        {
            attemptEntry.State = EntityState.Detached;
            if (connectionOpened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
