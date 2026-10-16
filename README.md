# WebAPI Platform Enterprise

原 40 页高保真交互原型保留在 `../prototype`（本机 4180）。本目录是经过确认的首期真实产品工程：.NET 10 控制面、PostgreSQL、Redis、Worker、双 YARP 网关与 React 管理控制台。控制台以实际数据库、Cookie 会话及节点确认记录工作。

## 使用与边界

- [本次集成与检查](docs/deployment/full-project-integration-20261016.md)：代码、历史验收、性能实验及本机部署的版本边界。
- [页面能力覆盖](docs/console-coverage.md)及[40页需求关闭台账](docs/evidence/work-package4/requirements-status.md)：页面覆盖与逐项功能验收分别记录；当前台账仍有部分、缺失和待复验项。
- 已交付实现包括 SSO、契约导入与兼容性检查、JWT／重试／缓存、外部通知、跨环境审批发布，以及 CORS／IP／Transform（含 JSON 请求／响应正文映射）。具体运行边界见 `docs/deployment/` 各阶段手册。
- [第4包验收](docs/evidence/work-package4/final-summary.md)：首次改密、显式重置、Credential 使用统计和轮换；[操作手册](docs/deployment/account-credential-operations.md)。历史固定版本验收不自动代表新集成版本或4192已升级。
- [验收与证据](docs/acceptance.md)：区分浏览器操作、数据库回归、真实容器业务响应与目标部署验收。
- [审查修正状态](docs/evidence/core-loop/final-review.md)与[实施决策](docs/decisions.md)：3项Important修正与最终浏览器复验均已完成；全部实施裁定与延期范围已记录。
- [配置与运维](docs/operations.md)：初始化、节点身份、快照、备份恢复和敏感日志处理。
- [设计基线](docs/superpowers/specs/2026-10-04-core-loop-design.md)与[实施计划](docs/superpowers/plans/2026-10-04-core-loop-implementation.md)。

## 本地构建与检查

前端需要 Node.js 22+ 与 pnpm；后端验证使用 Docker Compose 的固定 SDK 镜像，无须安装本机 .NET。先在 `console` 执行 `pnpm install --frozen-lockfile`，然后 `pnpm build`。`scripts/check-console.sh` 可通过 `WEBAPI_NODE`、`WEBAPI_PNPM` 指定已安装工具。

优先使用 `./scripts/check-contracts.sh domain`、`integration`、`gateway`：每次创建随机独立 owner、密码、数据库和依赖缓存，退出时校验归属后清理本次资源。支持 `WEBAPI_CONTRACT_TEST_SUBNET` 指定经检查空闲的测试网段，以及 `WEBAPI_CONTRACT_NUGET_SEED` 指定只读依赖种子卷。它们不会连接持久产品数据库。旧 `scripts/check.sh` 使用固定测试项目和显式 `.secrets/postgres-password`，不要并发用于共享环境。

`./scripts/check.sh e2e` 创建全新的随机临时测试项目，完成真实审批、A→B→回滚、旧请求跨切换及故障注入，退出时销毁**本次测试自己的**卷和凭证。它只使用合成数据，不能作为持久产品环境。`./scripts/e2e.sh --browser` 会等待浏览器验收；结束后删除 `.runtime/browser-e2e.wait` 才继续测试和清理。不要同时启动两个 E2E，因为本机测试端口固定。

`./scripts/package.sh` 生成源码交付压缩包；包含迁移、锁文件、配置、说明、验证证据与前端静态产物，排除密钥、测试数据库、运行目录、依赖缓存和 Git 元数据。

该交付在 Linux ARM64 容器完成本地验证。AMD64、企业网络/TLS、真实数据库备份恢复和生产容量仍须在目标环境单独验收；没有 5k RPS 或生产就绪承诺。

## 真实观测与告警阶段

[运行手册](docs/deployment/observability-runbook.md)、[数据字典](docs/observability-data-dictionary.md)、[本阶段证据](docs/evidence/observability/verification.json)。`./scripts/check-observability.sh e2e` 验证真实三源与持续告警，`faults` 加入真实容器停机、租约接管及核心兼容检查；`browser` 保持临时环境，结束删除该项目metadata同目录的 `browser.wait` 后精确清理。所有观测E2E端口按本次项目预分配到loopback，仍禁止并发修改共享构建目录。

`./scripts/package.sh observability <commit>` 从不可变提交封装本阶段源码、证据，并从同一提交重建前端静态产物，输出独立ZIP及manifest-observability.json，保留首期交付。当前持久本机服务未自动重配观测端点；实际数据需要按手册启用Collector及三源。

## 流量策略阶段（独立版本）

[交付索引](docs/evidence/policies/delivery-index.md)、[运行手册](docs/deployment/traffic-policies-runbook.md)与[数据字典](docs/traffic-policy-data-dictionary.md)。使用 `./scripts/check-policies.sh browser` 构建实际源码固定副本并保留随机端口评审入口；`e2e` 自动精确清理。`verify` 检查源码/镜像/截图/业务事实与旧包保留。`python3 scripts/package-policies.py --output <新ZIP路径>` 生成独立源码包及逐文件摘要。旧 4192 环境保持原版本，升级须另行固定提交并安排维护窗口。
