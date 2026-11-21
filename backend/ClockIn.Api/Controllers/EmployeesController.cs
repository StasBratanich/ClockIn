using ClockIn.Api.Services.Attendance;
using Microsoft.AspNetCore.Mvc;

namespace ClockIn.Api.Controllers;

[ApiController]
[Route("api/employees")]
public sealed class EmployeesController(AttendanceService attendance) : ControllerBase
{
    [HttpGet]
    public Task<EmployeeSummary[]> ListEmployees(CancellationToken cancellationToken) =>
        attendance.ListEmployeesAsync(cancellationToken);

    [HttpGet("{id:int}/attendance")]
    public async Task<ActionResult<AttendanceResponse>> GetAttendance(int id, CancellationToken cancellationToken)
    {
        var state = await attendance.GetStateAsync(id, cancellationToken);
        if (state is null)
        {
            return EmployeeNotFound();
        }

        return new AttendanceResponse(
            state.OpenShift is { } openShift ? ShiftResponse.FromShift(openShift) : null,
            state.MissingClockOutShifts.Select(ShiftResponse.FromShift).ToArray());
    }

    [HttpPost("{id:int}/attendance/clock-in")]
    public async Task<ActionResult<ShiftResponse>> ClockIn(int id, CancellationToken cancellationToken) =>
        ToActionResult(await attendance.ClockInAsync(id, cancellationToken));

    [HttpPost("{id:int}/attendance/clock-out")]
    public async Task<ActionResult<ShiftResponse>> ClockOut(int id, CancellationToken cancellationToken) =>
        ToActionResult(await attendance.ClockOutAsync(id, cancellationToken));

    [HttpPost("{id:int}/attendance/missing-clock-out")]
    public async Task<ActionResult<ShiftResponse>> MarkMissingClockOut(int id, CancellationToken cancellationToken) =>
        ToActionResult(await attendance.MarkMissingClockOutAsync(id, cancellationToken));

    private ActionResult<ShiftResponse> ToActionResult(AttendanceResult result) => result.Outcome switch
    {
        AttendanceOutcome.Succeeded => ShiftResponse.FromShift(result.Shift!),
        AttendanceOutcome.EmployeeNotFound => EmployeeNotFound(),
        AttendanceOutcome.OpenShiftAlreadyExists => AttendanceProblem(
            StatusCodes.Status409Conflict, "OPEN_SHIFT_ALREADY_EXISTS", "The employee already has an open shift."),
        AttendanceOutcome.NoOpenShift => AttendanceProblem(
            StatusCodes.Status409Conflict, "NO_OPEN_SHIFT", "The employee has no open shift."),
        AttendanceOutcome.OpenShiftChanged => AttendanceProblem(
            StatusCodes.Status409Conflict, "OPEN_SHIFT_CHANGED", "The open shift changed during the request."),
        AttendanceOutcome.ClockOutNotAfterClockIn => AttendanceProblem(
            StatusCodes.Status409Conflict, "CLOCK_OUT_NOT_AFTER_CLOCK_IN", "Clock Out must be after Clock In."),
        AttendanceOutcome.TimeUnavailable => AttendanceProblem(
            StatusCodes.Status503ServiceUnavailable, result.FailureCode!, "Authoritative time is unavailable.",
            result.FailedAttemptReferenceId),
        _ => throw new InvalidOperationException("Unexpected attendance outcome.")
    };

    private ObjectResult EmployeeNotFound() => AttendanceProblem(
        StatusCodes.Status404NotFound, "EMPLOYEE_NOT_FOUND", "The employee was not found.");

    private ObjectResult AttendanceProblem(int status, string code, string title, int? failedAttemptReferenceId = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (failedAttemptReferenceId is { } referenceId)
        {
            extensions["failedAttemptReferenceId"] = referenceId;
        }

        return Problem(statusCode: status, title: title, extensions: extensions);
    }
}
