# ClockIn — Coding Standards and Workflow

This document defines development practice. `AGENTS.md` is the single AI instruction entry point; it refers to this document. Product behavior belongs in work plans and `DECISIONS.md`.

## Boundaries for AI assistance

- The developer makes product and architecture decisions and reviews every implementation change.
- The AI may change code, tests, and documentation within the agreed task and run relevant non-destructive checks.
- The AI must not create commits, stage files, push, merge, rebase, reset branches, rewrite history, publish, or create/delete branches without an explicit instruction for that specific action. The developer owns the code review and commits.
- The AI must not add secrets to tracked files, reveal credentials, delete user data, or run destructive database operations without explicit authorization.
- Do not introduce unrelated changes. Raise open decisions before implementing behavior that depends on them.
- Never state that a tool, test, or review was performed unless it actually was.

## Collaboration and review

- Before a slice, briefly describe its intended behavior, the main edge cases, and a recommended approach. Discuss consequential alternatives with the developer.
- Work in small vertical slices. Keep changes focused and avoid speculative scaffolding.
- At the end, provide a short summary: what changed, why this approach, and which checks actually ran. If a check failed or a significant part was not verified, say so briefly.
- The developer reviews the diff independently in VS Code. Answer questions about the new code, explain tradeoffs, and revise it when needed before the developer decides on a commit. Do not require the AI to print the diff in its response.

## Git workflow

- `main` is the stable branch. `dev` branches from `main` and is the integration branch. Work on a feature branch such as `feature/clock-in-api` branched from `dev`; after developer review, integrate it into `dev`. The developer manages all branch operations.
- The developer decides when and how to create, stage, commit, merge, and push. The AI may suggest a branch name or commit message but must not perform those actions without a separate explicit instruction.
- Before a developer commit, review the diff and ensure generated and machine-specific output is ignored: `.vs/`, `node_modules/`, `bin/`, `obj/`, build output, local DB files, and actual `.env` secrets.
- Keep schema migrations with the change that needs them. Update relevant documentation alongside behavior changes.

## Architecture

- Keep the React + TypeScript frontend and ASP.NET Core API in separate projects within this repository, with SQL Server as persistence.
- Controllers handle HTTP concerns and delegate use cases to services. Services coordinate business behavior and use EF Core `DbContext` for persistence. Add a focused persistence abstraction only when it solves a concrete need; avoid a generic repository by default.
- Use an explicit database transaction where multiple writes require atomicity or a specific concurrency approach requires it; do not open one for every service method. Do not hold a transaction open during external API requests without a design reason.
- The backend owns business rules, persistence, and external service integration. The frontend uses typed HTTP calls and renders server-confirmed state.
- Keep frontend components focused. Keep styles close to a component when useful and shared styles in one place when reused. Extract reusable components after a real reuse case appears; do not force generic components or a CSS file for every component.
- Keep EF Core entity models, mappings, `DbContext`, and versioned migrations in the backend. Migrations define tables, columns, keys, relationships, constraints, and indexes. Add a human-readable schema description if it helps, but do not maintain a second competing schema definition.
- Record the concrete directory layout and dependency boundaries in `ARCHITECTURE.md` once agreed; do not infer a new layer or project solely from this document.

## Code and verification

- Use descriptive names and small cohesive units. Use async APIs for I/O; validate inputs at boundaries and return consistent errors without exposing internal details.
- Parameterize queries. Keep configuration environment-specific and commit safe examples rather than credentials.
- Add tests for consequential business behavior and failure paths, especially behavior that can corrupt records. Avoid tests that only repeat implementation details.
- Run checks relevant to changed code before presenting it for review, such as the .NET build/tests and frontend lint/typecheck/build. State briefly what actually ran and any material failure.
- Review EF migrations and test schema changes against a disposable database when introducing them.

## Documentation

- `README.md` explains setup and local run, including database setup.
- `DECISIONS.md` captures consequential choices, alternatives, edge cases, deferred work, and known limitations.
- `AGENTS.md` is the only AI instruction entry point. Keep substantive standards here and link them from `AGENTS.md`; do not create a duplicate `AGENTS.md`.
- Document the actual development process and tool contributions honestly.
