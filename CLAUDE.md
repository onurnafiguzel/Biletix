# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Biletix is a Ticketmaster-style ticketing system built as a **modular monolith** (ASP.NET Core 8). The README is in Turkish; this file summarizes the architecture-critical parts.

## Code conventions (project rules — apply when writing/modifying C#)

- **SOLID + design patterns, fully applied.** Every type single-responsibility; depend on abstractions, not concretions (extend the existing `IEventsModule` interface-boundary style). Use the appropriate Gang-of-Four / enterprise pattern (Strategy, Factory, Adapter, Decorator, Repository, etc.) when it clarifies intent instead of hand-rolling ad-hoc branching. Prefer composition over inheritance.
- **Primary constructors (C# 12).** Use primary constructors for dependency injection and simple field initialization rather than an explicit constructor + backing fields — e.g. `internal class EventsModule(AppDbContext db) : IEventsModule`. (`.NET 8` supports C# 12.)
- **Readable, OOP-first code.** Intention-revealing names, small cohesive methods, real encapsulation (no anemic public mutable state). Code should read top-to-bottom like prose; match the surrounding file's style.

## Commands

All runtime services are orchestrated by Docker Compose under `build/`.

```bash
# Build + start the whole stack (API, Postgres, Redis, Kafka/Connect/ksqlDB, Elasticsearch)
cd build && docker compose up -d --build      # API migrations run automatically at startup

# Register the CDC search pipeline (run ONCE on a fresh cluster, after compose is up)
cd build && ./register-connectors.sh          # bash; sets REPLICA IDENTITY, Debezium, ksqlDB, ES index + sink

# Compile locally (no services needed)
dotnet build Biletix.sln

# Add an EF Core migration (migrations live in and are applied from Biletix.Api)
dotnet ef migrations add <Name> --project src/Biletix.Api

# Overselling load test (needs a seeded event id; works on PowerShell 5.1 and 7)
pwsh ./scripts/oversell-test.ps1 -EventId <guid>   # or: powershell -File ...
```

There is **no test project / test framework** in this repo. `scripts/oversell-test.ps1` is the only automated check (a concurrency smoke test, not a unit test). The Postman collection (`postman/`) and README "Smoke Test" section are the manual verification path.

Service ports (host): API **8080**, Postgres 5432, Redis 6379, Kafka 9092, Kafka Connect REST **8085**, ksqlDB 8088, Elasticsearch 9200.

## Architecture: the two ideas that drive everything

The system deliberately splits into a **CP path** (buying tickets) and an **AP path** (search), each with its own correctness trade-off. Understanding these two is the key to the codebase.

### 1. Overselling protection (CP path) — Redis, not DB locks

The concurrency barrier lives **outside the database**, in Redis. `TicketLockService` (`src/Biletix.Modules.Bookings/Services/TicketLockService.cs`) runs a **single atomic Lua script** that checks all `ticket:{id}` keys with `EXISTS` then `SET ... PX` (120s TTL) — all-or-nothing, no partial-lock state, no rollback path. This is the only overselling boundary; `SELECT ... FOR UPDATE`-style DB row locks are intentionally not used.

The booking flow in `src/Biletix.Modules.Bookings/BookingsEndpoints.cs` (`POST /bookings/{eventId}`) is: validate (status check → 409) → `TryAcquireAllAsync` (lock → 409 if taken) → payment (release lock + 402 on failure) → **one DB transaction** that does `bookings` INSERT + `tickets` UPDATE.

**Non-obvious detail:** the cross-module call `IEventsModule.MarkTicketsBookedAsync` (`src/Biletix.Modules.Events/EventsModule.cs`) intentionally does **not** call `SaveChanges`. It only mutates tracked entities; the *endpoint* owns `SaveChangesAsync` + `tx.CommitAsync`. So booking + ticket update land in one transaction / one WAL change-set. Do not add a `SaveChanges` inside the module method.

### 2. Search sync (AP path) — Postgres CDC, fully automatic

The application **never writes to Elasticsearch and contains no Kafka consumer code** (no Kafka client in any `.csproj`). Sync is entirely declarative infrastructure:

```
Postgres WAL → Debezium (Kafka Connect) → Kafka topics → ksqlDB join → `events` topic → ES Sink (Kafka Connect) → Elasticsearch
```

- Debezium config: `build/connectors/debezium-postgres.json` (pgoutput, slot `biletix_slot`, snapshot `initial`).
- ksqlDB denormalization: `build/ksqldb/search-pipeline.sql` — joins `events`+`performers`+`venues` in **two steps** (ksqlDB forbids two FK-joins in one statement) into a snake_cased topic **named `events` on purpose** (the Confluent ES sink uses the topic name as the index name).
- ES sink: `build/connectors/elasticsearch-sink.json` (upsert by key, delete on tombstone).
- The Search module (`src/Biletix.Modules.Search/SearchEndpoints.cs`) only **reads** ES (`multi_match` + fuzzy). Postgres stays the single source of truth; ES is a derived view.

Kafka Connect is a **custom image** (`build/connect/Dockerfile`) because the stock Debezium image lacks the Confluent ES sink plugin. `docker compose up --build` bakes it in.

## Modular monolith wiring

Single host `Biletix.Api`, single `AppDbContext`, single Postgres. Three modules + a shared project:

- `Biletix.Modules.Events` — events, venues, performers, ticket inventory. Exposes the public `IEventsModule` interface.
- `Biletix.Modules.Bookings` — purchase flow, Redis lock, payment. Depends on `Biletix.Modules.Events` and calls `IEventsModule` **directly in-process** (no RabbitMQ/HTTP, no integration events — same transaction).
- `Biletix.Modules.Search` — read-only ES search. Has no entities.
- `Biletix.Shared` — `AppDbContext`, DTOs/contracts (`Contracts/Dtos.cs`).

**Entity configuration discovery:** each module ships its own entities and `IEntityTypeConfiguration<T>` implementations. `AppDbContext.OnModelCreating` (`src/Biletix.Shared/Persistence/AppDbContext.cs`) runs `ApplyConfigurationsFromAssembly` over the module assemblies listed in `Program.cs` (`moduleAssemblies`). **When adding a new module that owns entities, add its assembly to that array in `src/Biletix.Api/Program.cs`** or its configs won't be picked up. Endpoints are wired via `Add*Module()` / `Map*Endpoints()` extension methods, also in `Program.cs`.

## Gotchas

- **`appsettings.json` uses Docker-network hostnames** (`postgres`, `redis`, `elasticsearch`). To run the API outside Docker, override the connection strings to `localhost`.
- **Enum JSON serialization is numeric.** `TicketStatus` serializes as integers in API responses (`0`=Available, `1`=Reserved, `2`=Booked), not strings — relevant when scripting against the API. `Reserved` exists in the enum but is unused (the flow goes `Available → Booked` directly).
- **ksqlDB output topic must stay named `events`** (equals the ES index name); renaming it breaks the sink.
- Single-broker Kafka requires `transaction.state.log.replication.factor=1` (already set in `build/docker-compose.yml`) or ksqlDB times out.
- After a successful booking the Redis lock is **not** explicitly released; it expires via TTL (the DB row is already `Booked`, so the status check rejects retries in the meantime).
