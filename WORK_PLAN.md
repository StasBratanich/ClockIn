# ClockIn — Working Plan

Updated: 21 November 2026
Status: **Subtask 1 complete; Subtask 2 in code review; Subtasks 3–6 planned**. This is the shared progress record, not a Codex implementation prompt.

## Story — Clock employees in and out with reliable time

### Description

Build a small attendance flow in which a selected employee can clock in, clock out, and mark a forgotten clock-out as incomplete. Store successful punches using time obtained from an external API for `Europe/Zurich`, prevent conflicting concurrent actions, and preserve failed time-service attempts separately for later investigation.

### Acceptance Criteria

- An employee can have one active shift at most. Clock In opens it and Clock Out completes it; shifts may cross midnight.
- A duplicate or conflicting action does not create a second active shift or close a shift twice, including when requests run concurrently.
- An employee can explicitly mark an open shift as missing its clock-out; no departure time or completed duration is invented, and a new shift can then begin.
- A successful punch uses external Zurich time. If that time is unavailable, no punch is saved; a failed-attempt reference is shown when the database can save one.
- A user can select an employee, see the current state and relevant incomplete shifts, perform available actions, and understand errors. Selection is not authentication.
- A fresh clone can be run locally with the documented Docker Compose SQL Server setup and EF Core migrations.

## Subtask map

| #   | Subtask                                           | Deliverable                                                           | Depends on                      | Status      |
| --- | ------------------------------------------------- | --------------------------------------------------------------------- | ------------------------------- | ----------- |
| 1   | DB - Create attendance schema and local SQL setup | EF Core model/migration and reproducible SQL Server                   | Approved schema                 | Done        |
| 2   | BE - Read authoritative Zurich time               | External time client and validation                                   | Approved time-provider contract | Code review |
| 3   | BE - Implement attendance operations              | EF Core reads/writes, state transitions, concurrency, failed attempts | 1, 2                            | Planned     |
| 4   | BE - Expose attendance API                        | Employee and attendance endpoints                                     | 3                               | Planned     |
| 5   | FE - Build employee attendance screen             | Selection, shift state, actions, feedback                             | 4                               | Planned     |
| 6   | Repo - Document and verify local run              | README and final decisions, fresh-start check                         | 1–5                             | Planned     |

Each Subtask includes its own tests. No separate testing Subtask is planned.

## Subtask 1 — DB - Create attendance schema and local SQL setup

### Task Description

Add persistent employee, shift, and failed-attempt data with an EF Core migration and a reproducible local SQL Server setup.

### Expected Result

The database stores employees; shifts that are active, completed, or missing a clock-out; and failed time-service attempts separately from successful punches. It enforces at most one active shift per employee. Docker Compose and a migration can create the database for a fresh clone.

### Implementation

1. Add a new EF Core model and DbContext under the existing `backend/ClockIn.Api` project; add a migration for the agreed columns, relationships, state checks, and uniqueness rule.
2. Add a root Docker Compose configuration for SQL Server and the configuration needed by the API to connect locally. Decide how the demo employee list is seeded.

### Tests

- Apply the migration to an empty SQL Server database and verify that valid rows can be persisted.
- Verify that the database rejects two active shifts for one employee.

### Completion record

- Implemented the EF Core schema and `20261119140652_InitialAttendanceSchema` migration, SQL Server Docker setup, database constraints, and seeded employees 1001, 1002, and 1003.
- Verified the schema and constraints with a successful isolated SQL verification against SQL Server.
- Later performed a read-only check confirming that the development database has migration `20261119140652_InitialAttendanceSchema`.
- Code review found no confirmed blocking defects, and the user accepted Subtask 1.
- No commit or push is recorded here; Git operations remain with the developer.

## Subtask 2 — BE - Read authoritative Zurich time

### Task Description

Add a focused backend client for one external time API.

### Expected Result

The client requests `Europe/Zurich`, returns a validated instant for successful punches, and reports timeout, invalid data, or provider failure without using the server or browser clock as a fallback.

### Implementation

1. Add a new time-provider client in the existing `backend/ClockIn.Api` project, with configuration for its URL and a bounded request timeout.
2. Validate the response and expose a small interface so attendance operations can be tested with controlled times.

### Tests

- Check a valid Zurich response and reject malformed or wrong-zone data.
- Check provider failure and timeout without a local-clock fallback.

### Progress record

- Implemented the configurable `IAuthoritativeTimeClient` and typed `WorldTimeClient` using the approved `worldtime.timezone.io` provider, with a five-second default timeout and no fallback or caching.
- Added validation for the required timezone, complete ISO 8601 timestamps with explicit offsets, the reported UTC offset, and a matching instant. Timestamp precision is preserved.
- Added focused controlled-HTTP tests for valid winter/summer responses, wrong zone, malformed/inconsistent responses, provider failure, and timeout.
- Added startup validation for an absolute HTTPS provider URL and a positive, finite timeout up to 2,147,483,647 milliseconds. Tests cover these limits and verify the configured timeout and disabled redirects through the production DI registration.
- Added regression coverage for date-less timestamps and fractions longer than seven digits. All 32 backend tests passed, and the backend solution built with no warnings or errors.
- Two complete-diff review iterations were performed, followed by a final review of the post-review fixes. No confirmed defects or open product questions remain. A real elapsed-time timeout and caller-cancellation test remain unverified. Subtask 2 is in Code review pending the user's inspection and approval.

### Agreed contract and configuration

The approved provider is WorldTime at `worldtime.timezone.io` for `Europe/Zurich`. The response must include the agreed timezone, timestamps, and offset. Store the validated instant in UTC and display it as `HH:mm` in Zurich time. The provider URL and request timeout are configurable through `AuthoritativeTime:ProviderUrl` and `AuthoritativeTime:Timeout`. The URL must be an absolute HTTPS URL, and the timeout defaults to five seconds within the range above.

## Subtask 3 — BE - Implement attendance operations

### Task Description

Implement the employee attendance rules using EF Core and the authoritative time client.

### Expected Result

Clock In, Clock Out, and “I forgot to clock out” make only valid state transitions. Database transactions and the unique active-shift rule resolve concurrent requests safely. A time-service failure leaves shifts untouched and, when SQL Server is available, saves a separate failed-attempt record with a reference ID and non-authoritative diagnostic metadata.

### Implementation

1. Add a new focused attendance service in `backend/ClockIn.Api` for reading state and applying the three actions with EF Core; keep external HTTP calls outside database transactions.
2. Translate expected conflicts and time-provider failures into explicit results for the API. Do not promote a failed-attempt timestamp into a successful punch.

### Tests

- Cover normal and rejected transitions, including a shift crossing midnight and a missing clock-out.
- Use SQL Server-backed tests for concurrent Clock In and repeated/concurrent Clock Out; verify one effective state change.
- Verify that a failed time call leaves shift data unchanged and creates an attempt record when the database is available.

Prerequisite: Approve the exact operation semantics and persistence pattern.

## Subtask 4 — BE - Expose attendance API

### Task Description

Expose the employee selection, attendance state, and three actions through the existing ASP.NET Core API.

### Expected Result

The frontend can list/select employees, load an employee's active and relevant incomplete shifts, and request Clock In, Clock Out, or a missing clock-out mark. Invalid employees, conflicts, and time-service failures receive clear responses; a saved failed attempt includes its reference ID.

### Implementation

1. Add new controller endpoints and request/response DTOs in `backend/ClockIn.Api`; call the attendance service rather than putting shift rules in the controller.
2. Map operation results to agreed HTTP responses and configure local frontend-to-API access as needed.

### Tests

- Exercise the success and key failure responses through the API, including a failed-attempt reference.

Prerequisite: Agree on endpoint paths and response shapes.

## Subtask 5 — FE - Build employee attendance screen

### Task Description

Build a focused React screen for selecting an employee and using the attendance flow.

### Expected Result

The screen shows the selected employee's current shift and incomplete shifts, offers actions appropriate to the current state, confirms a forgotten clock-out, and shows the result or a clear error. A time-service failure displays the saved attempt reference when present and offers a retry. A long open shift is visible without silently closing it.

### Implementation

1. Replace the starter screen in the existing `frontend/src` app and add a small API client/types for the agreed backend responses.
2. Refresh state after actions and when an action's result is uncertain; display punch times in Zurich time without generating attendance timestamps in the browser.

### Tests

- Check the main open/closed state actions, a forgotten clock-out, and the failure/retry message.
- Run the existing frontend lint/build checks.

Prerequisite: Agree on UI details and the long-shift warning threshold.

## Subtask 6 — Repo - Document and verify local run

### Task Description

Make the finished MVP reproducible and document the actual implementation and limits.

### Expected Result

The root README explains prerequisites, Docker Compose startup, migration/application startup, and a small manual demo. `DECISIONS.md` reflects final choices and known limitations, including failed attempts and missing clock-outs.

### Implementation

1. Update the existing root `README.md` and `DECISIONS.md` after the implementation is reviewed.
2. Follow the README from a clean local database and correct any missing startup step.

### Tests

- Confirm the documented startup and a Clock In → Clock Out flow against SQL Server, plus the two exception paths.

## Decisions still open for later subtasks

- Exact attendance operation semantics and persistence pattern.
- API endpoint paths and response shapes.
- Attendance screen behavior and the warning threshold for unusually long open shifts.

## Progress and review protocol

For each Subtask, update its status here: **Planned → In progress → Code review → Done**. Record the actual changes, tests and their results, open issues, and the user's review decision beneath that Subtask. “Done” means Codex's changes and tests were reviewed with the user, and the user has inspected the diff personally. A commit is reported separately; it is never inferred from “Done.”

The user creates feature branches from `dev` and alone performs stage, commit, push, and merge. The assistant suggests a commit message after each reviewed Subtask, using the repository's chosen convention. Do not ask Codex to perform Git mutations. `main` remains stable and `dev` is the integration branch.

The assignment explicitly requests Claude Code artifacts; this project uses Codex. Describe actual AI-assisted work truthfully in the submission, and do not present Codex output as Claude Code output.
