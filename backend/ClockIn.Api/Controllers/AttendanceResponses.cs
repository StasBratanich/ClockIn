using ClockIn.Api.Models;

namespace ClockIn.Api.Controllers;

public sealed record AttendanceResponse(ShiftResponse? OpenShift, IReadOnlyList<ShiftResponse> MissingClockOutShifts);

public sealed record ShiftResponse(
    int Id, int EmployeeId, DateTimeOffset ClockInAtUtc, DateTimeOffset? ClockOutAtUtc, string Status)
{
    internal static ShiftResponse FromShift(Shift shift) =>
        new(shift.Id, shift.EmployeeId, shift.ClockInAtUtc, shift.ClockOutAtUtc, shift.Status.ToString());
}
