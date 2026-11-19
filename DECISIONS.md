# ClockIn — Decisions

Status: planning, 19 November 2026. This document records agreed product and architecture decisions. It does not claim that the features below have been implemented.

## Scope and delivery

ClockIn is a small employee attendance system built with ASP.NET Core (.NET 10), React with TypeScript, and SQL Server. The first deliverable focuses on a reliable clock-in/clock-out flow and a few meaningful exceptions rather than a broad workforce-management product.

The documented local SQL Server setup will use Docker Compose so someone cloning the repository can run it without the developer's existing local SQL Server installation. The exact Compose configuration is still to be designed.

## Agreed decisions

### Employee selection, not authentication

The interface will let a user select an employee from a small list. The backend will validate the selected employee ID. Selection is not proof of identity; authentication and roles are outside the initial scope. We will describe that limitation honestly and will not treat a selected employee as an authenticated actor.

### Attendance and shift states

An employee may have at most one active shift. Clock In opens one; Clock Out closes it. A shift may cross midnight and remains active until an explicit action changes its state. Repeating Clock In while a shift is active, or Clock Out when none is active, must not create or change attendance data and must return a clear response.

The initial shift model distinguishes an active shift, a completed shift, and a shift marked as missing a clock-out. If someone forgot to clock out, they can explicitly choose “I forgot to clock out.” This marks the old shift as incomplete, leaves its clock-out time empty, and allows a new Clock In. It does **not** invent a departure time or calculate a completed duration for that shift. There is no automatic closure after a fixed number of hours; the UI should make an unusually long active shift conspicuous while still allowing a legitimate long shift to be clocked out. The exact warning threshold remains open.

### Concurrent requests and database enforcement

Clock actions will use database transactions. The database will independently enforce at most one active shift per employee, so two concurrent Clock In requests cannot create two active shifts. Only one should succeed; the other receives a clear conflict response. A concurrent or repeated Clock Out must likewise not close the same shift twice. The exact schema, index predicate, and write pattern remain to be specified before implementation.

### Authoritative time

Every successful Clock In and Clock Out must use time obtained by the backend from an external time API for `Europe/Zurich`. Neither the browser clock nor the server's local clock is a substitute for an attendance timestamp. The initial implementation will use one external provider; the provider, response validation, storage type, and exact timeout policy have not yet been selected. The UI will offer a retry when time retrieval fails. A later successful attempt records the time obtained then; it does not silently backdate the punch.

### Failed time-service attempts

If the external time service fails during Clock In or Clock Out, no successful punch is recorded and no shift is opened or closed. If SQL Server is available, the backend will save a separate failed-attempt record containing the employee, requested action, failure reason, and a reference ID shown to the user. A server-observed request time may be kept there **only as diagnostic metadata**, clearly labelled non-authoritative. It must never be used as a Clock In/Out time or automatically converted into one. A failed-attempt record supports later investigation; it does not establish when work began or ended. If the database is also unavailable, the application cannot guarantee that the failed attempt was saved.

## Outside the first deliverable and known limits

- No manual creation or correction of attendance times, manager approval, payroll integration, notifications, or full audit trail. A missing clock-out and a failed attempt remain visible as unresolved exceptions; the MVP cannot determine their actual work times. We may reconsider one focused correction feature after the core flow is working, but it is not part of the current scope.
- No authentication: any user of the app may select an employee. This is a deliberate home-assignment simplification and is unsuitable for trusted payroll records.
- No automatic offline punch using a device or server clock. When the time API is unavailable, the user can retry, and the failed attempt may be logged separately; attendance is not fabricated.
- No dedicated idempotency-key protocol initially. State checks and database constraints protect against duplicate active shifts and duplicate closures, but a lost HTTP response may still require the client to refresh the employee's state to learn whether a punch succeeded.

## To decide before implementation

The concrete employee/shift/failed-attempt columns and constraints; state-transition details and unusually long shift warning; the external time provider and validation; API responses; the initial employee data; and the precise Docker Compose and database initialization approach. These are design questions, not decisions already approved.
