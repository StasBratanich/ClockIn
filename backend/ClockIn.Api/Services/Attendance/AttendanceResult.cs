using ClockIn.Api.Models;

namespace ClockIn.Api.Services.Attendance;

public enum AttendanceOutcome
{
    Succeeded,
    EmployeeNotFound,
    OpenShiftAlreadyExists,
    NoOpenShift,
    OpenShiftChanged,
    ClockOutNotAfterClockIn,
    TimeUnavailable
}

public sealed record AttendanceResult(
    AttendanceOutcome Outcome,
    Shift? Shift = null,
    string? FailureCode = null,
    int? FailedAttemptReferenceId = null);

public sealed record AttendanceState(Shift? OpenShift, IReadOnlyList<Shift> MissingClockOutShifts);

public sealed record EmployeeSummary(int Id, string EmployeeNumber, string FullName);
