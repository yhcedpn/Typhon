# SpaceBattle

SpaceBattle 是一个使用 Typhon ECS/runtime 的确定性空间战斗 demo。它以 50,000 艘飞船为默认规模，固定 25 Hz 逻辑帧，展示从 bootstrap、并行系统、空间目标获取、战斗结算到持久化和可观测性的完整闭环。

本文是可操作说明；状态机、数据模型和系统访问不变量见同目录的 [DESIGN.md](DESIGN.md)。不需要先阅读源码即可运行 demo、读取结果或执行测试。

## 运行环境与目录

- .NET SDK 10（项目使用 C# 14）。
- 当前目录是仓库根目录时，项目入口是 `demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj`。
- 程序没有命令行参数。默认配置来自 `SimulationDefinition.Default`：50,000 艘、1,000 × 1,000 × 400 世界、25 Hz、每帧 0.04 s、最大 22,500 tick；如果先出现 draw/winner，则会提前结束。
- 数据库根目录为程序输出目录下的 `data/`，实际数据库是 `data/space-battle.typhon`。每次新运行只删除并重建这个 SpaceBattle 数据库，不会删除同级其他文件。
- 默认 profiler/trace 关闭。`trace=` 仍会打印“配置解析后的候选路径”，不代表已经产生了 trace 文件。

## 运行 demo

首次使用或依赖尚未还原时，先只还原 SpaceBattle 入口项目（不会还原整个解决方案）：

```powershell
dotnet restore demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj
```

之后可以只操作 SpaceBattle 项目：

```powershell
dotnet run --project demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj -c Release --no-restore
```

程序会先输出初始化完成行，然后在 tick 1、126、251、376……输出一条稳定的 `key=value` telemetry 行（采样使用 zero-based tick 0、125、250、375……）。最后输出一行类似：

```text
termination=tick_limit bootstrap_ships=50000 completed_ticks=22500 remaining_ships=... bootstrap_ms=... database=... trace=...
```

`bootstrap_ms` 是批量 spawn 和首个 fence 的初始化成本，不计入 tick 百分位。正常退出的非 fatal 结果 exit code 为 0；fatal 结果为 1，并在 stderr 继续打印 `fatal_system`、`fatal_exception`。

### 如何解释输出

每条采样 telemetry 都是 invariant-culture 的 `key=value` 文本，字段含义如下：

| 字段 | 含义 |
| --- | --- |
| `tick` | 对用户显示的 1-based 逻辑帧编号；程序内部/测试的 `TickNumber` 是 zero-based。 |
| `alive` | Publish 后仍有生命值的飞船数。 |
| `next_wandering`、`next_tracking`、`next_approaching`、`next_attacking`、`next_turning` | 本帧 Behavior 完成后、下一帧预计处于五种模式的数量；五项之和应等于 `alive`。 |
| `valid_locks` | Movement 后仍满足目标存活且距离不超过 200 的锁定数。 |
| `tick_p50_ms`、`tick_p95_ms`、`tick_p99_ms`、`tick_max_ms` | 已记录 tick 的墙钟耗时百分位；bootstrap 不在其中。 |
| `tick_over_40ms` | 严格大于 40 ms 的 tick 数；等于 40 ms 不计入。它是诊断计数，不是 CI 失败条件。 |
| `actual_hz`、`overload`、`tick_multiplier` | 实测频率、runtime overload 状态和 multiplier。SpaceBattle 固定最低 25 Hz，不用自动降频掩盖超时。 |
| `workers`、`systems` | 本次运行的 worker 数和系统/围栏指标条目数。 |
| `query_direct`、`query_batched`、`gather_candidates`、`exact_distance_tests` | 混合目标获取的 direct query、batched gather、候选和精确 3D 距离测试累计计数。 |
| `weapon_uses`、`in_range_attacks`、`damage`、`deaths` | 武器尝试、射程内攻击、应用伤害总量和死亡数累计计数。 |
| `system=... mean_us=... p95_us=... max_us=... entities=... workers=...` | 应用系统与 fence 分解；包括 `FramePrepare`、`Publish`、`Behavior`、`Damage`、`Movement`、`Reap`、`Observe`、`dirty_marking`、`AABB_refresh`、`migrate_fence`、`FenceFinalize` 等。 |

`tick`、计数和百分位格式都使用 invariant culture，适合脚本逐字段消费。Profiler trace 是另一条高吞吐诊断管线，不等同 OpenTelemetry；不要把开启 trace 后的耗时当作关闭 trace 的性能基线。

## 终止结果、取消和 fatal

`termination` 只有以下几种稳定值：

- `draw`：存活数为 0。
- `winner`：初始多于一艘且只剩一艘。
- `tick_limit`：完成 `MaximumCompletedTicks`，默认是 22,500。
- `cancelled`：收到 Ctrl+C 或调用方取消；Host 会等待当前 in-flight tick 到边界，不再启动下一帧，然后执行 runtime shutdown。
- `fatal`：系统异常触发 `SystemExceptionPolicy.AbortTickAndStop`；会记录失败系统和完整异常，调用 `FatalStop()`，不重试也不继续 tick，并以 exit code 1 返回。
- `bootstrap_only`：仅供测试/调用方使用的 bootstrap 结果，命令行入口不选择此路径。

取消路径是 graceful 的边界停止而不是强制杀进程；profiler 在正常 shutdown 时才有机会 drain/flush。fatal 路径优先保留原始错误，不能假装完成完整 graceful shutdown 或保证当前 trace/当前 tick 已完全收尾。`AbortTickAndStop` 也不是整个 tick 的原子回滚：异常之前已成功提交的系统改动可以保留，已开始的并行工作可能完成，tick fence 仍可能执行；因此最终数据库可能包含部分 tick 的提交。

数据库采用 `SingleVersion` 组件。组件写入对后续读取可见，但由 runtime 每 tick 的 tick fence 批量写 WAL 才形成耐久边界。正常运行结束时 Host 会在读取最终快照前补做幂等回收并读取 committed database；不会提供 pause/resume 或崩溃后继续战斗的承诺。接受的 `FenceWal` 恢复缺口见 ADR-0008。

## 开启 profiler trace

Trace 默认关闭，性能测量必须保持关闭。需要诊断时，在启动前提供 trace 输出路径，并确保父目录存在：

```powershell
New-Item -ItemType Directory -Force trace | Out-Null
$env:TYPHON__PROFILER__TRACE = "trace/space-battle.typhon-trace"
dotnet run --project demo/SpaceBattle/SpaceBattle.Main/SpaceBattle.Main.csproj -c Release --no-restore
Remove-Item Env:TYPHON__PROFILER__TRACE
```

也可以复制/编辑输出目录旁的 `typhon.telemetry.json`，取消 `Trace` 注释后重启程序；环境变量优先于 JSON。程序退出后 `.typhon-trace` 保留在该路径，直到用户手动删除，可用 Typhon Workbench 离线打开。默认的 `typhon.telemetry.json` 只保留关闭状态，避免把 profiler 开销混入常规运行。

数据库和 trace 的保留规则不同：数据库每次 fresh run 会替换自己的 `space-battle.typhon` 目录并在 shutdown 后保留；trace 是追加诊断产物，由用户按运行目录自行归档/删除。测试使用临时目录并在 teardown 删除它们。

## 默认测试与显式性能场景

默认验证只构建/测试 SpaceBattle.Tests（不会构建整个解决方案）。首次执行测试时先还原测试项目：

```powershell
dotnet restore demo/SpaceBattle/SpaceBattle.Tests/SpaceBattle.Tests.csproj
```

随后运行：

```powershell
dotnet build demo/SpaceBattle/SpaceBattle.Tests/SpaceBattle.Tests.csproj --no-restore
dotnet test demo/SpaceBattle/SpaceBattle.Tests/SpaceBattle.Tests.csproj --no-restore --no-build --filter "TestCategory!=Quarantine&TestCategory!=Performance&TestCategory!=Manual"
```
这里显式排除 `Performance`/`Manual`，避免某些 adapter 的 Explicit-selection 配置把 release-only 场景带入默认测试；issue 验收的原始过滤器 `TestCategory!=Quarantine` 也可在默认 NUnit discovery 下使用，但不会改变本段的“默认不跑性能场景”约定。

`PerformanceTests.FiftyThousandShips_25Hz_500Ticks_ReportsMeasuredBreakdown` 是 `[Explicit]` + `[Category("Performance")]` 的 Release-only 场景（另带 `Manual` 类别，避免共享 CI 误运行）。默认测试不会执行它。手工测量时使用 Release，并显式选择该测试：

```powershell
dotnet test demo/SpaceBattle/SpaceBattle.Tests/SpaceBattle.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PerformanceTests.FiftyThousandShips_25Hz_500Ticks_ReportsMeasuredBreakdown" --logger "console;verbosity=detailed"
```
场景配置为 50,000 艘、25 Hz、500 tick；Host 在停止边界可能完成一个已经 in-flight 的额外 tick，因此 `completed_ticks` 可为 500 或 501，但测量窗口固定按 zero-based 125–499 取 375 个 tick。前 125 tick 只作为 warmup，丢弃后用这 375 个 `SimulationTickCompleted.Duration` 计算 p50/p95/p99/max。它同时打印 bootstrap、最新 telemetry sample 的系统/fence 实测分解和稳定计数；该系统 snapshot 是 runtime 已暴露的累积窗口（截至采样 tick，包含 warmup），不冒充 warmup-trimmed 的 phase 百分位。当前 runtime 未提供有效样本的 fence 条目会稳定显示 0，并在报告中标为“未暴露测量”，不把 0 解读为零成本。p95 严格超过 40 ms 只打印 `warning=p95_over_40ms`，不会 `Assert` 失败；40 ms 不是跨机器 CI SLA，机器、worker 拓扑、磁盘和 profiler 状态必须随报告记录。

## ADR 与边界

SpaceBattle 遵循主仓库根 `docs/adr/` 中的 ADR；任务 worktree 刻意不复制该目录。以下 `file:///` 链接指向本机主仓库对应文件，离线 checkout 时文件位于 `C:/Users/zyhs/source/repos/Typhon/docs/adr/`；不要在本 demo worktree 创建 ADR 副本：

- [ADR-0001：pull damage 与延迟销毁](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0001-pull-damage-and-deferred-destruction.md)：worker-private damage lane，统一 Damage 后再 Reap。
- [ADR-0002：cluster-native 宽系统](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0002-cluster-native-wide-systems.md)：不使用大实体集 pull-mode view。
- [ADR-0003：EntityKey 派生快照](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0003-derived-entity-key-snapshots.md)：组件为事实来源，派生数组只读跨系统共享。
- [ADR-0004：AABB3F fence rescan](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0004-aabb3f-fence-rescan.md)：三维位置通过 cluster span 写入并依赖 tick fence。
- [ADR-0005：固定 25 Hz 调度](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0005-fixed-runtime-scheduling.md)：并行 fence 开启、自适应 fence cost 关闭，不通过降频掩盖 overload。
- [ADR-0006：混合目标获取](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0006-hybrid-target-acquisition.md)：按 cluster 中 tracking 源数量选择 direct 或 gather+临时 bins。
- [ADR-0007：per-worker acquisition transaction](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0007-per-worker-acquisition-transactions.md)：每个 worker 在同一逻辑帧复用 thread-affine read transaction。
- [ADR-0008：接受当前 Typhon 限制](file:///C:/Users/zyhs/source/repos/Typhon/docs/adr/0008-accepted-typhon-limitations.md)：完整列出引擎限制、demo workaround、尚未修复缺口和“不宣称”的边界。

重要区分：二维 XY spatial grid、AABB3F 不可由 `WriteSpatial` 直接更新、缺少公开 epoch scope、并行 `EventQueue<T>.Push` 不安全、parallel fence 尾部锁、FenceWal 恢复缺口和跨 worker 无关确定性，都是当前 Typhon 的限制或尚未修复问题；它们不是 SpaceBattle 已修复的引擎能力。临时三维 bins、按 EntityKey 派生快照、span dirty marking、worker-private lanes/reap buffers、per-worker transaction slot 和固定 worker 的 Explicit 诊断，都是 demo 内 workaround。改动范围刻意不进入 `src/`。
