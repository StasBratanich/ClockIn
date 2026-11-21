# ClockIn — Decisions

This document records agreed product and architecture decisions. Implementation progress is tracked in WORK_PLAN.md.

## Scope and delivery

ClockIn is a small employee attendance system built with ASP.NET Core (.NET 10), React with TypeScript, and SQL Server. The first deliverable focuses on a reliable clock-in/clock-out flow and a few meaningful exceptions rather than a broad workforce-management product.

The local SQL Server setup uses one tracked Docker Compose file. It runs SQL Server 2022, publishes on `127.0.0.1:14333` by default, and stores data in a named volume. The Quick Start helper uses the password from the ignored `.env`, waits for SQL Server, and supplies the canonical API connection key `ConnectionStrings:ClockIn` through `ConnectionStrings__ClockIn` in the current PowerShell process. It keeps an existing container and rejects a mismatched port binding. The default database is `ClockInDemo` so subsequent runs preserve attendance. A separate database name is available for a fresh demo. Apply EF Core migrations explicitly with `dotnet ef database update` after SQL Server starts.

The Quick Start helper clears existing ClockIn connection environment overrides
before validation and exports a new value only after Compose health and the
in-container login succeed. It checks the active Docker endpoint and pins its
Docker commands to a local named pipe or Unix socket. Confirm the helper's
printed target against EF's reported server and database before applying a
migration.

## Agreed decisions

### Employee selection, not authentication

The interface will let a user select an employee from a small list. The backend will validate the selected employee ID. Selection is not proof of identity; authentication and roles are outside the initial scope. We will describe that limitation honestly and will not treat a selected employee as an authenticated actor.

`Employees.Id` is an internal `int IDENTITY` primary key used by foreign keys. `EmployeeNumber` is a separate, required `varchar(20)` business identifier with a unique index; `FullName` is required `nvarchar(120)`. The business number is not a primary/foreign key and is not generated from the internal ID. The initial migration supplies three fixed demo employees numbered `1001`, `1002`, and `1003`. The demo has no employee-creation UI and does not impose an artificial database limit of ten employees.

### Attendance and shift states

An employee may have at most one active shift. Clock In opens one; Clock Out closes it. A shift may cross midnight and remains active until an explicit action changes its state. Repeating Clock In while a shift is active, or Clock Out when none is active, must not create or change attendance data and must return a clear response.

The initial shift model distinguishes an active shift, a completed shift, and a shift marked as missing a clock-out. If someone forgot to clock out, they can explicitly choose “I forgot to clock out.” This marks the old shift as incomplete, leaves its clock-out time empty, and allows a new Clock In. It does **not** invent a departure time or calculate a completed duration for that shift. The UI shows an informational warning once more than 12 hours have elapsed and keeps the existing shift actions available. Shifts stay open until an explicit action changes their state.

For the first migration, `Shifts` has `Id` (`int IDENTITY`), `EmployeeId` (`int`, required FK), `ClockInAtUtc` (`datetimeoffset`, required), `ClockOutAtUtc` (`datetimeoffset`, nullable), and `Status` (`tinyint`, required). The backend represents `Status` with a `ShiftStatus : byte` enum: `1=Open`, `2=Completed`, `3=MissingClockOut`. A database check permits only these states and requires a missing exit time for `Open`/`MissingClockOut`, or an exit later than the entry for `Completed`. Attendance timestamps are stored at offset `+00:00`; a check constraint enforces that representation. The database cannot prove the source of a timestamp; the later backend service must obtain it from the external API.

### Concurrent requests and database enforcement

The attendance service uses an atomic SQL Server write for each successful transition; it does not hold an explicit database transaction open while calling the external provider. Clock In checks for an existing open shift before requesting time. The unique filtered index on `Shifts.EmployeeId` where `Status=Open` resolves insert races and the service returns a conflict if another insert wins. Clock Out and the missing clock-out action both update the specific shift they read, conditional on that shift still being open. If a competing action has already closed or marked it, the update affects zero rows and the service reports that the open shift changed. A delayed Clock Out therefore cannot close a replacement shift. A provider instant at or before the clock-in time is rejected, as required by the schema. All provider calls finish before writes begin. Foreign keys to `Employees` do not cascade-delete attendance history.

### Authoritative time

Every successful Clock In and Clock Out must use time obtained by the backend from the configured provider for `Europe/Zurich`. `AuthoritativeTime:ProviderUrl` defaults to `https://worldtime.timezone.io/api/timezone/Europe/Zurich` and must be an absolute HTTPS URL. `AuthoritativeTime:Timeout` defaults to five seconds and must be positive and at most 2,147,483,647 milliseconds. The client does not cache times or fall back to another provider, and neither the browser clock nor the server's local clock is a substitute for an attendance timestamp.

A successful response must report `timezone == "Europe/Zurich"`, include valid `datetime`, `utc_datetime`, and `utc_offset` fields, and give a local offset that matches the reported offset. Both timestamps must represent the same instant and use complete ISO 8601 date-times with explicit offsets and at most seven fractional-second digits. Store the validated instant as UTC in SQL Server `datetimeoffset` columns, preserving the accepted precision, and show `HH:mm` in Zurich time in the UI. The UI will offer a retry when time retrieval fails. A later successful attempt records the time obtained then.

The MVP has one fixed business time zone: `Europe/Zurich`. The backend converts each validated provider instant to UTC for storage, and the UI displays attendance in Zurich time. There is no per-employee or per-shift time-zone column in the MVP. Supporting multiple regions later would require a time-zone identifier for selecting the external time request and interpreting local dates and display; the stored UTC instants remain valid.

### Failed time-service attempts

If the external time service fails during Clock In or Clock Out, no successful punch is recorded and no shift is opened or closed. For a non-canceled request, the backend attempts to save a separate failed-attempt record containing the employee, requested action, failure reason, and diagnostic metadata, and returns a reference ID only when that write succeeds. A server-observed request time may be kept there **only as diagnostic metadata**, clearly labelled non-authoritative. It must never be used as a Clock In/Out time or automatically converted into one. A failed-attempt record supports later investigation; it does not establish when work began or ended. If the database is also unavailable, the application cannot guarantee that the failed attempt was saved.

`FailedPunchAttempts` has `Id` (`int IDENTITY`, the reference ID), `EmployeeId` (`int`, required FK), `Action` (`tinyint`, `1=In` or `2=Out`, checked in the database), `FailureCode` (`varchar(32)`, required), and `ReceivedAtServerUtc` (`datetimeoffset`, required diagnostic metadata only). `ReceivedAtServerUtc` records when the server observed the failed request; it is never an authoritative attendance timestamp and must not be used to create, backdate, or infer a Clock In or Clock Out. The backend will map provider errors to its own stable codes, such as `TIMEOUT`, `PROVIDER_UNAVAILABLE`, and `INVALID_RESPONSE`, instead of storing raw provider messages. The first migration does not need a fixed SQL check list of those codes before the provider behavior is selected. Foreign keys do not cascade-delete failed attempts.

The attendance service writes `TIMEOUT`, `PROVIDER_UNAVAILABLE`, or `INVALID_RESPONSE` in a separate database write and returns the saved reference ID on success. If that write fails, the service still returns the time failure code with no reference ID. A caller cancellation cancels the diagnostic write with the request; persistence of that diagnostic is not guaranteed, and the service does not continue it independently of the canceled request. Requests rejected during employee or shift validation never call the provider. A successful retry creates a new punch from the new provider response and leaves the diagnostic row available for investigation. The attendance screen exposes the reference in the error and has no diagnostic history view.

### Attendance HTTP API

| Request | Successful response |
| --- | --- |
| `GET /api/employees` | 200 and an array of `id`, `employeeNumber`, and `fullName` objects, ordered by employee number |
| `GET /api/employees/{id}/attendance` | 200 and an object containing nullable `openShift` and the `missingClockOutShifts` array |
| `POST /api/employees/{id}/attendance/clock-in` | 200 and the resulting shift |
| `POST /api/employees/{id}/attendance/clock-out` | 200 and the resulting shift |
| `POST /api/employees/{id}/attendance/missing-clock-out` | 200 and the resulting shift |

The path ID is the employee's internal integer ID. Action requests have no body. A shift response contains `id`, `employeeId`, `clockInAtUtc`, nullable `clockOutAtUtc`, and a string `status` whose value is `Open`, `Completed`, or `MissingClockOut`. Punch timestamps come from the service's stored UTC instants. The attendance state excludes completed shifts.

Errors use ASP.NET Core ProblemDetails with a stable `code` extension.

| HTTP status | Codes |
| --- | --- |
| 404 | `EMPLOYEE_NOT_FOUND` for an unknown employee or an ID that cannot be routed as a 32-bit integer, including when the request also uses an unsupported method |
| 405 | `METHOD_NOT_ALLOWED` for an unsupported method on an attendance state or action route with a valid integer ID, with the supported methods in the `Allow` header |
| 409 | `OPEN_SHIFT_ALREADY_EXISTS`, `NO_OPEN_SHIFT`, `OPEN_SHIFT_CHANGED`, `CLOCK_OUT_NOT_AFTER_CLOCK_IN` |
| 503 | `TIMEOUT`, `PROVIDER_UNAVAILABLE`, `INVALID_RESPONSE` |
| 500 | `INTERNAL_ERROR` for unexpected errors, including database failures during attendance reads or writes |

A 503 includes a numeric `failedAttemptReferenceId` extension only if the service saved the diagnostic. Error responses omit raw provider messages, connection strings, and exception details. Attendance rules remain in the service.

### Attendance screen

The responsive screen loads the employee list from the API and uses an explicit selection to load attendance. It shows the selected employee's open shift and shifts missing a clock-out. With no open shift the available action is Clock In. With an open shift the available actions are Clock Out and “I forgot to clock out,” which requires a separate confirmation. Missing clock-outs show the stored clock-in date and time. All punch times display with their local date and 24-hour time in `Europe/Zurich`.

Completed shifts are persisted in SQL Server. The screen confirms Clock Out after the action and provides no completed-shift history view.

The browser clock determines whether more than 12 hours have elapsed since the stored clock-in instant. The warning is informational and can be affected by an inaccurate device clock. Punch timestamps always come from the API.

The screen shows request progress, success, conflicts, and errors. A recognized time-provider failure displays its stable code and the failed-attempt reference when supplied. Retry sends a fresh punch request only when clicked. The frontend validates successful response bodies and gives each request 15 seconds, including reading its body. After successful actions, conflicts, or uncertain outcomes such as a timeout or an invalid punch response, the screen reads attendance once. A failed read leaves actions unavailable until the user can load attendance with Refresh attendance. A punch and its reconciliation read lock the controls against concurrent submission or selection changes. Attendance reads for a prior selection cannot replace the current selection's state. The API accepts the employee ID for closing actions, so a shift can still change on another client between the displayed read and a new action request.

A timed-out punch can still finish on the server after the reconciliation read. The screen keeps the outcome marked as uncertain and leaves Refresh attendance available for another read.

Successful Clock Out responses must contain a valid exit instant strictly later than entry. The frontend checks ordering at the API's full seven-digit fractional precision. When reloading employees, keyboard focus stays in the selection panel and moves to the enabled selector or the reload button when the request finishes.

## Outside the first deliverable and known limits

- No manual creation or correction of attendance times, manager approval, payroll integration, notifications, or full audit trail. Missing clock-outs and diagnostic records persist for investigation. Actual work times for those exceptions remain unknown.
- No authentication: any user of the app may select an employee. This is a deliberate home-assignment simplification and is unsuitable for trusted payroll records.
- No automatic offline punch using a device or server clock. When the time API is unavailable, the user can retry, and the failed attempt may be logged separately; attendance is not fabricated.
- No dedicated idempotency-key protocol initially. State checks and database constraints protect against duplicate active shifts and duplicate closures, but a lost HTTP response may still require the client to refresh the employee's state to learn whether a punch succeeded.
