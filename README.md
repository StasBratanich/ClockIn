# ClockIn

Employee attendance system.

## Projects

- `backend/ClockIn.Api` — ASP.NET Core Web API (.NET 10)
- `frontend` — React and TypeScript with Vite

The backend includes the attendance schema, Zurich time client, and HTTP endpoints for
employee selection, attendance state, Clock In, Clock Out, and marking a missing clock-out.
The frontend provides an employee attendance screen for those operations.

## Prerequisites

- .NET 10 SDK, or a newer SDK with the ASP.NET Core 10 runtime installed.
  Verified with SDK 11.0.100 and runtime 10.0.15.
- Node.js and npm. The frontend test suite uses jsdom and requires Node.js
  `^22.22.2 || ^24.15.0 || >=26.0.0`. Verified with Node.js 26.13.1 and npm 10.8.3.
- Docker Desktop with Docker Compose
- .NET EF tool (`dotnet-ef`), installed with `dotnet tool install --global dotnet-ef --version 10.0.15`
- Outbound HTTPS access from the API to the [Zurich time provider](https://worldtime.timezone.io/api/timezone/Europe/Zurich) for Clock In and Clock Out.

## Quick Start

**First run.** Open PowerShell at the repository root and create the ignored
`.env` if it is missing.

```powershell
if (-not (Test-Path -LiteralPath .env)) {
    [IO.File]::Copy((Join-Path $PWD .env.example), (Join-Path $PWD .env))
}
```

For a new SQL Server volume, edit `.env` once, save it as UTF-8, and replace the example
`MSSQL_SA_PASSWORD` with a strong, local-only password. Wrap the value in
single quotes. For an existing volume, keep its configured password. See
[Advanced](#advanced) if the password contains a single quote or if you have an
older SQL Server container.

```powershell
.\Start-ClockInDatabase.ps1
```

The helper requires a local Docker context and uses the tracked
`docker-compose.yml` to start SQL Server on `127.0.0.1:14333` without recreating
an existing container. It waits for Compose
health, then verifies an `sa` login inside the container using the password from
`.env`. Each Docker inspection or configuration command has 30 seconds. Startup
has a 10-minute limit to allow for an image pull, and login verification has
15 seconds. The helper clears inherited ClockIn environment overrides at the start of each
run. On success, it prints the server and database target and sets
`ConnectionStrings__ClockIn` in this PowerShell session. That setting takes
precedence over user secrets.

If the helper reports an error or does not print a target, stop here. Resolve
the error and rerun the helper before continuing. After it succeeds, check the
EF target in that same terminal.

```powershell
cd backend/ClockIn.Api
dotnet restore
dotnet ef dbcontext info
```

Apply the migration only after confirming that the helper's printed target and
EF's reported data source and database both identify `127.0.0.1` on port
`14333` and `ClockInDemo`.

```powershell
dotnet ef database update
```

After the migration succeeds, start the API in that terminal.

```powershell
dotnet run --launch-profile http
```

In a second PowerShell terminal at the repository root, start the frontend.

```powershell
cd frontend
npm ci
$env:CLOCKIN_API_TARGET = 'http://localhost:5146'
npm run dev
```

Open the URL Vite prints. The initial migration seeds exactly **1001**, **1002**,
and **1003**. Select **1001** and click **Clock In** to see the open shift.

**Subsequent runs.** Keep `.env` and the Compose volume. Rerun the helper from
the repository root. If the helper fails, stop and rerun it successfully before
continuing. Confirm the printed server and database against
`dotnet ef dbcontext info` as above before running `dotnet ef database update`
when new migrations are available. Start the API and frontend as above, and
run `npm ci` when dependencies change. Existing attendance stays in
`ClockInDemo`, so an open shift still shows **Clock Out** after a restart.

## Run backend tests

From the repository root, run the following with the local SQL Server running.
Use a SQL Server login that can create and drop the disposable test database.

```powershell
$clockInSqlPassword = Read-Host "Local SQL Server password" -AsSecureString
$clockInConnection = [System.Data.Common.DbConnectionStringBuilder]::new()
$clockInConnection['Server'] = '127.0.0.1,14333'
$clockInConnection['User Id'] = 'sa'
$clockInConnection['Password'] = [System.Net.NetworkCredential]::new('', $clockInSqlPassword).Password
$clockInConnection['TrustServerCertificate'] = 'True'
$clockInConnection['Encrypt'] = 'False'
$env:CLOCKIN_TEST_SQLSERVER = $clockInConnection.ConnectionString
Remove-Variable clockInSqlPassword, clockInConnection

dotnet test backend/backend.slnx
dotnet build backend/backend.slnx
```

The service and HTTP attendance integration suites each create a uniquely named database,
apply the migration, and drop their database after the suite. Both use controlled time-client
responses.
Without `CLOCKIN_TEST_SQLSERVER`, the test runner reports the SQL Server tests as skipped.
A configured connection that fails causes the SQL Server tests to fail.

## Run frontend checks

From `frontend`, run `npm test` for the frontend suite, `npm run lint`
for lint checks, and `npm run build` for TypeScript and the production build.

## Advanced

### SQL Server troubleshooting

The Compose commands below assume `COMPOSE_PROJECT_NAME` is unset in your shell.

- Compose reads the single-quoted password literally, including `$`. Escape a
  single quote within it as `\'`. For example, the value
  `'Example$Only;It\'sLiteral9'` represents `Example$Only;It'sLiteral9`.
  See the [Compose .env syntax](https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/#env-file-syntax).
- For a slow or failed start, check `docker compose -f docker-compose.yml logs --tail 50 sqlserver`
  from the root. If login fails against an existing volume, check that `.env`
  still has that volume's password. Editing `.env` alone leaves the stored
  SQL Server password as it was.
- If the helper rejects the Docker context, select your local Docker Desktop
  context and check for `DOCKER_HOST` or `DOCKER_CONTEXT` overrides before
  retrying. The helper accepts local named pipes and Unix sockets.
- If the helper reports an older or different port binding, inspect it with
  `docker compose -f docker-compose.yml ps --all`. To leave that container
  alone, run from a separate checkout with a different directory name and pass
  a free loopback port, for example `.\Start-ClockInDatabase.ps1 -SqlPort 15433`.
  Use that same port on subsequent runs of that checkout.
- To stop SQL Server for the current checkout while retaining its named volume,
  run `docker compose -f docker-compose.yml stop sqlserver` from the root.

### Fresh database for an isolated demo

From the repository root, generate a separate database name and use the helper
to target it. Before invoking Docker, the helper rejects blank names, names that
contain NUL or exceed 128 characters, and the SQL Server system databases
`master`, `tempdb`, `model`, and `msdb`.

```powershell
$clockInDemoDatabase = "ClockInDemo_$([Guid]::NewGuid().ToString('N'))"
.\Start-ClockInDatabase.ps1 -Database $clockInDemoDatabase
```

If the helper fails, stop and rerun it successfully before continuing.

```powershell
cd backend/ClockIn.Api
dotnet restore
$clockInDemoDatabase
dotnet ef dbcontext info
```

Confirm the helper and EF both report `127.0.0.1` on port `14333` and the
generated database name, then open `sqlcmd` in the container.

```powershell
docker compose -f ../../docker-compose.yml exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S 127.0.0.1,1433 -U sa -d master -C
```

Enter the literal password at the `sqlcmd` prompt. Run `SELECT 1;` followed by `GO` to
check readiness, then substitute the generated name below and run the query.

```sql
SELECT DB_ID(N'<generated database name>');
GO
```

Once `DB_ID` returns `NULL`, enter `EXIT` and run `dotnet ef database update`
in the same terminal, then start the app using the Quick Start commands.
Migrations run only when invoked explicitly.

### API and frontend options

Use [ClockIn.Api.http](backend/ClockIn.Api/ClockIn.Api.http) for employee,
attendance, and bodyless action requests. Use the internal employee ID returned
by `GET /api/employees` in attendance routes. See
[DECISIONS.md](DECISIONS.md#attendance-http-api) for response fields and errors.

Vite forwards `/api` to `http://localhost:5146` by default. Set
`CLOCKIN_API_TARGET` to the API URL in the frontend terminal if using a
different address. A deployed frontend expects its host to route same-origin
`/api` requests to ClockIn.Api.

### Manual demo

Use a fresh isolated demo database so each seeded employee starts with no shifts.

1. Select employee **1001**. Click **Clock In**, then **Clock Out**. Check both
   confirmations and their local dates and times under **Times in Europe/Zurich**.
   The attendance panel should return to **No open shift**.
2. Select employee **1002** and clock in. Choose **I forgot to clock out**,
   then **Mark missing clock-out**. The old shift appears under **Missing
   clock-outs** with only its clock-in time. Start a new shift and confirm
   that the old entry remains listed.
3. To reproduce a provider outage locally, keep Vite running and stop the API.
   In the API terminal, keep the isolated database connection and temporarily
   point the provider at an unused loopback port:

   ```powershell
   $env:AuthoritativeTime__ProviderUrl = "https://127.0.0.1:55442/api/timezone/Europe/Zurich"
   dotnet run --launch-profile http
   ```

   With employee **1003** selected and no open shift, click **Clock In**.
   Expect `PROVIDER_UNAVAILABLE`, a failed-attempt reference if SQL Server saved
   the diagnostic, and **No open shift**. Leave the page open. Stop the API,
   clear the temporary override, and restart it in the same terminal:

   ```powershell
   Remove-Item Env:AuthoritativeTime__ProviderUrl
   dotnet run --launch-profile http
   ```

   Click **Retry** on the existing error. The new shift uses the Zurich time
   obtained on that request.
