# ClockIn

Employee attendance system under development.

## Projects

- `backend/ClockIn.Api` — ASP.NET Core Web API (.NET 10)
- `frontend` — React and TypeScript with Vite

The backend includes the attendance schema, Zurich time client, and services for Clock In,
Clock Out, and marking a missing clock-out. The attendance HTTP endpoints and UI are planned.

## Prerequisites

- .NET 10 SDK
- Node.js and npm
- Docker Desktop with Docker Compose
- .NET EF tool (`dotnet-ef`), installed with `dotnet tool install --global dotnet-ef --version 10.0.15`

## Local SQL Server

From the repository root, copy `.env.example` to `.env` and change the local-only
password if desired. The `.env` file is ignored and must not be committed.

```powershell
Copy-Item .env.example .env
docker compose up -d sqlserver
```

Configure the single `ConnectionStrings:ClockIn` key through .NET user secrets
(recommended):

```powershell
cd backend/ClockIn.Api
dotnet user-secrets set "ConnectionStrings:ClockIn" "Server=localhost,14333;Database=ClockIn;User Id=sa;Password=<the-password-from-.env>;TrustServerCertificate=True;Encrypt=False"
dotnet ef database update
```

The password in the connection string must match `MSSQL_SA_PASSWORD` in `.env`.
As an alternative, provide the same key through the standard environment-variable
form and run the migration from the same shell:

```powershell
$env:ConnectionStrings__ClockIn = "Server=localhost,14333;Database=ClockIn;User Id=sa;Password=<the-password-from-.env>;TrustServerCertificate=True;Encrypt=False"
dotnet ef database update
```

The API and design-time factory both read only `ConnectionStrings:ClockIn`.
The `dotnet-ef` global tool install does not modify the tracked `.csproj` file.
Migrations are applied explicitly with `dotnet ef database update`; the API does
not migrate the database on startup. The initial migration seeds employees 1001,
1002, and 1003.

## Run the backend

From the repository root:

```powershell
cd backend/ClockIn.Api
dotnet run
```

The API prints its local address in the terminal. With the current default launch profile, it runs at http://localhost:5146

## Run backend tests

From the repository root, run the following with the local SQL Server running.
Use a SQL Server login that can create and drop the disposable test database.

```powershell
$env:CLOCKIN_TEST_SQLSERVER = "Server=localhost,14333;User Id=sa;Password=<the-password-from-.env>;TrustServerCertificate=True;Encrypt=False"
dotnet test backend/backend.slnx
```

The attendance integration tests create a uniquely named database, apply the migration,
and drop that database after the suite. They use controlled time-client responses.
Without `CLOCKIN_TEST_SQLSERVER`, the test runner reports the SQL Server tests as skipped.
A configured connection that fails causes the SQL Server tests to fail.

## Run the frontend

In a second terminal, from the repository root:

```powershell
cd frontend
npm install
npm run dev
```

Vite prints the local address for the frontend. At this stage, the frontend
and backend run separately and are not connected yet.
