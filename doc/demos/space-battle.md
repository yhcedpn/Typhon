---
uid: demo-space-battle
title: SpaceBattle
description: Headless .NET 10 real-time combat benchmark demonstrating Typhon ECS, runtime scheduling, spatial clustering, tick-fence persistence, recovery, and asynchronous observability.
---

# SpaceBattle

SpaceBattle is a headless .NET 10 console demo implemented by [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Program.cs) and [`SpaceBattleHost.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleHost.cs) for exercising Typhon under a sustained real-time workload. It creates a deterministic fleet in a bounded three-dimensional world, advances the battle at a fixed 25 TPS cadence, and persists the state of each completed simulation tick.

The demo has no GUI, web server, command prompt, or gameplay input. Its purpose is to make Typhon's ECS, runtime DAG, spatial maintenance, durability boundary, recovery behavior, and telemetry visible in one reproducible workload.

## Quick start

Install the .NET 10 SDK, clone the repository, and run the demo project from the repository root:

```powershell
dotnet run --project demo/SpaceBattle/SpaceBattle.csproj --configuration Release
```

The default run creates 50,000 ships. The first run performs a bulk load before starting the runtime, so initialization can take a while and is reported separately from tick performance. At the 25 TPS target, the 45,000-tick convergence limit represents up to 30 minutes of simulation time; a battle normally terminates earlier when one or zero ships remain.

The executable stores its database at `Path.Combine(AppContext.BaseDirectory, "default.typhon")`. For the command above this is normally under `demo/SpaceBattle/bin/Release/net10.0/`, not the repository root. Typhon may create a directory and companion files for that database path.

The default executable has no command-line configuration surface. To start another independent run, change the `RunName` constant in [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Program.cs), rebuild, and run again. Keep each run name mapped to its own database; never reuse a completed or timed-out database for a new battle.

### Small, fast verification

The default executable is intentionally a production-scale workload. Use the companion tests for a small run instead of changing production constants just to smoke-test the project:

```powershell
dotnet test demo/SpaceBattle.Tests/SpaceBattle.Tests.csproj --configuration Release --filter 'FullyQualifiedName~SpaceBattle.Tests.InitialWorldTests'
```

To run the complete SpaceBattle test suite without building the whole solution:

```powershell
dotnet test demo/SpaceBattle.Tests/SpaceBattle.Tests.csproj --configuration Release
```

The tests create small worlds in temporary directories and cover initialization, deterministic state, fixed-tick progression, target locks, combat, terminal outcomes, asynchronous observations, pause/recovery, and recovery validation.

## Starting, pausing, and resuming

### Start or resume

Run the same command each time. The startup and resume decisions are implemented by [`SpaceBattleHost.Start`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleHost.cs):

```powershell
dotnet run --project demo/SpaceBattle/SpaceBattle.csproj --configuration Release
```

Startup behavior is determined by the database state:

| Database state | Behavior |
|---|---|
| Path does not exist | Create one `SimulationRun`, bulk-load the ships, checkpoint the initial world, then start the runtime. |
| Existing run with `Running` status | Validate the run identity and state, restore a pause checkpoint when it matches the current completed tick, increment the process segment, and continue from the last completed tick. |
| Existing run with `Completed` or `TimedOut` status | Reject startup. The historical run is not overwritten. |
| Existing path with zero or multiple `SimulationRun` entities | Reject startup. An ambiguous or half-created database is not silently repaired. |

The console prints `恢复运行 default...` when an incomplete run is resumed. The `ProcessSegment` value increases for every process lifetime, so observations from separate starts can be distinguished.

### Safe pause with Ctrl+C

Press `Ctrl+C` once. [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Program.cs) cancels the host token, and [`SpaceBattleSimulation`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs) treats cancellation as a pause request:

1. The current tick is allowed to finish.
2. The tick fence persists the completed tick's ordinary combat state.
3. The pause checkpoint captures the run, ships, equipment flags, and target locks with immediate durability.
4. The process reports `已在完成当前模拟 tick 后暂停；下次启动将继续运行。` and exits successfully.

The pause boundary is therefore a completed tick, not an arbitrary instruction boundary. [`SpaceBattleCheckpoint`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleCheckpoint.cs) validates the checkpoint before applying it and resumes at exactly the persisted `CompletedTicks` value. Derived runtime state such as views, in-memory event queues, counters, and histograms is rebuilt for the new process segment.

If the current tick cannot finish within the host's five-second pause wait, the program fails rather than claiming that a safe pause occurred.

### Terminal outcomes

The `Output` system in [`SpaceBattleSimulation.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs) evaluates terminal state after each completed tick:

| Outcome | Condition | Console/process result |
|---|---|---|
| `Winner` | Exactly one living ship remains. | Prints the winner's entity key; exit code `0`. |
| `Draw` | All remaining ships are destroyed in the same resolved tick. | Prints a draw; exit code `0`. |
| `TimedOut` | More than one ship remains at `MaximumCompletedTicks` (`45,000` by default). | Prints the remaining population; exit code `1`. |

An existing terminal database cannot be resumed. This protects completed benchmark history from being silently changed. A seed/ruleset mismatch, malformed state, dangling lock, illegal equipment combination, invalid coordinate, or other recovery violation throws during startup and produces a process failure rather than continuing with an altered simulation.

## Default run definition

The defaults live in [`SimulationDefinition.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SimulationDefinition.cs) and [`ProductionSettings.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/ProductionSettings.cs); database registration and spatial-grid setup live in [`SpaceBattleDatabase.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleDatabase.cs):

| Setting | Default | Meaning |
|---|---:|---|
| Run name | `default` | Stable name used by the console program to select `default.typhon`. |
| Ships | `50,000` | Initial number of `Ship` entities. |
| Seed | `0x5350414345424154` | Fixed `ulong` seed; its bytes spell `SPACEBAT`. |
| Ruleset version | `1` | Stored in `SimulationRun` and checked on resume. |
| World | `0 ≤ X,Y,Z ≤ 1000` | Closed, bounded cube; ships are zero-volume points. |
| Maximum health | `1,000` | Initial health for every ship. |
| Tick rate | `25 TPS` | Wall-clock target, with a 40 ms budget per tick. |
| Simulation delta | `0.04 s` | Fixed logical movement step; an overrun never enlarges it. |
| Staging | `250 ticks` | Ships remain stationary for the first 10 seconds of simulation time. |
| Maximum ticks | `45,000` | Non-convergence guard; a run becomes `TimedOut`. |
| Spatial grid | XY, cell size `100` | Cluster broadphase and spatial maintenance; combat distances remain 3D. |
| Spatial margin | `20` | Fat-AABB margin for the dynamic spatial index. |
| Page cache | `512 MiB` | Fixed production page-cache envelope. |
| Typhon memory envelope | `1 GiB` | Fixed production resource budget. |
| Workers | `max(1, logical processors - 4)` | Automatic runtime worker count. |
| Damage queues | `65,536` per worker | One typed `DamageIntent` queue per worker. |
| Minimum overload rate | `25 Hz` | Overload modulation cannot hide a missed 25 TPS budget. |
| Queue-growth escalation | Disabled | Queue bursts do not change dispatch policy for this benchmark. |
| Deep profiling | Disabled | Baseline runs avoid trace capture overhead. |

The seed and ruleset are part of the persisted run identity. Changing either value while pointing at an existing database is rejected; use a new run name/database for a new definition.

## What the console output means

A fresh run normally prints lines like these (the values vary by machine and run):

```text
初始化完成：50,000 艘飞船，耗时 12.34 秒。
运行资源：逻辑处理器 16，Typhon worker 12，page cache 512 MiB，内存封套 1024 MiB，overload Normal。
模拟运行中。按 Ctrl+C 安全暂停。
战况：segment 1，tick 25，存活 50,000，锁定 0，射击 0，命中 0，死亡 0；tick p50/p95/p99 1.20/2.40/3.10 ms，超预算 0 次，状态 Running。
资源：segment 1，tick 125，节点 123，最高容量利用率 42.0%（...）。
```

### Initialization and runtime lines

- `初始化完成` is emitted only for a new database. It measures bulk-load initialization separately from simulation ticks.
- `运行资源` records the logical processor count, actual Typhon worker count, page-cache size, memory envelope, and the current overload level. These values are useful context when comparing machines.
- `模拟运行中` confirms that `Ctrl+C` is handled as a safe pause request.

### Battle lines

[`SpaceBattleObservationPublisher`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleObservationPublisher.cs) emits one `SpaceBattleLogSnapshot` every 25 completed ticks (approximately once per second), and emits one additional terminal snapshot even when the terminal tick is not on that cadence. The record types are defined in [`Observations.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Observations.cs).

| Field | Meaning |
|---|---|
| `segment` | Process segment. The performance histogram and in-memory counters start a new segment after a restart. |
| `tick` | Last fully completed simulation tick represented by the line. |
| `存活` / `AliveShipCount` | Authoritative living `Ship` entity count. Death removes the entity; `Health = 0` is not a surviving state. |
| `锁定` / `ActiveLockCount` | Current `TargetLock` entity count, including acquisition and releasing locks. |
| `射击` / `ShotsFired` | Cumulative combat fire evaluations in the current process segment. The current implementation increments this counter while an eligible weapon/target pair is evaluated, including cooldown and out-of-range evaluations; use `Hits` for actual damage-intent-producing evaluations. |
| `命中` / `Hits` | Cumulative in-range fire attempts that produced a damage intent. |
| `死亡` / `Deaths` | Cumulative destroyed ships in the current process segment. |
| `状态` | `Running`, `Completed`, or `TimedOut`. |

The mode counts in the public snapshot are mutually exclusive. Their sum must equal the living ship count:

`Staging + Wandering + Tracking + Combat + Disengaging + Escaping = AliveShipCount`

The performance part of `SpaceBattleLogSnapshot` contains `SampleCount`, p50/p95/p99 actual duration, maximum actual duration, overrun count, maximum overrun ratio, and the last actual/target duration and ratio. The default console line prints the p50/p95/p99 values and overrun count; a custom `ISpaceBattleObservationSink` can consume the complete record.

The percentiles are calculated from a rolling duration histogram for the current process segment. They are representative bucket values, not a promise of identical numbers across hardware. The first 250 staging ticks remain in the histogram; initialization time is not a tick and is not included.

### Resource lines

The observation publisher emits a `SpaceBattleResourceSnapshot` every 125 completed ticks (approximately every five wall-clock seconds when the 25 TPS target is maintained). The default console line reports:

- the process segment and completed tick;
- the number of Typhon resource-graph nodes;
- the node with the highest capacity utilization and its path.

The snapshot object retains the complete Typhon `ResourceSnapshot`, including the other resource dimensions available from the engine. Resource output is asynchronous and can arrive after the tick that produced it; it is diagnostic output, not a synchronization point.

### Performance interpretation

Compare actual tick duration with the fixed 40 ms target:

- p95 or p99 below 40 ms indicates that most sampled ticks met the target on that machine;
- any overrun count means at least one tick exceeded its target duration;
- a large maximum or high maximum overrun ratio identifies tail latency even when p50 is low;
- `TimedOut` means the simulation did not converge before its logical tick limit, not that a tick overrun occurred;
- initialization, JIT warm-up, storage setup, console rendering, and resource sampling should be discussed separately from tick duration.

Tick overruns are reported, not converted into a logical time skip. Every late tick still advances exactly 0.04 seconds of simulation time.

## Simulation model

### Typhon data model

The ECS shapes are declared in [`Archetypes.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Archetypes.cs) and the persisted fields in [`Components.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Components.cs). The world has three archetypes:

| Archetype | Role |
|---|---|
| `SimulationRunEntity` | Exactly one persisted run identity and terminal state. |
| `Ship` | One entity per ship, using `EntityId` as its domain identity. |
| `TargetLock` | One entity per lock attempt, independently addressable by owner and target. |

The `Ship` archetype stores position, indexed spatial bounds, motion, health, behavior, tracking, and pause-checkpoint data. Weapon and afterburner are component enable bits, so they can be enabled or disabled without changing the archetype shape.

The engine-wide spatial grid is configured in [`SpaceBattleDatabase.Open`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleDatabase.cs) and covers XY only because Typhon uses it as a coarse cluster broadphase. Each ship still owns a point-shaped `AABB3F`, movement includes Z, lock range uses 3D Euclidean distance, and weapon range uses 3D Euclidean distance. The grid must not be interpreted as a 2D combat rule.

Target acquisition, implemented by [`TargetingSystem`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs) and [`BehaviorRules.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/BehaviorRules.cs), intentionally does not materialize a large spatial query result for every ship. It samples at most 64 candidates from the deterministic living roster and then applies the exact 3D distance check. The sampling depends on seed, ship ID, decision ordinal, and purpose, not on R-Tree enumeration order.

### Behavior modes

The behavior constants and deterministic transitions are implemented in [`BehaviorRules.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/BehaviorRules.cs) and [`SpaceBattleSimulation.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs). Every living ship has exactly one behavior mode:

| Mode | Behavior |
|---|---|
| `Staging` | Initial stationary period. Direction starts at `(+1, 0, 0)` and speed at `0`. |
| `Wandering` | Moves with a deterministic unit-sphere direction and speed sampled uniformly from `[0, 37.5]`; reevaluates every 250 ticks. |
| `Tracking` | Pursues any other living ship at speed `50`, recalculating direction every tick, for at most 250 ticks. |
| `Combat` | Moves at speed `45`, retries deterministic target acquisition, and returns to wandering if acquisition does not succeed within its combat window. |
| `Disengaging` | A ship that participated in a kill glides for 75 ticks at speed `25` with weapon and afterburner disabled. |
| `Escaping` | A ship that receives positive damage flees at speed `75` for 125 quiet ticks with afterburner enabled and weapon disabled. New damage resets the quiet window. |

The transition that ends a duration is applied at a tick boundary. A trigger tick is not counted as one of the future complete duration intervals. Escape takes priority over disengaging when a ship is both damaged and involved in a kill.

### Target locks and weapons

Combat target selection and lock lifecycle are separate from behavior mode; the lock entity schema is in [`Components.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Components.cs), while lifecycle and weapon authorization are in [`SpaceBattleSimulation.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs):

1. A combat ship samples up to 64 other ships and accepts a candidate within 3D lock range `≤ 300`.
2. The `TargetLock` entity enters `Acquiring` for 50 complete ticks. Acquisition fails immediately if the target dies or leaves range.
3. A completed acquisition becomes `Locked`, enables the owner's weapon, and allows an immediate first fire attempt.
4. A weapon attempts to fire every 50 ticks. Each in-range attempt adds 200 damage; an out-of-range attempt consumes the normal cooldown but adds no damage.
5. A voluntary cancellation enters `Releasing` for 25 ticks and disables the weapon immediately. Death or range invalidation destroys the lock immediately.

At present each ship can occupy one lock slot. Acquisition and releasing locks count against that slot, and owner/target fields are indexed for cleanup and reverse lookup.

### Tick order and simultaneous damage

The public `SpaceBattle` DAG declared in [`SpaceBattleSimulation.ConfigureRuntime`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs) executes the following ordered stages:

```text
State
  → Steering
  → Movement
  → TargetLockCleanup
  → Targeting
  → Combat
  → DamageResolution
  → Resolution
  → Output
  → Typhon tick fence
```

`Combat` is a parallel `QuerySystem`; [`CombatRules.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/CombatRules.cs) defines fire and damage constants. Each worker writes `DamageIntent { Attacker, Target }` to its own typed queue. `DamageResolution` drains all queues, sorts intents by target and attacker, groups them by target, and applies the complete same-tick damage set before destroying dead entities. Therefore:

- movement happens before firing and all range checks use post-movement positions;
- a ship whose death is resolved in this tick still contributes any fire attempt already due in this tick;
- all valid same-tick hits are counted, including overkill hits;
- a surviving damaged ship enters `Escaping`;
- an attacker participating in a kill enters `Disengaging` unless it was itself damaged;
- entity existence, rather than a zero health value, is the authoritative death fact.

The demo uses Typhon's runtime DAG, access declarations, parallel query dispatch, and per-worker typed event queues. It does not create an application-level thread pool or a second tick scheduler.

## Persistence and recovery

### Authoritative versus derived state

The persistence choices are declared in [`Components.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Components.cs) and the tick-fence/startup behavior is implemented in [`SpaceBattleSimulation.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs). The following data is authoritative and persisted:

| Data | Storage/durability contract |
|---|---|
| Seed, ruleset, initial population, completed tick, alive population | Versioned `SimulationRunComponent`; the single run entity is the run identity. |
| Process segment, status, outcome, winner | Versioned `SimulationRunStateComponent`; terminal transitions use immediate durability. |
| Ship position, bounds, motion, health, behavior, tracking, cooldown, equipment state | `SingleVersion` components; ordinary changes are written at the Typhon tick fence. |
| Target-lock data and lock entity lifecycle | Lock data uses `SingleVersion`; entity creation/destruction participates in the tick's transaction and fence. |
| Explicit pause checkpoint | Versioned checkpoint components written with immediate durability after the completed tick. |

The following are derived or process-local and are rebuilt rather than treated as the source of truth:

- the `EcsView<Ship>` roster used by the runtime;
- worker-local `DamageIntent` queues and pending damage events;
- per-segment shots, hits, deaths, and tick-duration histograms;
- the asynchronous observation channel and resource snapshots;
- spatial grid occupancy and other Typhon-maintained derived structures.

This separation is why a crash or restart resumes from the last complete tick rather than from an arbitrary in-memory phase. Ordinary hits and deaths do not perform an `Immediate` flush per hit; the benchmark measures batched tick-fence persistence instead of turning every shot into an `fsync` benchmark.

### Resume validation

Before continuing an existing run, [`SpaceBattleRecoveryValidation.ValidateCurrent`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleRecoveryValidation.cs) validates:

- there is exactly one `SimulationRunEntity`;
- the persisted seed and ruleset version match the current definition;
- the alive count matches the authoritative ship entities and does not exceed the initial count;
- every position and point bound is finite and inside the world;
- every motion direction is normalized and every speed is valid;
- behavior modes and their timers are valid;
- tracking references point to a living ship;
- weapon and afterburner are not enabled together and are enabled only in valid modes;
- every lock has living, distinct endpoints, a valid status/timer, and an owner within the lock limit;
- a locked weapon has the corresponding locked target;
- terminal state fields agree with the population;
- the pause-checkpoint envelope is valid.

If validation fails, startup throws and leaves the database untouched. Fix the definition or recover using the matching binary/ruleset; do not delete the database as an automatic workaround.

### Tick fence versus pause checkpoint

The normal crash-recovery boundary is the last completed tick fence, as recorded by [ADR-0001](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0001-persist-combat-state-at-tick-fence.md). The explicit pause checkpoint is an additional application-level snapshot written by [`SpaceBattleCheckpoint.Persist`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleCheckpoint.cs) after a graceful `Ctrl+C` pause. It includes enough ship, equipment, run, and lock data to validate and restore the paused world exactly.

The distinction follows the demo's persistence decisions in [ADR-0001](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0001-persist-combat-state-at-tick-fence.md), [ADR-0002](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0002-model-target-locks-as-entities.md), and [ADR-0008](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0008-keep-simulation-run-start-state-versioned.md):

- **Tick fence:** normal batched persistence boundary for high-frequency `SingleVersion` state.
- **Pause checkpoint:** immediate, validated snapshot used after an intentional safe pause.
- **Immediate durability:** reserved for run identity/terminal transitions and the pause checkpoint; ordinary combat does not use it per hit.

## Profiling and deeper analysis

Deep profiling is disabled by default in [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Program.cs) so a baseline run does not pay trace-capture cost. To enable it, set `EnableDeepProfiling` to `true`, rebuild, and run the same command:

```powershell
dotnet run --project demo/SpaceBattle/SpaceBattle.csproj --configuration Release
```

The program enables Typhon's profiler before engine telemetry is initialized and writes the trace beside the database with the `.typhon-trace` extension. For the default Release run, the expected paths are approximately:

```text
demo/SpaceBattle/bin/Release/net10.0/default.typhon
demo/SpaceBattle/bin/Release/net10.0/default.typhon-trace
```

Open the trace with the [Typhon Workbench](../tools/workbench/index.md). Use the runtime DAG and tick spans to separate system execution, worker scheduling, tick-fence work, WAL/checkpoint activity, and resource pressure. Use different run names/database files for profiled and baseline runs, or preserve the trace before starting another run.

## Source and tests

The public entry points and the implementation areas are:

- [`Program.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Program.cs) — console lifecycle, output, exit codes, run name, and profiling switch.
- [`SpaceBattleHost.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleHost.cs) — database creation, resume checks, initial bulk load, and snapshots.
- [`SpaceBattleSimulation.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleSimulation.cs) — runtime DAG, pause/terminal control, systems, tick order, and combat resolution.
- [`SimulationDefinition.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SimulationDefinition.cs) — deterministic simulation constants and default workload.
- [`Components.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Components.cs) and [`Archetypes.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Archetypes.cs) — persisted schema and ECS shapes.
- [`SpaceBattleObservationPublisher.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleObservationPublisher.cs) and [`Observations.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/Observations.cs) — asynchronous observations and public snapshot records.
- [`SpaceBattleCheckpoint.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleCheckpoint.cs) and [`SpaceBattleRecoveryValidation.cs`](https://github.com/yhcedpn/Typhon/blob/main/demo/SpaceBattle/SpaceBattleRecoveryValidation.cs) — pause persistence, restore, and recovery invariants.
- [`demo/SpaceBattle.Tests`](https://github.com/yhcedpn/Typhon/tree/main/demo/SpaceBattle.Tests) — small deterministic, lifecycle, combat, observability, pause, and recovery tests.

The Typhon concepts behind the demo are documented in the [ECS overview](../in-depth-overview/06-ecs.md), [spatial overview](../in-depth-overview/07-spatial.md), [runtime overview](../in-depth-overview/10-runtime.md), [durability overview](../in-depth-overview/11-durability.md), and [observability overview](../in-depth-overview/12-observability.md). The SpaceBattle-specific design decisions are recorded in [ADR-0001](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0001-persist-combat-state-at-tick-fence.md), [ADR-0002](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0002-model-target-locks-as-entities.md), [ADR-0004](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0004-move-before-simultaneous-fire.md), [ADR-0005](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0005-use-runtime-scheduling-and-per-worker-event-queues.md), [ADR-0006](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0006-configure-spatial-grid-for-clustered-ships.md), [ADR-0007](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0007-select-lock-targets-by-deterministic-roster-sampling.md), and [ADR-0008](https://github.com/yhcedpn/Typhon/blob/main/docs/adr/0008-keep-simulation-run-start-state-versioned.md).
