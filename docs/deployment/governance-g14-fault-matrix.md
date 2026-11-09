# G14 完整协调式故障矩阵(维护窗口协调循环容错验证)

> Work Package 4 / G14 剩余项 ①。补全 D1 最小子集(`g14-minimal.mjs`,3 项)之外的**协调式故障组合**:在"发布 → 节点 ACK → 收敛"投递循环上注入独立容器故障,断言 **PG 权威收敛**与**幂等恢复**。

## 范围与结论

**范围**:本地一次性 E2E 环境(命名 compose 工程 `webapi-enterprise-e2e-*`,与生产无关;固定端口 5092/4186/4187/4182,与运行环境 4192/4196/4197 不重叠,可并存)。双网关 `gateway-a`/`gateway-b` 均注册为投递节点。

**结论:通过(7/7 协调式 D1 故障)**。D1 最小子集 3 项 + 本矩阵 4 项,全部在一个环境内串行跑绿。

| 检查 | 故障 | 结果 |
|---|---|---|
| D1-1 | 节点 ACK 后 kill → 存活节点收敛 | passed(见 `g14-minimal.json`) |
| D1-2 | worker 中途 kill → 幂等续 | passed |
| D1-3 | 协调式 Redis 重启 → PG 权威不反写 | passed |
| D1-4 | **PG 原地重启(权威存储本身)** → 协调事实持久 | passed |
| D1-5 | 单网关发布中途 down(ACK 前)→ 恢复后收敛 | passed |
| D1-6 | **worker + 网关双服务故障** → 恢复后收敛 | passed |
| D1-7 | **双网关全停(总中断)** → 恢复后收敛 | passed |

证据:`docs/evidence/governance-merge/g14-matrix.json`(4/4 `complete:true`)+ `g14-minimal.json`(3/3)。

## 关键发现(本矩阵验证出的不变量)

1. **收敛要求所有节点都 ACK(非任一节点)。** 任一节点缺 ACK,发布不能进入 `Succeeded`。已被 kill 但已持久化 ACK 的节点可被其 ACK 事实"替补";未及 ACK 就被 kill 的节点必须恢复后自行补齐。

2. **发布有 ~30 秒 ACK 时限(`failure_code = 'ack_timeout'`,`deadline_at - created_at ≈ 30s`),超时干净失败而非悬挂。** 节点若在时限内未能重新注册并 ACK(重启+重注册+重 ACK 常 >30s),在途发布以 `ack_timeout` 失败;**待节点恢复后 `POST /releases/{id}/retry` 幂等收敛**(retry 创建新发布,重新驱动协调)。这使"节点在 ACK 窗口内长时间不可用"成为**可预期的失败 + 恢复**,而非不可测的悬挂。

3. **时序敏感 → 两种终态都合法。** 节点恢复快于 30s 时限 → 发布直接 `Succeeded`;慢于时限 → `Failed`(ack_timeout)→ retry 收敛。`faultThenRecover` 对两条路径都做收敛断言(终态 + 恰好 2 个 ACK + 双网关服务新序列),**不变量是"故障后收敛到一致态",不是"必须失败"**。证据里 `stateDuringFault` / `recoveredByRetry` 记录当次实际路径。

4. **worker 是协调者,worker 挂了发布无法推进到任何终态**(30s 时限与协调推进都由 worker 执行)。故 D1-6(worker 故障)必须**先恢复 worker** 再等终态;恢复后的 worker 从 PG **幂等续**在途发布,把发布推到终态。节点故障(D1-5/D1-7)则相反:worker 存活,让故障贯穿时限 → 触发 `ack_timeout` → 再恢复节点。

5. **PG 是权威存储;已提交的协调事实跨 PG 重启持久。** D1-4 以**直接 SQL**(绕过可能已脱节的控制面连接池)比对重启前后的 `environments.deployment_sequence`、`release_records.status`、`gateway_acks` 计数——三者在 PG 重启后逐字节一致。API/流量若因控制面连接池断裂短暂不可用,重启 `control-plane`+`worker` 即恢复(本环境多为自恢复)。

## 检查语义(脚本 `scripts/g14-matrix.mjs`)

共享 `faultThenRecover(backend, fault, recover, name, recoverFirst)`:
1. 建版 → 更新 `/orders` 路由绑定 → 预览 → 建发布(取 preview 的 `resourceRevisions`)→ 双审批 → `POST /publish` → 等 `Publishing`;
2. `fault()`(注入故障);`recoverFirst`(D1-6)则**立即** `recover()`,否则让故障贯穿;
3. 等在途发布到终态(`Succeeded`/`Failed`,120s);非 `recoverFirst` 再 `recover()`;
4. 等双网关 `/orders` 200 + 全部节点 `offline=false`;
5. 若终态 `Failed` → `POST /retry` → 等 retry 发布 `Succeeded`;
6. 断言成功发布**恰好 2 个 ACK 事实** + 双网关服务 `backend`@新序列。

`D1-4`(PG 重启)独立:SQL 断言持久 → 等 PG 可查询 → 比对前后事实 → 确保 API 可用(必要时重启控制面服务)→ 等双网关 200。

## 如何复现

```bash
# 1. 一次性环境(构建+迁移+seed+全栈,不自动清理)
bash .runtime/g14/setup-env.sh          # 写 .runtime/g14/env.txt(project + environmentId)
# 2. 基线(/orders 路由 + baseline Succeeded 发布)
<WEBAPI_NODE 及各 env 变量> node .runtime/g14/establish-baseline.mjs
# 3. 故障矩阵
node scripts/g14-minimal.mjs            # D1-1/2/3
node scripts/g14-matrix.mjs             # D1-4/5/6/7
# 4. 拆除(含 --volumes)
bash .runtime/g14/teardown-env.sh
```

必需环境变量:`WEBAPI_E2E_PROJECT`、`WEBAPI_E2E_RELATIVE_DIR`、`WEBAPI_E2E_DIRECTORY`、`WEBAPI_ENVIRONMENT_ID`。一次性工程名必须以 `webapi-enterprise-e2e-` 开头(脚本硬校验,防误伤运行环境)。

## 残留

G14 另一项(② 4 个 reviewed-evidence 测试项,需 Layer-2 reviewed-delivery 管线产出 `WEBAPI_{POLICY,NETWORK_POLICY,SETTINGS,SSO}_RESULT_DIRECTORY`)**不在本矩阵范围**,另行处理。本矩阵只覆盖协调式故障容错(①)。
