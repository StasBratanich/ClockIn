# ClockIn

Employee attendance system under development.

## Projects

- `backend/ClockIn.Api` — ASP.NET Core Web API (.NET 10)
- `frontend` — React and TypeScript with Vite

The database and attendance functionality have not been implemented yet.

## Prerequisites

- .NET 10 SDK
- Node.js and npm

## Run the backend

From the repository root:

```powershell
cd backend/ClockIn.Api
dotnet run
```

The API prints its local address in the terminal. With the current default launch profile, it runs at http://localhost:5146

## Run the frontend

In a second terminal, from the repository root:

```powershell
cd frontend
npm install
npm run dev
```

Vite prints the local address for the frontend. At this stage, the frontend
and backend run separately and are not connected yet.
