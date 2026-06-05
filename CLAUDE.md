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

### 1. Overselling protection (CP path) — DB-authoritative, Redis advisory

The authoritative oversell guard is an **atomic compare-and-set on `tickets.status`** in Postgres (EF Core `ExecuteUpdateAsync`), owned by the Events module. Redis (`TicketLockService`, `src/Biletix.Modules.Bookings/Services/TicketLockService.cs`) is only an **advisory fast-path** that fails fast under contention — a Redis outage is swallowed and the flow still relies on the DB. So oversell is impossible even if Redis is down or wrong.

The flow in `src/Biletix.Modules.Bookings/BookingsEndpoints.cs` (`POST /bookings/{eventId}`) is **Reserve → Pay → Confirm**:
1. **Reserve (tx1):** `IEventsModule.TryReserveTicketsAsync` CAS `Available → Reserved` (also reclaims an expired `Reserved` hold). Wrapped in a transaction; if affected rows ≠ requested, roll back → 409 *before any charge*.
2. **Pay:** `IPaymentGateway.ChargeAsync`; on failure release the hold → 402.
3. **Confirm (tx2):** `TryConfirmTicketsAsync` CAS `Reserved → Booked` (only for this `bookingId`) + `bookings` INSERT, in one transaction. If the hold was lost during payment, roll back + `RefundAsync` + release → 409.

**Key points:** ticket-state transitions are atomic `ExecuteUpdateAsync` CAS in `EventsModule` (`src/Biletix.Modules.Events/EventsModule.cs`) — they bypass the change tracker but run in the caller's ambient transaction. Holds carry `ReservedBy` + `ReservedUntil`; expired holds self-heal (reclaimed by the next reserve) and are also swept by `ReservationSweeper` (a `BackgroundService`). The post-charge crash window (guaranteed refund) is out of scope here — see BILETIX-7 in `JIRA.md`.

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
- **Enum JSON serialization is numeric.** `TicketStatus` serializes as integers in API responses (`0`=Available, `1`=Reserved, `2`=Booked), not strings — relevant when scripting against the API. All three states are used: the booking flow holds tickets as `Reserved` between reserve and confirm.
- **ksqlDB output topic must stay named `events`** (equals the ES index name); renaming it breaks the sink.
- Single-broker Kafka requires `transaction.state.log.replication.factor=1` (already set in `build/docker-compose.yml`) or ksqlDB times out.
- The Redis lock is advisory only and is released best-effort on every terminal path (and would expire via its 120s TTL anyway). Correctness never depends on it — the DB CAS is the gate.
