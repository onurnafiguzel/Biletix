# Biletix Redis Cluster (3 master + 3 replica)

Physical multi-node Redis Cluster that backs the advisory ticket lock
(`TicketLockService`). It is the second half of **BILETIX-2** — the code half (hash-tag
keys) was already shipped; this is the running infrastructure.

> **Why a cluster at all?** After BILETIX-1 the lock is *advisory* — the authoritative
> oversell guard is the Postgres CAS. So the cluster is not here for correctness; it is here
> for **availability + horizontal scale** of the fast-path, and to make the failover
> trade-offs real enough to observe. Killing a master must *not* cause overselling — the
> chaos test proves it.

## Topology

```
        masters (own the 16384 hash slots)        replicas (1 each, async)
        ┌───────────────┐  ┌───────────────┐  ┌───────────────┐
        │ redis-c1      │  │ redis-c2      │  │ redis-c3      │   ← 172.28.0.11/.12/.13
        │ 172.28.0.11   │  │ 172.28.0.12   │  │ 172.28.0.13   │
        └──────┬────────┘  └──────┬────────┘  └──────┬────────┘
               │ replicates       │                  │
        ┌──────▼────────┐  ┌──────▼────────┐  ┌──────▼────────┐
        │ redis-c4      │  │ redis-c5      │  │ redis-c6      │   ← 172.28.0.14/.15/.16
        └───────────────┘  └───────────────┘  └───────────────┘
```

- **3 masters** is the minimum for a failover-capable cluster: promoting a dead master's
  replica needs a **majority of masters** to vote. With 2 masters a lone survivor is not a
  majority; with 3, two survivors are. (Replicas do not vote.)
- **3 replicas** (`--cluster-replicas 1`) are the **HA** ingredient. Drop them and you still
  get sharding, but a master death loses its slots. They are not required for *correctness*
  here (the DB is the fence) — they keep the fast-path available during a node failure.

## How it boots (`docker compose up -d --build`)

1. Six `redis:7-alpine` nodes start with [`redis.conf`](redis.conf) (`cluster-enabled yes`,
   `appendonly yes`, `cluster-config-file nodes.conf`) and a per-node
   `--cluster-announce-ip 172.28.0.1X`.
2. Each node gets a **static IP** on the compose default network (subnet `172.28.0.0/16`),
   so its persisted `nodes.conf` stays valid across restarts — the classic "dynamic IP +
   persisted cluster state = broken cluster after `up`" trap is avoided.
3. The one-shot **`redis-cluster-init`** service waits for all six to answer `PING`, then runs
   [`init-cluster.sh`](init-cluster.sh): if the cluster is already `cluster_state:ok` it does
   nothing (idempotent), otherwise `redis-cli --cluster create … --cluster-replicas 1
   --cluster-yes`.
4. **`biletix-api`** starts only after `redis-cluster-init` exits 0 (`service_completed_
   successfully`) and is pointed at the cluster via
   `Redis__ConnectionString=redis-c1:6379,redis-c2:6379,redis-c3:6379` (3 master seeds;
   StackExchange.Redis discovers the rest). Because `AbortOnConnectFail=false` and the lock is
   advisory, the API would start even if the cluster were still forming.

## The hostname / announce-ip caveat (why we inspect with `docker exec`)

Cluster clients are *redirected* (`MOVED`/`ASK`) to the IP a node **announces** — here
`172.28.0.11..16`. Containers on the compose network can reach those IPs, so the **API works**.
A `redis-cli -c` from the **Windows host** cannot route to `172.28.x.x`, so host access would
hang on the first redirect. That is why the node ports are **not** published and all inspection
below uses `docker exec` from inside the network.

## Inspect it

```bash
# Cluster healthy? (cluster_state:ok, all 16384 slots assigned)
docker exec biletix-redis-c1 redis-cli -c cluster info

# Who is master / replica, and which slots each master owns
docker exec biletix-redis-c1 redis-cli -c cluster nodes

# Prove the hash-tag puts a whole event's tickets in ONE slot:
docker exec biletix-redis-c1 redis-cli cluster keyslot "{evt:11111111-1111-1111-1111-111111111111}:ticket:A"
docker exec biletix-redis-c1 redis-cli cluster keyslot "{evt:11111111-1111-1111-1111-111111111111}:ticket:B"
#   -> identical slot numbers (only the {evt:...} substring is hashed)

# See real lock keys appear during a booking (scan one node):
docker exec biletix-redis-c1 redis-cli --scan --pattern "{evt:*"
```

## Failover / chaos test

```powershell
# Seed an event first (see repo README / Postman), then:
powershell -File scripts/redis-cluster-chaos.ps1 -EventId <guid>
```

It prints the topology, runs the oversell load while `docker stop biletix-redis-c1` kills a
master mid-flow, prints the topology again (a replica has been promoted), and verifies
**Booked ≤ stock** with **0 request errors** — overselling stays impossible across a Redis
failover because the DB CAS, not Redis, is the gate. The node is then restarted and rejoins as
a replica.

## Reset

```bash
cd build
docker compose down -v        # -v also drops the redis-cX-data volumes (fresh cluster next up)
```
