namespace ClockIn.Api.Models;

public enum ShiftStatus : byte
{
    Open = 1,
    Completed = 2,
    MissingClockOut = 3
}

public enum PunchAction : byte
{
    In = 1,
    Out = 2
}

public sealed class Employee
{
    public int Id { get; set; }
    public required string EmployeeNumber { get; set; }
    public required string FullName { get; set; }
    public ICollection<Shift> Shifts { get; } = [];
    public ICollection<FailedPunchAttempt> FailedPunchAttempts { get; } = [];
}

public sealed class Shift
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public required DateTimeOffset ClockInAtUtc { get; set; }
    public DateTimeOffset? ClockOutAtUtc { get; set; }
    public ShiftStatus Status { get; set; }
    public Employee Employee { get; set; } = null!;
}

public sealed class FailedPunchAttempt
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public PunchAction Action { get; set; }
    public required string FailureCode { get; set; }
    public DateTimeOffset ReceivedAtServerUtc { get; set; }
    public Employee Employee { get; set; } = null!;
}
