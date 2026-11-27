# P3 修复与项目复查

受测产品为 `de415066e7dd001584dd2491fcba5bfec65fb27a`。原清单的 D23、G02、D15、D17、D14、D16、D18、F10、F11、P03-C02 共 10 项均已修复。本轮另修项目与环境操作范围、版本 OpenAPI 下载、未发送响应状态的显示和三份恢复测试的 CI 接入。原型 454 项中有 18 项已取得固定提交的隔离验收记录，436 项待逐项复验。[各需求对应的证据](../../deployment/requirements-acceptance-current.json)

## 尚待完成

- P1 现场。P12 生产目标待登记，12 个场景均未执行。G14 现场仍暂停，正式性能资格还差 11 组、22 个窗口。
- P2 告警。滚动窗口内已退役后端的流量会在规则试算与 Worker 处理中被漏计，活动告警可能错误恢复。[告警作用域](../../../src/WebApi.Infrastructure/Alerts/AlertRuleScopeResolver.cs)
- P2 迁移工具。Migrator 在收到普通帮助参数时仍可登记迁移尝试。[命令入口](../../../src/WebApi.Migrator/Program.cs)
- P2 专用验收。晋级、流水线、安全加固的三个命令未接入恢复存储预算与独立验证器，七个模式在创建容器前退出。[晋级](../../../scripts/delivery/cli.mjs) · [流水线](../../../scripts/delivery/pipeline-cli.mjs) · [安全加固](../../../scripts/security/acceptance.mjs)
- P2 安装集成测试。提供九项本地迁移测试预算及匹配的镜像清单后，六项原生用例有 3 项通过、3 项失败。备份和环境绑定的夹具仍需首次换密，卷权限夹具需采用正式的新卷预配流程。[本地安装集成套件](../../../tests/runtime/integration)

## 独立复核记录

受测提交为 `de415066e7dd001584dd2491fcba5bfec65fb27a`，Git 树为 `395330b3f1f0db054046ba50c766bce5503f3662`。复核人是本轮可观测性子任务的审查代理。本人参与了 G02、D15 和状态文案的实现，D16、D23 由其他实现者完成。本次从受测提交单独归档产品源码，使用独立 PostgreSQL、Redis、固定 SDK 和临时探针，完成 22 项定向用例。复核清理后测试容器、卷、网络均为零。记录仅覆盖本文的本机定向验证。[本次完整定向日志](/private/tmp/webapi-p3-observability-20261127/release-de41506-integration.log)

## 仍可复现的 P2

两个网关先确认含目标后端的发布，目标删除后再次确认发布。对同一授权环境、同一滚动一小时和同一受控 Prometheus 源，监控 HTTP 200 返回 **200 个样本、0.0555556 req/s**，规则 `request_rps > 0.01` 的试算 HTTP 200 却返回 **0 个样本、0 req/s、Known、条件 false**。首轮 Worker 评估创建 Open 事件，退役后下一轮把同一事件标为 Resolved，原因为 Recovered。正确性断言对样本、速率、阈值条件和事件保持四项均失败。监控读取传入窗口起点，而告警解析仅传截止时刻，遗漏了查询窗口内曾承载流量的后端。监控查询与测试告警接口均在实际控制面执行，事件转换读取真实数据库。[本提交红灯与原始数值](/private/tmp/webapi-p3-observability-20261127/release-de41506-alert.json) · [正确性断言](/private/tmp/webapi-p3-observability-20261127/release-de41506-alert-check.py)

该问题上次实际复现于 `2412b4957432eb77f71253e0592385fb23a798eb`，`af78d70b882bfb40eb1289a241e6a625fcb54896` 曾逐文件核对同一实现。本次在 `de415066e7dd001584dd2491fcba5bfec65fb27a` 重新创建双 ACK 场景并重跑 Worker，复现误恢复。[首次运行回执](/private/tmp/webapi-p3-observability-20261127/audit-worker.log) · [源码比对](/private/tmp/webapi-p3-observability-20261127/release-de41506-source-digests.json)

## 已修复项

| 项目 | 受测提交上的结果 |
| --- | --- |
| G02 供应商请求上限 | 1、65、129、2048 个可见应用均为 11 次即时查询加 3 次范围查询，共 14 次。129 与 2048 个应用的两条长查询使用 POST。实际 Prometheus 对带路径前缀的 80,432／80,392 字节表单均返回 200、matrix。另用四条真实 PromQL、32 条时序验证跨环境同名应用和目标的授权隔离，含外部及错配标签。超出响应或维度预算的定向用例亦通过。 |
| D15 慢 API 排行 | P95、P99 均只计 2 个有可用延迟的组，每页一个，第三页为空；同一输入按请求量仍为 5 组。 |
| 无响应状态文案 | 当前指标展示保留 `None` 与旧 `0` 的原始分组键，均显示“未发送响应状态”。HTTP 200、503、总计 215、取消 10 和 5xx 计数 5 全部通过。 |
| D16 流水线节点确认时限 | 本次使用 17 秒，控制面目录和详情均返回 17，独立 Worker 的实际截止时间为 `2026-11-27T02:23:03.615141+00:00`，落在构建时间加 17 秒的区间。首个 ACK 后为 Publishing，第二个后为 Succeeded，重复 ACK 仍然两条。 |
| D23 晋级映射复用 | 旧格式省略准备元数据，颠倒映射顺序后各执行两轮保存与预检。authentication 与 timeout 的优先级分别含 7/7 与 11/7 两种设置，每种均只保留原来两个独立策略 ID，候选哈希保持一致。并发幂等、非本路由的同前缀策略和来源策略的仓库用例通过。 |

[本次请求计数与有效分组](/private/tmp/webapi-p3-observability-20261127/release-de41506-calls.log) · [实际 PromQL](/private/tmp/webapi-p3-observability-20261127/release-de41506-promql.log) · [长 POST 回执](/private/tmp/webapi-p3-observability-20261127/release-de41506-prometheus-http.log) · [业务与发布回执](/private/tmp/webapi-p3-observability-20261127/release-de41506-integration.log)

## 来源与适用范围

- 与 `af78d70b882bfb40eb1289a241e6a625fcb54896` 比对，最终受测提交中可观测性产品的唯一差异是状态显示同时识别 `None` 与旧 `0`。回归文件增加对应计数用例。D16 涉及的 12 个实现、部署与测试文件，D23 涉及的 5 个文件，以及退役告警判定链的 6 个文件均逐字相同。[Git 源文件摘要](/private/tmp/webapi-p3-observability-20261127/release-de41506-source-digests.json)
- G02、D15 最初的红灯在 `b01313dea3adb4fb96fa47b7e90e3f2329fa5ea0` 的产品基线上。最初 64 项绿色回执来自该基线上的修复工作树，三份被测文件的 SHA-256 全部对应后来提交的 `2412b4957432eb77f71253e0592385fb23a798eb`。这次在 `de415066e7dd001584dd2491fcba5bfec65fb27a` 重新捕获四组请求计数与跨环境 PromQL，生成的查询与两份长表单均与原始文件逐字相同。[初次红绿与文件摘要](/private/tmp/webapi-p3-observability-20261127/results.json) · [本次查询](/private/tmp/webapi-p3-observability-20261127/release-de41506-probe/captured.json)
- `HTTP None` 原始红灯和旧 0 对照实际在 `af78d70b882bfb40eb1289a241e6a625fcb54896` 的归档上执行；39 项绿色回执来自随后的修复工作树，两份文件摘要与 `de415066e7dd001584dd2491fcba5bfec65fb27a` 一致，本次在该提交重新执行状态分组用例。D16／D23 上轮 18 项业务与 3 项浏览器回执实际测试 `af78d70b882bfb40eb1289a241e6a625fcb54896`，其中部署配置解析分别为默认 120、覆盖 47、E2E 30 秒，控制面与 Worker 配置一致。[状态展示红绿证据](/private/tmp/webapi-p3-observability-20261127/status-fix.json) · [上一轮 D16、D23 独立回执](/private/tmp/webapi-p3-observability-20261127/final-independent-review.json)

## G02、D15 最终提交交叉审查

复核人为本轮负责 API 目录与控制台的代理，未参与 G02、D15 及 HTTP 状态文案实现。受审提交为 `de415066e7dd001584dd2491fcba5bfec65fb27a`，树为 `395330b3f1f0db054046ba50c766bce5503f3662`。与已作独立审查的 `2412b4957432eb77f71253e0592385fb23a798eb` 比较，最终提交保留了跨环境资源筛选、P95/P99 慢榜及本轮四种规模的 14 次请求上限。

| 核查项 | 最终提交的证据 |
| --- | --- |
| 无响应状态 | 变动仅位于 `PrometheusMetricSource.cs` 的分组显示名称。原始键 `None`、旧键 `0` 各自保留，均显示“未发送响应状态”。最终提交的定向用例同时验证 HTTP 200、503、无响应组分别 200、5、10 次，总数 215，取消 10 次、5xx 为 5 次。 |
| G02 授权与请求数 | 最终 C# 探针捕获文件与前次文件逐字节相同。1、65、129、2048 个可见应用均发出 11 次即时查询、3 次范围查询，后两种规模均有 2 次 POST。按环境组合 API、共享应用和后端的四条真实表达式与两份规则夹具逐条一致。 |
| D15 慢榜 | 筛选、排序和分页的源码未变。最终提交的 P95、P99 HTTP 测试分别保留 2 个有请求及分位值的组，前两页各 1 组，第三页为空；同一输入按请求量排序保留 5 组。另一个候选探针中，唯一上榜的 Orders 有 10 个请求、P95 为 95 ms。 |

**本次亲自执行的核查。** 运行 `git diff -U4 2412b49 de415066` 对照指标源与测试，使用 Python 从 `git show` 取原始字节并计算 SHA256。剔除上述唯一的显示表达式后，两版指标源的 SHA256 均为 `14b8c44193507b0633cc8c0929a5feb4e571f66f8ee298492a93fb904c78aa28`。最终指标源完整哈希为 `3683715eca53e6fa8f46379e3ed1148c09bd55b4a2b8c40c331d4b08cb98b7fb`，供应商客户端两版均为 `9ffce44a96a5987c9dad3008f439e837a41cd353b5eeb69fbe24b3865af8e545`。[最终 C# 探针输出](/private/tmp/webapi-p3-observability-20261127/release-de41506-calls.log)

再次在无网络、只读挂载的锁定 Prometheus 镜像中运行 `promtool test rules /counter.yml /scope.yml`，两组均返回 `SUCCESS`，退出码为 0。15 条独立重启/错配时序的总速率与筛选速率分别为 3、2 req/s。另 32 条跨环境同名资源时序得 13 和 2 req/s 两个状态组，筛选后为 2 req/s，累积桶分别为 [13, 15, 15] 和 [0, 2, 2]。两组共执行 8 次表达式断言。镜像摘要为 `efd719c99d83b060d9daefdcf00360461adf279f45ef5391f8d111892118753e`。[重启及错配夹具](/private/tmp/webapi-p3-catalog-20261127.SdygfuZ5/fixed-run/obs-counter-rules.json) · [发布提交的作用域夹具](/private/tmp/webapi-p3-observability-20261127/release-de41506-prometheus/scope-promql.json)

最终提交的 .NET 22 项定向回执由实现侧运行，本人核对运行日志、具名用例和 SHA256 `1e990e04f669b6a16b8e08cb7b9302e42ff6c4e4264541d3be62783483f8bb78`。本次交叉审查使用本机合成样本，目标环境容量仍待正式验收。退役后端引发告警误恢复的 P2 在同一提交另有红灯，继续单列。[定向原始日志](/private/tmp/webapi-p3-observability-20261127/release-de41506-integration.log) · [该 P2 的独立记录](independent-review.md)
