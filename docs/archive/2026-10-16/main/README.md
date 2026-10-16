# WebAPI Platform Enterprise · 核心闭环

原 40 页高保真交互原型保留在 `../prototype`（本机 4180）。本目录是经过确认的首期真实产品工程：.NET 10 控制面、PostgreSQL、Redis、Worker、双 YARP 网关与 React 管理控制台。控制台以实际数据库、Cookie 会话及节点确认记录工作。

## 使用与边界

- [页面能力覆盖](docs/console-coverage.md)：逐一对应原 40 页；28–33页已接入真实监控/告警；16/17页已在本批独立评审版本接入四类流量策略；SSO、JWT/重试/缓存、完整 Breaking Change 引擎仍属于后续开发。
- [验收与证据](docs/acceptance.md)：区分浏览器操作、数据库回归、真实容器业务响应与目标部署验收。
- [审查修正状态](docs/evidence/core-loop/final-review.md)与[实施决策](docs/decisions.md)：3项Important修正与最终浏览器复验均已完成；全部实施裁定与延期范围已记录。
- [配置与运维](docs/operations.md)：初始化、节点身份、快照、备份恢复和敏感日志处理。
- [设计基线](docs/superpowers/specs/2026-10-04-core-loop-design.md)与[实施计划](docs/superpowers/plans/2026-10-04-core-loop-implementation.md)。

## 本地构建与检查

前端需要 Node.js 22+ 与 pnpm；后端验证使用 Docker Compose 的固定 SDK 镜像，无须安装本机 .NET。先在 `console` 执行 `pnpm install --frozen-lockfile`，然后 `pnpm build`。`scripts/check-console.sh` 可通过 `WEBAPI_NODE`、`WEBAPI_PNPM` 指定已安装工具。

数据库回归需要显式 `.secrets/postgres-password`（仅本机、文件权限 600），没有内置密码。`./scripts/check.sh domain`、`integration`、`gateway` 使用专用 `webapi-enterprise-core-test` 项目；测试数据库与真实业务数据库分开。

`./scripts/check.sh e2e` 创建全新的随机临时测试项目，完成真实审批、A→B→回滚、旧请求跨切换及故障注入，退出时销毁**本次测试自己的**卷和凭证。它只使用合成数据，不能作为持久产品环境。`./scripts/e2e.sh --browser` 会等待浏览器验收；结束后删除 `.runtime/browser-e2e.wait` 才继续测试和清理。不要同时启动两个 E2E，因为本机测试端口固定。

`./scripts/package.sh` 生成源码交付压缩包；包含迁移、锁文件、配置、说明、验证证据与前端静态产物，排除密钥、测试数据库、运行目录、依赖缓存和 Git 元数据。

该交付在 Linux ARM64 容器完成本地验证。AMD64、企业网络/TLS、真实数据库备份恢复和生产容量仍须在目标环境单独验收；没有 5k RPS 或生产就绪承诺。

## 真实观测与告警阶段

[运行手册](docs/deployment/observability-runbook.md)、[数据字典](docs/observability-data-dictionary.md)、[本阶段证据](docs/evidence/observability/verification.json)。`./scripts/check-observability.sh e2e` 验证真实三源与持续告警，`faults` 加入真实容器停机、租约接管及核心兼容检查；`browser` 保持临时环境，结束删除该项目metadata同目录的 `browser.wait` 后精确清理。所有观测E2E端口按本次项目预分配到loopback，仍禁止并发修改共享构建目录。

`./scripts/package.sh observability <commit>` 从不可变提交封装本阶段源码、证据，并从同一提交重建前端静态产物，输出独立ZIP及manifest-observability.json，保留首期交付。原核心测试环境保留；独立长期项目的实际观测状态见下节。

## 独立长期本机环境与演示交付

用户选择的 A 方案已在 `webapi-enterprise-local` 建立本机演示业务，使用新数据库与独立卷，保留原 4180/4181/5090 入口。控制台为 [4192](http://127.0.0.1:4192)，管理员 `admin`；双网关 4196/4197、两个内网上游及三源真实运行。当前演示已完成独立审批、双ACK、请求、可观测与站内告警恢复验证。

- [演示使用说明](docs/deployment/local-demo-guide.md)：账号密码文件位置、调用命令及演示范围。
- [长期运行手册](docs/deployment/local-runtime-runbook.md)：构建、绑定、状态、启停、冷备份与隔离恢复。
- [演示恢复清单](docs/deployment/local-demo-recovery-checklist.md)：本实例的持久卷、私有凭据补充保存与恢复检查。
- [长期实例验收证据](docs/evidence/local-runtime/demo-activation.json)及[安装状态](docs/evidence/local-runtime/installation-status.json)：真实实例事实，区别于历史随机项目验收。

本机源码交付 V1/V1.1 和原40页原型保留。演示验收补充包仅封装说明与脱敏证据，不能替代源码镜像或私有数据备份。SSO、高级策略、完整 Breaking Change 引擎、企业实际接口与外部通知仍按能力覆盖表列为后续。

## 流量策略阶段（独立版本）

[交付索引](docs/evidence/policies/delivery-index.md)、[运行手册](docs/deployment/traffic-policies-runbook.md)与[数据字典](docs/traffic-policy-data-dictionary.md)。使用 `./scripts/check-policies.sh browser` 构建实际源码固定副本并保留随机端口评审入口；`e2e` 自动精确清理。`verify` 检查源码/镜像/截图/业务事实与旧包保留。`python3 scripts/package-policies.py --output <新ZIP路径>` 生成独立源码包及逐文件摘要。旧 4192 环境保持原版本，升级须另行固定提交并安排维护窗口。

## 2026-10-06 本机升级补充（当前状态）

上面的长期演示及流量策略段落保留各阶段交付记录，其中尚未升级、SSO或高级策略待开发的表述不代表当前状态。4192已部署固定提交63681d1，包含流量策略、系统设置、OIDC SSO、独立Keycloak及网关重启运行确认；本机正常重启后双网关Ready，业务仍为v4/seq4。详见[本机升级结果](.worktrees/gateway-restart-readiness/docs/deployment/gateway-readiness-upgrade-result.md)和[固定维护入口](.worktrees/gateway-restart-readiness/docs/deployment/gateway-readiness-upgrade-runbook.md)。生产验收仍未执行。
