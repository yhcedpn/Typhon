# PROTOTYPE — SpaceBattle Transient membership view

本目录是一次性验证代码，不是 SpaceBattle 生产实现。

## 要回答的问题

恢复已有数据库后，批量重建带 `[Index(AllowMultiple = true)]` 的 `Transient` Ship membership component，能否在 runtime 启动前立即通过 `WhereField(...).ToView()` 得到完整且可增量维护的 Ship view；`Commit`、`WriteTickFence`、view 创建和 runtime refresh 的最小正确顺序是什么？

## 运行

```powershell
dotnet run --project demo/SpaceBattle/SpaceBattle.TransientViewPrototype/SpaceBattle.TransientViewPrototype.csproj -c Release
```

程序只创建并删除一个名称含 `PROTOTYPE-WIPE-ME` 的独立临时数据库，不修改 SpaceBattle 生产数据库。

## 验证结论

2026-08-11 在分支 `codex/prototype-spacebattle-transient-view` 上以 Release 运行通过。

| 场景 | 结果 |
|---|---|
| 新数据库，Spawn membership 后 Commit，再创建两个 view | 两个 view 都立即包含 3/3 Ship；新实体的索引在 Spawn Commit 路径插入，无需显式 fence。 |
| view 创建后 Spawn / Destroy | 显式 `Refresh(tx)` 后，两个独立 view 都分别得到正确的 `Added=1` / `Removed=1`。 |
| 已有数据库重开 | 3 个权威 Ship 保留，Transient membership 全部归零，key `0` 的重建索引包含 3 个实体。 |
| 批量重写 membership 后仅 Commit | `WhereField` 与随后创建的 view 已看到 3/3 Ship。当前 Transient cluster query 固定使用 SoA Path B，因此查询正确性不等待 B+Tree shadow drain。 |
| Commit 后、fence 前创建 view，再执行 fence | view 初始已经是 3/3，但 fence 后第一次 Refresh 又报告 `Added=3`。底层 Transient index 和 view 通知在 fence 的 shadow drain 才发生，因此此顺序会制造重复初始 Added。 |
| Commit → fence → 创建两个 view | 两个 view 都从完整 3/3 集合开始，且没有待 drain 的恢复 delta；这是恢复流程的推荐顺序。Transient 不写 WAL，所以本次 `WriteTickFence` 返回的 LSN 为 `0`。 |
| incremental view 直接作为 runtime system input | Spawn 后 runtime 连续推进 5 tick，system 与 view 仍停在 3，数据库已经是 4；runtime 只自动 refresh pull-mode view。 |
| 增加显式 refresh 阶段，再运行 QuerySystem | Spawn 后两个 view 与 system 都从 4 增至 5，并各收 `Added=1`；Destroy 后都回到 4，并各收 `Removed=1`。 |

## 推荐顺序

恢复已有数据库时：

1. 打开数据库并遍历权威 Ship。
2. 在同一批 transaction 中重写 `ShipMembershipComponent.RunEntityKey`。
3. `Commit()`。
4. 在 runtime 启动前调用一次 `WriteTickFence(...)`，排空 Transient index shadow 与 view notification。它不是为了 Transient durability，而是为了建立干净的 index/view 起点。
5. fence 完成后创建所有 `WhereField(...).ToView()`。
6. 在每个 tick 的所有 Ship view 消费者之前，用一个明确的早期 runtime 阶段对每个 incremental view 调用一次 `Refresh(ctx.Transaction)`；后续 `QuerySystem.Parallel()` 再消费它。

仅从“初始成员是否完整”看，第 4 步不是必需的；从“完整且 delta 语义干净、可增量维护”看，第 4 步应保留。仅把 incremental view 交给 runtime 作为 system input 并不足够，runtime 当前不会替它 drain delta。

## 源码证据

- `EcsQuery.ScanAllArchetypes` 强制 Transient index home 走读取 live SoA 数据的 Path B；`ToIncrementalView` 的初始填充复用该路径：`src/Typhon.Engine/Ecs/public/EcsQuery.cs`。
- `DatabaseEngine.ProcessClusterShadowEntries` 在 tick fence 移动 Transient B+Tree key，并向每个注册 view 的 delta buffer 写通知：`src/Typhon.Engine/Ecs/public/DatabaseEngine.TickFence.cs`、`src/Typhon.Engine/Ecs/public/DatabaseEngine.ClusterMigration.cs`。
- `TyphonRuntime.RefreshSystemInputViewsAtTickStart` 明确跳过 `!view.IsPullMode`，因此 incremental system-input view 不会自动 refresh：`src/Typhon.Engine/Runtime/public/TyphonRuntime.cs`。

## Versioned 备选

当前 Transient 方案在“fence 后创建 view，并设置显式 runtime refresh 阶段”的条件下可行，不建议改为 Versioned。Versioned membership 会跨重开保留并在 Commit 路径维护索引，但代价是为纯派生工作集支付 revision、WAL 与持久化空间，并引入一份可能与权威 Ship/run 状态漂移的 durable membership。它也不会解决 runtime 不自动 refresh incremental system-input view 的问题。
