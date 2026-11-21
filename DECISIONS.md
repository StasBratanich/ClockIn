# ClockIn — Decisions

Status: Subtask 1 completed. This document records agreed product and architecture decisions. Later attendance features remain unimplemented; implementation progress is tracked in WORK_PLAN.md.

## Scope and delivery

ClockIn is a small employee attendance system built with ASP.NET Core (.NET 10), React with TypeScript, and SQL Server. The first deliverable focuses on a reliable clock-in/clock-out flow and a few meaningful exceptions rather than a broad workforce-management product.

The local SQL Server setup uses Docker Compose so someone cloning the repository can run it without the developer's existing local SQL Server installation. The Compose service runs SQL Server 2022, maps host port 14333 to container port 1433 to avoid the developer's local instance, and uses a named data volume. A local, ignored `.env` supplies the SQL Server password; an example file shows the required setting. The canonical API connection key is `ConnectionStrings:ClockIn`, supplied through development user secrets or the standard `ConnectionStrings__ClockIn` environment-variable form, not a checked-in password. Apply EF Core migrations explicitly with `dotnet ef database update` after SQL Server starts; the API does not run migrations automatically on startup.

## Agreed decisions

### Employee selection, not authentication

The interface will let a user select an employee from a small list. The backend will validate the selected employee ID. Selection is not proof of identity; authentication and roles are outside the initial scope. We will describe that limitation honestly and will not treat a selected employee as an authenticated actor.

`Employees.Id` is an internal `int IDENTITY` primary key used by foreign keys. `EmployeeNumber` is a separate, required `varchar(20)` business identifier with a unique index; `FullName` is required `nvarchar(120)`. The business number is not a primary/foreign key and is not generated from the internal ID. The initial migration supplies three fixed demo employees numbered `1001`, `1002`, and `1003`. The demo has no employee-creation UI and does not impose an artificial database limit of ten employees.

### Attendance and shift states

An employee may have at most one active shift. Clock In opens one; Clock Out closes it. A shift may cross midnight and remains active until an explicit action changes its state. Repeating Clock In while a shift is active, or Clock Out when none is active, must not create or change attendance data and must return a clear response.

The initial shift model distinguishes an active shift, a completed shift, and a shift marked as missing a clock-out. If someone forgot to clock out, they can explicitly choose “I forgot to clock out.” This marks the old shift as incomplete, leaves its clock-out time empty, and allows a new Clock In. It does **not** invent a departure time or calculate a completed duration for that shift. There is no automatic closure after a fixed number of hours; the UI should make an unusually long active shift conspicuous while still allowing a legitimate long shift to be clocked out. The exact warning threshold remains open.

For the first migration, `Shifts` has `Id` (`int IDENTITY`), `EmployeeId` (`int`, required FK), `ClockInAtUtc` (`datetimeoffset`, required), `ClockOutAtUtc` (`datetimeoffset`, nullable), and `Status` (`tinyint`, required). The backend represents `Status` with a `ShiftStatus : byte` enum: `1=Open`, `2=Completed`, `3=MissingClockOut`. A database check permits only these states and requires a missing exit time for `Open`/`MissingClockOut`, or an exit later than the entry for `Completed`. Attendance timestamps are stored at offset `+00:00`; a check constraint enforces that representation. The database cannot prove the source of a timestamp; the later backend service must obtain it from the external API.

### Concurrent requests and database enforcement

Clock actions will use database transactions. A unique filtered index on `Shifts.EmployeeId` where `Status=Open` independently enforces at most one active shift per employee. Two concurrent Clock In requests cannot create two active shifts: only one should succeed, and the other receives a clear conflict response. A concurrent or repeated Clock Out must likewise not close the same shift twice. The conditional write pattern belongs to the later attendance-service Subtask. Foreign keys to `Employees` do not cascade-delete attendance history.

### Authoritative time

Every successful Clock In and Clock Out must use time obtained by the backend from an external time API for `Europe/Zurich`. Neither the browser clock nor the server's local clock is a substitute for an attendance timestamp. Store the validated instant as UTC in SQL Server `datetimeoffset` columns, preserving the provider's available precision; show `HH:mm` in Zurich time in the UI without rounding away the stored instant. The initial implementation will use one external provider; the provider, response validation, and exact timeout policy are decided in the time-client Subtask. The UI will offer a retry when time retrieval fails. A later successful attempt records the time obtained then; it does not silently backdate the punch.

The MVP has one fixed business time zone: `Europe/Zurich`. The backend converts each validated provider instant to UTC for storage, and the UI displays attendance in Zurich time. There is no per-employee or per-shift time-zone column in the MVP. Supporting multiple regions later would require a time-zone identifier for selecting the external time request and interpreting local dates and display; the stored UTC instants remain valid.

### Failed time-service attempts

If the external time service fails during Clock In or Clock Out, no successful punch is recorded and no shift is opened or closed. If SQL Server is available, the backend will save a separate failed-attempt record containing the employee, requested action, failure reason, and a reference ID shown to the user. A server-observed request time may be kept there **only as diagnostic metadata**, clearly labelled non-authoritative. It must never be used as a Clock In/Out time or automatically converted into one. A failed-attempt record supports later investigation; it does not establish when work began or ended. If the database is also unavailable, the application cannot guarantee that the failed attempt was saved.

`FailedPunchAttempts` has `Id` (`int IDENTITY`, the reference ID), `EmployeeId` (`int`, required FK), `Action` (`tinyint`, `1=In` or `2=Out`, checked in the database), `FailureCode` (`varchar(32)`, required), and `ReceivedAtServerUtc` (`datetimeoffset`, required diagnostic metadata only). `ReceivedAtServerUtc` records when the server observed the failed request; it is never an authoritative attendance timestamp and must not be used to create, backdate, or infer a Clock In or Clock Out. The backend will map provider errors to its own stable codes, such as `TIMEOUT`, `PROVIDER_UNAVAILABLE`, and `INVALID_RESPONSE`, instead of storing raw provider messages. The first migration does not need a fixed SQL check list of those codes before the provider behavior is selected. Foreign keys do not cascade-delete failed attempts.

## Outside the first deliverable and known limits

- No manual creation or correction of attendance times, manager approval, payroll integration, notifications, or full audit trail. A missing clock-out and a failed attempt remain visible as unresolved exceptions; the MVP cannot determine their actual work times. We may reconsider one focused correction feature after the core flow is working, but it is not part of the current scope.
- No authentication: any user of the app may select an employee. This is a deliberate home-assignment simplification and is unsuitable for trusted payroll records.
- No automatic offline punch using a device or server clock. When the time API is unavailable, the user can retry, and the failed attempt may be logged separately; attendance is not fabricated.
- No dedicated idempotency-key protocol initially. State checks and database constraints protect against duplicate active shifts and duplicate closures, but a lost HTTP response may still require the client to refresh the employee's state to learn whether a punch succeeded.

## Open decisions for upcoming subtasks

The unusually long shift warning threshold; the external time provider, validation, and timeout; API paths and responses; and exact UI behavior. These are later-Subtask design questions, not decisions already approved.
