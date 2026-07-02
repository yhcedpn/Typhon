# Typhon — The Real-Time ACID Database Engine

**A microsecond-latency ACID database with a parallel, tick-based runtime built in — not bolted on next to it, running inside it.**

---

## Where Typhon Stands Today

Typhon is in **active development, targeting an alpha release** — not a finished 1.0. There's no NuGet package yet, no stable API, no support contract. What there is: a ~3,500-test NUnit suite, a durability model backed by TLA+ proofs and a crash-simulation sweep (not just tests), and a measured performance story (see [Measured, Not Just Claimed](#measured-not-just-claimed) below) that already beats or matches purpose-built rivals on their own turf.

If you're building a game server, simulation, or real-time stateful system and want to shape the engine while it's still soft — this is the moment.

---

## The Problem

Every game server, every simulation, every real-time system faces the same impossible choice:

**Option A: Use a real database.** Get transactions, crash recovery, and data integrity — but pay the price in latency. Network round-trips to PostgreSQL or Redis add milliseconds you don't have. A 60fps game tick is 16.6ms. A round-trip to your database just ate half of it.

**Option B: Roll your own.** Keep state in memory, serialize to disk when you can, pray nothing crashes between saves. You'll ship faster, but you'll also ship bugs: lost inventory, duplicated currency, corrupted world state. Every online game has war stories. Every player remembers.

**Option C: Use an embedded database.** SQLite, LevelDB, RocksDB — fast, local, no network. But none of them were designed for your data model. You'll spend months wrapping ECS components into relational tables, fighting impedance mismatch, and discovering that "embedded" doesn't mean "real-time." And none of them run your simulation loop for you — you still hand-roll a tick scheduler and a way to push state to clients on top.

There is no Option D. Until now.

---

## What Is Typhon?

Typhon is an **embedded ACID database engine with a game-server runtime built in** — it runs inside your process, speaks your data model natively, and delivers **microsecond-level operations** with full transactional guarantees.

It is not a general-purpose SQL database. It is not a key-value store with transactions bolted on. It is a purpose-built engine for systems where **every microsecond matters, data loss is unacceptable, and the simulation loop is a first-class concern, not an afterthought**.

```
┌─────────────────────────────────────────────────────┐
│                 Your Application                    │
│                                                     │
│    Game Server · Simulation · Trading Engine        │
│                                                     │
│  ┌─────────────────────────────────────────────────┐│
│  │               Typhon Engine                     ││
│  │                                                 ││
│  │  ECS Data Model · MVCC Snapshot Isolation       ││
│  │  DAG-Scheduled Tick Runtime · Spatial Index     ││
│  │  WAL v2 Durability · Rebuild-Based Recovery     ││
│  │  Zero-Copy Reads · Cache-Line-Aware Structures  ││
│  └─────────────────────────────────────────────────┘│
│               ▼               ▼                     │
│          Memory-Mapped Storage File                 │
└─────────────────────────────────────────────────────┘
             No network. No serialization.
                No separate process.
```

### The Core Idea

Your data lives as **components** — small, typed, blittable structs attached to **entities**, grouped by **archetype**. If you've ever used Unity DOTS, Bevy, or Flecs, this is your native language. If you haven't, think of it as a database where every "row" is an entity ID and every "column group" is a component type — but stored in cache-optimized columnar layout, not row-oriented tables, and with a scheduler that runs your game logic directly against that layout every tick.

```csharp
// A component is a plain struct; an archetype is the fixed set of components an entity has.
[Component("Game.Position", 1, StorageMode = StorageMode.SingleVersion)]
public struct Position { public float X, Y, Z; }

[Component("Game.Health", 1)]  // Default: Versioned (MVCC)
public struct Health { public int Current, Max; }

[Archetype(1)]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Health> Health = Register<Health>();
}

using var tx = dbe.CreateQuickTransaction();
var id = tx.Spawn<Unit>(
    Unit.Position.Set(new Position { X = 10, Y = 0, Z = 20 }),
    Unit.Health.Set(new Health { Current = 100, Max = 100 }));

tx.Commit();  // Atomic. Durable per the UoW's durability mode. Done.
```

No ORM. No mapping layer. No serialization step. Your struct *is* the storage format.

---

## Why Typhon Is a Game Changer

### 1. Microsecond Operations, Not Millisecond

Typhon doesn't just aim for "fast." It's engineered at the hardware level for predictable, microsecond-latency operations:

| Technique | What it does |
|-----------|-------------|
| **Memory-mapped I/O** | Pages live in your address space. Reads are pointer dereferences, not syscalls |
| **Blittable zero-copy components** | Your struct *is* the on-disk format. No serialize/deserialize step |
| **Entity Clusters (SoA, SIMD)** | Batched columnar storage per archetype with AVX-512/AVX2 predicate evaluation — turns per-entity hashmap lookups into sequential array scans |
| **Cache-line-aware 256-byte B+Tree nodes** | Tuned through multi-phase profiling for the common case: one cache-line-friendly node per traversal step |
| **Lock-free MVCC reads** | Readers never acquire locks. Period. Not even latches |
| **Optimistic Lock Coupling on every index** | B+Tree and R-Tree readers verify a version counter instead of latching; writers latch only the node(s) they touch |
| **Epoch-based resource protection** | 2 obligations per transaction, not one increment/decrement pair per page touched |
| **Hardware CRC32C checksums** | SSE4.2/ARM-intrinsic page integrity verification, ~1.3µs per 8 KiB page |

This isn't theoretical — it's measured. A durable commit under Group Commit / Deferred durability returns in **~1.7µs mean**. Bulk-iterating 100K entities through the Entity Cluster path drops per-entity cost from 134ns to **~2.7ns** (~50x) and shrinks the working set from 19.2 MB to 2.5 MB — the difference between spilling out of L3 and staying in L2.

### 2. Real ACID, Not "Pretty Close"

Typhon provides genuine ACID guarantees, not approximations:

- **Atomicity:** Transactions commit entirely or not at all. UnitOfWork boundaries make even groups of transactions atomic
- **Consistency:** Schema validation, typed components, constraint enforcement at commit time
- **Isolation:** MVCC snapshot isolation — every transaction sees a consistent point-in-time view. No dirty reads, no phantom reads, no read skew
- **Durability:** A logical Write-Ahead Log (WAL v2), an append-before-publish commit pipeline, CRC32C checksums on every page, and rebuild-based crash recovery

And the isolation model makes **deadlocks impossible by construction** — not detected-and-retried, but structurally impossible, on a three-pillar argument:

1. MVCC never holds locks between transactions — readers are snapshot-consistent, writers create new revisions without locking existing ones
2. B+Tree/R-Tree access is Optimistic Lock Coupling: optimistic readers never hold latches; pessimistic writers latch strictly top-down
3. Dual-node operations (index Move) order their two latches by ChunkId, ruling out AB/BA cycles

Deadlock detection is deliberately not implemented — there's nothing for it to detect.

### 3. Durability Is a Per-Component, Per-Transaction Choice

Not every write is equally important, and Typhon doesn't force one durability tier on your whole database. It's a **two-axis** choice:

**Per component type** — `StorageMode` picks the cost/durability point at registration time:

| Storage mode | Write cost (Zen 4) | Durability | Use it for |
|---|---|---|---|
| **Versioned** | ~150-360ns | Zero loss, full MVCC history | Inventory, economy, anything needing snapshot isolation or rollback |
| **SingleVersion** | ~3-10ns | ≤1 tick loss | Position, velocity, health — high-frequency, loss-tolerant |
| **Transient** | ~3-5ns | None — gone on crash | Animation state, pathfinding scratch, input buffers |
| **Committed** *(discipline on SingleVersion)* | ~25ns stage / ~60ns publish | Zero loss, atomic, no revision chain | Teleport, item pickup, currency debit — needs atomicity, not history |

**Per Unit of Work** — `DurabilityMode` picks when a batch of writes becomes crash-safe:

| Durability mode | Commit-return latency (measured) | Data at risk on crash | Use it for |
|---|---|---|---|
| **Deferred** | ~1.7µs mean | Until you explicitly flush | Bulk imports, tests, analytics |
| **Group Commit** | ~1.7µs mean | Up to one flush interval (default 5ms) | Game ticks, real-time simulation |
| **Immediate** | ~15-85µs (WAL record flushed with FUA before commit returns) | Zero after commit returns | Financial trades, audit-critical writes |

One entity can mix a `Versioned` wallet, a `SingleVersion` position, and a `Transient` animation cursor — same `Read`/`Write` API across all three, only the cost and guarantees differ. One database can run your player-authentication transaction on Immediate while your physics update runs on Group Commit, side by side.

### 4. Concurrent Indexing That Scales

Typhon's B+Tree and R-Tree indexes run **Optimistic Lock Coupling (OLC)** — the same technique research databases use for 256+ core scaling:

- **Readers are completely lock-free.** They read a version counter, traverse the node, verify the counter hasn't changed. No CAS, no latch, no contention
- **Writers latch only the nodes they modify** — typically 1-3 nodes for an insert, not the whole tree
- **Compound Move operations** fold a field update's remove+insert into one traversal with one lock on the common same-leaf case

Phase 3 of the OLC roadmap — fully latch-free, CAS-based leaf writes (inspired by the FB+-tree paper) — is designed but not yet built; today's writers still take a leaf latch. Everything above it is real.

### 5. An ECS-Native Data Model, With a GPU-Inspired Storage Layer

If your application already thinks in entities and components, Typhon speaks your language:

- **Compositional data.** Attach any combination of components to any entity. No rigid table schemas, no sparse-NULL columns
- **Cache-friendly bulk processing via Entity Clusters.** Eligible archetypes auto-pack up to 64 same-archetype entities into one contiguous SoA chunk. Profiling found 66% of naive per-entity iteration cost was pure lookup/fetch overhead, not computation — clustering eliminates it: 134ns → 2.7ns per entity, ~10x tick time, on a verified 100K-entity benchmark. Random single-entity access is unaffected — it transparently resolves through the same cluster
- **Secondary indexes with MVCC versioning.** Mark a field `[Index]`, and Typhon maintains a concurrent B+Tree with HEAD/TAIL buffers so index membership stays correct across updates and deletes, at your transaction's snapshot
- **Schema evolution without downtime.** Add fields, remove fields, widen types — Typhon migrates existing data automatically at startup, preserving chunk allocation so indexes never need a rebuild. Breaking changes get user-defined migration functions with automatic multi-step chain resolution

### 6. Spatial Indexing and Simulation Tiering, Built In

Most embedded databases stop at "here's your data." Typhon ships a full spatial subsystem, because "what's near this entity" and "how often should this region simulate" are first-class real-time-system questions:

- **A page-backed R-Tree** per `[SpatialIndex]` field, answering AABB, Radius, Ray, Frustum, kNN, and Count queries — sub-microsecond, zero heap allocation per query, OLC-concurrent with restart-not-block semantics under contention
- **An engine-wide spatial grid**, independent of the R-Tree, driving two things: **Spatially-Coherent Entity Clustering** (entities sharing a grid cell also share a storage cluster) and **Tiered Simulation Dispatch** (`SimTier` — process near entities every tick, far entities at reduced/amortized/dormant rates, with zero per-entity distance checks)
- **Cluster dormancy** — clusters untouched for N ticks sleep and are skipped by every dispatch path, waking within one tick of being written to
- **Checkerboard (Red/Black) dispatch** for systems that write across cell boundaries, still running as one parallel DAG node

### 7. A Tick-Based Runtime, Not Just a Store

This is the part most embedded databases don't have at all: Typhon includes `TyphonRuntime`, a DAG-scheduled, multi-threaded tick loop that owns your game/simulation loop and drives it directly against the ECS store — the runtime runs *inside* the database, not on top of it.

- **Five system types** — proactive callbacks, reactive per-entity queries (auto-skip when nothing relevant changed), chunk-parallel non-entity work, multi-stage pipelines, and sub-system grouping
- **Declarative, auto-derived parallelism.** Systems declare what components they read/write and their phase; the scheduler derives a safe execution DAG and rejects unsafe write/write or stale-read conflicts at build time — you never hand-write a dependency graph
- **Change-filtered dispatch.** A system can subscribe to "only entities that changed since I last ran," piggybacking on the same ring buffer that backs Persistent Views
- **Graceful overload response.** A single-writer overload state machine escalates through system throttling and tick-rate modulation (up to 6x) before ever calling your critical-overload callback to shed load — the server degrades, it doesn't fall over
- **Side-transactions.** A system can commit an economy-critical write durably mid-tick, independent of whether the tick's main transaction ever flushes
- **Validated at real scale, on Linux.** AntHill, Typhon's headless ECS/runtime stress harness, has been run successfully on a 96-core Linux machine — confirming correctness and healthy scaling of the entity-cluster/system/runtime dispatch path well beyond the single-CCD, 16-thread window the benchmark suite is pinned to

### 8. Built-In Client State Sync — No Networking Code Required

Typhon ships a Subscriptions server and a zero-engine-dependency `Typhon.Client` SDK: register a query as a **Published View**, and the runtime diffs it against every connected client's subscriptions each tick, pushing one MemoryPack-encoded delta over TCP per client, per tick.

- **Automatic incremental sync** — added/removed/modified entities, only the changed component bytes, computed from the same delta pipeline that backs Persistent Views
- **Backpressure handled for you.** A full client send buffer drops one tick's delta and triggers an automatic full-state resync — never an unbounded queue, never a memory leak
- **Priority throttling.** Critical Views always go out; Normal/Low Views throttle under overload
- **A real client SDK**, not just a wire format — `Typhon.Client` decodes deltas and maintains a per-View local entity cache your game client reads directly

If you've ever hand-rolled "diff the world state and push it to clients" for a multiplayer game, this is that system, built in and battle-tested against the same commit pipeline as everything else.

### 9. Production-Grade Crash Safety

```
Write Path:
  Transaction Commit
    → WAL v2 logical record — (EntityId, ComponentTypeId), not an 8KB page copy
    → Lock-free MPSC ring buffer (zero contention between writers)
    → VALIDATE → PREPARE → BUILD → APPEND → PUBLISH → WAIT
      (nothing is visible before its WAL record is appended; publish never rolls back)
    → Done.

Background:
  Checkpoint v2
    → Seqlock-consistent page snapshots (zero overhead on the write path)
    → CRC32C verification per page, A/B slot-pairing for pages that can't be re-derived
    → Advances CheckpointLSN only over pages actually written, recycles WAL segments

Crash Recovery (RecoveryDriver):
  1. Scan the WAL's durably-committed prefix
  2. Replay it idempotently, in strict LSN order, through the engine's own write primitives
  3. Discard and rebuild — never repair — every derived structure: indexes, EntityMap, occupancy
  4. Ready.
```

Typhon used to protect against torn pages with Full-Page Images (the PostgreSQL strategy). That mechanism was **retired in 2026** in favor of something simpler: derived structures (indexes, EntityMap, occupancy bitmaps) are never trusted after a crash — they're thrown away and rebuilt wholesale from the recovered primary data, which is itself protected by CRC32C plus A/B pairing on the handful of structural pages that can't be re-derived. Measured: **357ms to replay 100,000 hard-crash-interrupted commits with zero data loss.** Every protocol claim here is backed by invariant rules and TLA+ models in CI, not tests alone.

### 10. Backup & Restore — Landing Next, Not Yet Shipped

Point-in-Time incremental backup (forward-chained `.pack` files, scoped to changed pages, restoring through the same `RecoveryDriver` that handles crash recovery) is **designed and on the roadmap**, not implemented yet. If external backup/restore is a hard requirement today, Typhon isn't there — flagged here deliberately rather than left for you to discover after integrating.

### 11. Zero-Overhead Observability, and a Real Dev UI

Telemetry that costs nothing when you don't need it:

```csharp
// This field is static readonly — the JIT evaluates it once at startup.
if (TelemetryConfig.ProfilerActive)
{
    span = TyphonActivitySource.StartActivity("BTree.Insert");
}
// When ProfilerActive is false, the JIT eliminates the entire block.
// Not "branch not taken." Eliminated. The instructions don't exist.
```

~200 hierarchical, JSON/env-driven flags gate everything from Activity tracing to the embedded typed-event Profiler, with full OpenTelemetry export (traces, metrics, health checks) through one OTLP endpoint — Jaeger, Prometheus, Grafana, SigNoz, or anything else that speaks OTLP.

And unusually for an embedded engine: Typhon ships **Workbench**, a local browser-based dev UI — think *DataGrip for Typhon*, not just a profiler. Attach to a live engine over TCP or open a recorded trace: per-tick profiler timeline with CPU-sample and off-CPU overlays, a System DAG view, a zoomable Database File Map, a Schema Inspector, and a live Query Console.

---

## Who Is Typhon For?

### Game Servers

You're running a persistent online world. Thousands of entities with positions, health, inventory, quest state. You need to update 100K+ components per tick at 20-60Hz, never lose a player's inventory to a crash, query "all enemies within 50m with health below 50%" in microseconds, and push world-state deltas to every connected client without hand-rolling a sync protocol.

Typhon was built for exactly this. The ECS model *is* your data model. The tick runtime *is* your game loop. Spatial queries and simulation tiers are native, not bolted on. Subscriptions handle client sync. And when the server crashes at 3 AM, recovery replays 100,000 lost commits in well under half a second.

### Simulations & Digital Twins

Large-scale simulations with millions of entities, each with multiple state components. You need snapshot isolation so analysis threads see consistent state while simulation advances, incremental views that update reactively as entities change, and schema evolution as your model matures.

Typhon's MVCC gives you free consistent snapshots. Persistent Views with delta tracking make reactive queries practical at scale. (Point-in-time historical queries against the revision chains that already exist — "what was this entity's state 1000 ticks ago" — are designed but not built yet; today's history lives in the chain, it just isn't exposed as a query API.)

### Financial & Trading Systems

High-frequency data with strict durability requirements. You need low-latency transaction commit, immediate durability for trades and deferred for market data, and an embedded deployment with no external database dependency.

Typhon's mixed durability modes run both workloads in the same engine, side by side. Immediate mode holds the commit until its WAL record is flushed to disk with FUA — durability is real before the caller ever sees success, typically ~15-85µs. The logical WAL keeps record sizes small. The embedded model means no network hop.

### IoT & Edge Computing

Resource-constrained environments processing sensor data. You need an in-process database with a bounded memory footprint and configurable back-pressure instead of OOM crashes.

Typhon's resource system enforces memory budgets with graceful back-pressure today. If your deployment model depends on shipping incremental backups over a limited link, hold off — that path isn't built yet (see [§10](#10-backup--restore--landing-next-not-yet-shipped) above).

---

## Measured, Not Just Claimed

Every number below comes from [`test/Typhon.CompetitiveBenchmark`](test/Typhon.CompetitiveBenchmark), a benchmark suite in this repo (single CCD, ≤16 threads, matched durability/consistency guarantees per comparison) — not marketing math. Each competitor (SQLite, RocksDB, LMDB, FASTER, DuckDB) was configured and driven with the best fairness and API knowledge we had at the time; we're not claiming these are the most-optimized configuration possible for every rival, and if you know a fairer setup, the project is right there — open an issue or a PR.

**Against SQLite** (same durability guarantees, 1M rows): Typhon reads **1.75x** faster (18.5M vs 10.6M ops/s), updates **14x** faster (18.8M vs 1.3M ops/s), single-row point reads **48x** faster (17.7M vs 0.37M ops/s).

**Against DuckDB**, a purpose-built OLAP columnar engine — on its own game, a `SUM` aggregate over the Entity Cluster SoA layout: Typhon is **1.43x faster at 1 thread**, **1.37x faster at 8 threads** (both engines threading-controlled, 4M rows). A transactional, real-time-shaped engine outperforming a dedicated analytics engine on a columnar scan is not something the ECS model was designed to do — it's a side effect of storing data the way a GPU would.

**Crash recovery:** 357ms to replay 100,000 hard-crash-interrupted commits, zero loss.

**Honest losses, not hidden:** FASTER and LMDB win raw point-read throughput by doing far less per operation (no MVCC, no ACID) — FASTER hits 130.5M reads/s at 16 threads against Typhon's 18.5M. LMDB wins range scans outright and recovers in 5.5ms against Typhon's 357ms (LMDB has nothing to replay — no WAL, no revision chains, no consistency guarantee beyond "last write wins").

Across a 10-rival weighted scorecard (capability × niche-proximity — how close a rival's actual design center is to Typhon's real-time-embedded-ACID niche) spanning embedded engines and enterprise NewSQL/HTAP players (CockroachDB, SingleStore, TiDB, SAP HANA, VoltDB): no other engine scores high on both axes at once. The high-capability players (HANA, CockroachDB) aren't embedded or real-time-shaped; the embedded/real-time players (SQLite, LMDB, FASTER, DuckDB) top out around 60-63% capability. **That top-right quadrant is empty except for Typhon.**

---

## The Architecture at a Glance

```
┌────────────────────────────────────────────────────────────────┐
│                        Application                             │
├────────────────────────────────────────────────────────────────┤
│  DatabaseEngine                                                │
│  ├─ TyphonRuntime (DAG-scheduled tick loop, optional)          │
│  │  └─ Systems (5 types) → auto-derived parallel dispatch      │
│  ├─ UnitOfWork (durability boundary)                           │
│  │  └─ Transaction (MVCC snapshot)                             │
│  │     ├─ Spawn / Open / Read / Write / Destroy                │
│  │     └─ Query / View (index-driven, incrementally refreshed) │
│  ├─ ComponentTable (per-type storage + indexes)                │
│  │  ├─ Entity Clusters (SoA, SIMD-optimized bulk path)         │
│  │  ├─ B+Tree Secondary Indexes (OLC, versioned HEAD/TAIL)     │
│  │  └─ R-Tree Spatial Index + engine-wide Spatial Grid         │
│  ├─ Subscriptions (TCP delta streaming to Typhon.Client)       │
│  ├─ PagedMMF (memory-mapped, 8KB pages, clock-sweep cache)     │
│  ├─ WAL v2 (MPSC ring buffer → append-before-publish → replay) │
│  ├─ Checkpoint v2 (background flush, A/B slot-pairing)         │
│  ├─ Resource System (budgets, back-pressure, health checks)    │
│  └─ Observability (OTLP traces + metrics, zero-overhead)       │
├────────────────────────────────────────────────────────────────┤
│  Concurrency Primitives                                        │
│  ├─ AccessControl / AccessControlSmall (atomic RW locks)       │
│  ├─ Epoch-Based Resource Protection                            │
│  ├─ OLC latches (optimistic lock coupling, B+Tree & R-Tree)    │
│  └─ AdaptiveWaiter (spin → yield → sleep)                      │
├────────────────────────────────────────────────────────────────┤
│  Storage File (memory-mapped, CRC32C verified)                 │
└────────────────────────────────────────────────────────────────┘
```

---

## What Makes Typhon Different — A Comparison

|  | Traditional DB (PostgreSQL) | Embedded KV (RocksDB) | In-Memory (Redis) | **Typhon** |
|--|---|---|---|---|
| **Deployment** | Separate server | Library | Separate server | **Library (in-process)** |
| **Latency** | Milliseconds (network) | Microseconds (disk) | Microseconds (network) | **Microseconds (memory-mapped)** |
| **ACID** | Full | Limited | None | **Full** |
| **Data Model** | Relational | Key-Value | Key-Value | **ECS (Entity-Component)** |
| **Runtime included** | No | No | No | **Yes — tick-scheduled, parallel** |
| **Client sync** | Build it yourself | Build it yourself | Build it yourself | **Built in (Subscriptions)** |
| **Concurrency** | Lock-based + MVCC | Single-writer | Single-threaded | **MVCC + OLC (lock-free reads)** |
| **Durability** | One mode | Configurable | Optional | **Per-component + per-UoW configurable** |
| **Crash Recovery** | WAL replay | WAL replay | AOF/RDB | **WAL replay + rebuild-derived-structures** |
| **Schema Evolution** | ALTER TABLE | N/A | N/A | **Automatic migration** |
| **Spatial queries** | Extension (PostGIS) | No | No | **Native (R-Tree + grid)** |
| **Deadlocks** | Detected & retried | N/A | N/A | **Impossible by construction** |

---

## What's Built Today

Every item below is ✅ **Implemented** and cross-linked to source and tests in the [Feature Catalog](doc/feature-set/README.md) — this list deliberately excludes anything still 🚧 Partial or 📋 Planned; see the [Roadmap](#beyond-today-the-roadmap) for those.

**Core Engine** — ECS data model with blittable component storage · MVCC snapshot isolation with optimistic conflict detection · Entity/archetype lifecycle with source-generated zero-copy accessors · Schema versioning with automatic and user-defined migration

**Storage Modes** — Versioned, SingleVersion, Transient, and the Committed durability discipline — mixed freely per entity

**Entity Clusters** — SoA batched storage, SIMD predicate evaluation, auto-eligibility, ~50x measured per-entity speedup on bulk iteration

**Indexing** — Four key-width-specialized B+Tree variants, Optimistic Lock Coupling, versioned HEAD/TAIL secondary indexes, compound Move operations

**Spatial** — Page-backed R-Tree (AABB/Radius/Ray/Frustum/kNN/Count), engine-wide spatial grid, spatially-coherent clustering, tiered simulation dispatch, trigger volumes, category filtering

**Query System** — Three-tier constraint evaluation, index-first execution planning, FK navigation joins, OR/DNF predicates, ordering & pagination, incrementally-refreshed Persistent Views (indexed predicates only, today)

**Runtime** — DAG-scheduled tick loop, five system types, declarative access-based auto-parallelism, change-filtered reactive dispatch, spatial-tier adaptive dispatch with cluster dormancy, checkerboard Red/Black dispatch, side-transactions for mid-tick immediate durability

**Subscriptions** — Published Views (shared and per-client), diff-based subscription management, per-tick delta computation, TCP wire transport with MemoryPack, incremental sync, backpressure-driven resync, priority throttling, a standalone `Typhon.Client` SDK

**Durability & Recovery** — Logical WAL v2, append-before-publish commit pipeline, three per-UoW durability modes, Checkpoint v2 with A/B slot-pairing, rebuild-based crash recovery (RecoveryDriver), CRC32C page checksums with seqlock snapshots — every protocol claim backed by invariant rules and TLA+ models

**Storage Engine** — Memory-mapped 8KB pages, clock-sweep eviction, epoch-based resource protection, resource budgets with graceful back-pressure

**Observability & Tooling** — OpenTelemetry traces/metrics/health checks, zero-overhead JIT-eliminated telemetry gating, an embedded zero-allocation event profiler with CPU-sampling and off-CPU thread-scheduling capture, the Workbench dev UI, the `tsh` interactive shell

---

## Where Typhon Isn't (Yet) the Best Fit

In the spirit of not making you find these out the hard way:

- **No stable API, no NuGet package, no support contract.** Pre-alpha. If you need a vendor to call, this isn't there yet
- **No backup/restore.** Point-in-Time backup is designed, not built — if that's a hard requirement today, wait or plan around it
- **Not a general-purpose analytics warehouse.** The DuckDB result above is a real, verified win on one aggregate shape over Entity Cluster data — it isn't a claim that Typhon replaces a columnar OLAP engine for arbitrary ad-hoc analytics
- **High-thread-count zero-loss commits don't group-commit well yet.** At 16 concurrent threads on the `durable@return` path, throughput reaches only ~2.4x the single-thread rate (a well-tuned group-commit design would approach ~16x). This isn't an architectural ceiling — the WAL-writer handoff and flush batching simply haven't been optimized yet, and the fix path is already understood. It also doesn't affect the common case: the deferred/durable-soon path (~1.7µs, unaffected by thread count) is what most workloads, including game ticks, should reach for

---

## Beyond Today: The Roadmap

The four items below are the highlights — for the full, current backlog and priorities, see the [GitHub Project](https://github.com/orgs/Log2n-io/projects/1/views/1).

**Point-in-Time Incremental Backup** — forward-chained `.pack` files scoped to changed pages, restoring through the same crash-recovery driver. Designed, not built. The most concrete near-term gap.

**Temporal Queries** — `ReadEntityAtVersion(entityId, timestamp)` and `GetRevisionHistory(entityId)` as a thin API layer over the revision chains Typhon already maintains for every Versioned component. Design exists; no code yet.

**Latch-Free Leaf Updates** — Phase 3 of the OLC roadmap: CAS-based B+Tree leaf modifications (FB+-tree-inspired), removing the last write latch from the index path. Phases 1-2 (OLC reads, Compound Move) are live; this one is deferred.

**Async-Aware UnitOfWork** — `async/await` at the API boundary while engine internals stay synchronous. Currently an early idea, not yet promoted to a design doc.

---

## The Bottom Line

Typhon exists because the real-time systems being built today deserve better than databases designed for a world without them — and because "run your simulation loop" and "sync state to clients" shouldn't be problems you solve *around* your database instead of *with* it.

Game servers shouldn't have to choose between "fast" and "correct." Trading engines shouldn't need a separate PostgreSQL instance for the 5% of writes that need durability. Simulations shouldn't sacrifice transactional consistency to hit their tick rate. And none of them should have to hand-roll a tick scheduler and a client-sync protocol on top of whatever storage engine they picked.

**Typhon is a microsecond-latency ACID database with a parallel tick-based runtime and client-state sync built in — running in your process, speaking your data model, never making you choose between performance and correctness.**

It's pre-alpha and not finished. But the parts that are built are measured, proven against TLA+ models where it matters most, and already beating purpose-built rivals on their own turf.

It's also **source-available**, not open source: every version before 1.0 — which is all of them, right now — is free for any use, any organization size, any purpose. Read it, fork it, ship it. From 1.0 onward it stays free for organizations under $2M annual revenue and 50 people; past that threshold, or if you want to build a competing database product or service on top of it, you need a commercial license. Full terms in [LICENSE.md](LICENSE.md).

*What would a database look like if it were designed for real-time systems, ECS data models, and the runtime that drives them — all from the ground up, together? This is our answer.*

---

*Typhon is source-available (see [LICENSE.md](LICENSE.md)) — free for any use pre-1.0, and pre-1.0 is everything that exists today. Written in C#/.NET 10. CI-tested on Windows and Linux.*
*[GitHub](https://github.com/log2n-io/Typhon) · [User Guide](doc/guide/README.md) · [Feature Catalog](doc/feature-set/README.md) · [In-Depth Overview](doc/in-depth-overview/README.md)*
