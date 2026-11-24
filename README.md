# Katalepsis Fund Terminal (FT)

Multi-manager portfolio monitor and order execution: a master fund split into sleeves, each run by several managers, across spot, perps, options and traditional instruments.

- What it does and the delivery phases: [docs/plan.md](docs/plan.md)
- The system as built, including order execution: [docs/system-architecture.md](docs/system-architecture.md)
- The original monitoring design and its decisions: [docs/architecture.md](docs/architecture.md)

## Status

Phase 0, skeleton. What exists today:

- Built-in sign-in with two roles, Admin and Viewer: `/api/auth/login`, `logout`, `me`, `change-password`.
- `/health` (is the process up) and `/health/ready` (can it reach its stores). Detail for Admins at `/api/system/status`.
- A worker started as `cefi`, `defi` or `core` that publishes a heartbeat through Key Value.
- Commands: `check` (both stores answer) and `migrate` (schema, roles, first Admin).

## Run locally

Needs the .NET 8 SDK, Postgres and Redis or Valkey.

```bash
export DATABASE_URL=postgresql://user:password@localhost:5432/katalepsis_ft
export KEYVALUE_URL=redis://localhost:6379
dotnet run --project src/KatalepsisFT.Web
dotnet run --project src/KatalepsisFT.Worker -- --group core
```

```bash
dotnet run --project src/KatalepsisFT.Jobs -- migrate
```

Tests need a Postgres server where the user may create databases:

```bash
export TEST_DATABASE_URL=postgresql://postgres:postgres@localhost:5432/postgres
dotnet test
```

## Deploy

`render.yaml` describes the production environment on Render: two web instances, three workers, Postgres and Key Value. Every push to `main` that passes CI is deployed.
