# WebAPI Platform Enterprise · 核心闭环

原 40 页高保真交互原型保留在 `../prototype`（本机 4180）。本目录是经过确认的首期真实产品工程：.NET 10 控制面、PostgreSQL、Redis、Worker、双 YARP 网关与 React 管理控制台。控制台以实际数据库、Cookie 会话及节点确认记录工作。

## 使用与边界

- [页面能力覆盖](docs/console-coverage.md)：逐一对应原 40 页；SSO、完整监控/告警、高级策略、完整 Breaking Change 引擎等仍属于后续开发。
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
