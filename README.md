# WebAPI Platform Enterprise

.NET 10 控制面、PostgreSQL、Redis、Worker、双 YARP 网关和 React 管理控制台组成的 API 管理工程。管理页面使用真实数据库、Cookie 会话及节点确认记录；保存工作配置与发布到网关是不同操作。

## 从哪里开始

- [项目资料总目录](docs/README.md)：功能、源码、运维、验收与历史资料的统一入口。
- [当前源码与验证状态](docs/deployment/current-project-status.md)：区分 GitHub 主干、本地修改、固定版本验证和实际安装。
- [运维与交付目录](docs/deployment/README.md)：按用途查找手册、阶段交付和历史安装记录。
- [40 页能力映射](docs/console-coverage.md)与[逐项需求台账](docs/evidence/work-package4/requirements-status.md)：页面有入口不等于对应需求全部验收完成。

GitHub 主干与本地目录可能处于不同提交。阅读“当前”“已完成”“已部署”时，先核对记录的提交和日期；不同工具留下的总结不能覆盖实际代码及固定版本证据。

## 代码结构

| 目录 | 内容 |
| --- | --- |
| `src/` | 控制面、Worker、网关、领域、持久化、契约与运行工具 |
| `console/` | React 管理控制台及前端测试 |
| `tests/` | 领域、集成、网关、端到端测试和隔离夹具 |
| `deploy/`、`scripts/` | 固定镜像、环境配置及构建、验证、打包入口 |
| `docs/` | 当前说明、操作参考、固定版本证据及历史记录 |
| `third_party/` | 固定第三方源码、许可证与来源说明 |

原 40 页交互原型属于独立资料，不在本 Git 仓库中；本机路径或演示端口不能作为仓库交付文件。

## 构建与验证

依赖版本以 [SDK 配置](global.json)、[后端集中版本](Directory.Packages.props)、[前端依赖](console/package.json)及对应锁文件为准。当前 SDK 固定为 `10.0.401`，禁止自动滚动到其他版本；CI 配置使用 Node `24.22.1`、pnpm `11.25.0`。

已核实的 main `5192415` 的四项 GitHub 检查已实际运行通过：4255 项通过，0 失败/跳过，类型检查与前端构建通过。固定版本 `3774ccc` 的历史 CI 结果单独保留。本地凭证影响类现40项通过，本轮含4项真实HTTP并发撤权顺序验证；此前网关定向20项通过，覆盖精确时间边界及工作配置须再发布才改变双节点行为。各轮分开计数，完整页面、部署节点及生产验收待补。2026-11-11只读核对已确认四项检查列为主干必需检查；完整规则与剩余交付边界见[当前状态](docs/deployment/current-project-status.md)。

前端先执行 `pnpm --dir console install --frozen-lockfile`，再执行 `bash scripts/check-console.sh`。可用 `WEBAPI_NODE`、`WEBAPI_PNPM` 指定已有工具。

后端优先执行 `bash scripts/check-contracts.sh domain`、`integration`、`gateway`。各次使用独立 owner、密码、数据库、依赖缓存和固定 SDK 镜像，退出时按归属清理自己的测试资源。旧 `scripts/check.sh` 使用固定测试项目，不应在共享环境并发运行。

固定端口 E2E、故障演练、性能实验和持久环境维护须按[运维目录](docs/deployment/README.md)选择对应入口，不能与普通回归混用。交付打包入口为 `scripts/package.sh`；运行目录、秘密、数据库转储和依赖缓存不属于公开资料。

## 能力边界

源码包含组织/项目/环境、API 与契约、路由策略、应用凭证、审批发布、监控告警、SSO 和跨环境交付等实现。普通凭证轮换及撤销依赖再次审批发布；安全检查点/租约签发仍关闭，网关安全准入尚未接线，详见[安全操作说明](docs/deployment/credential-security-operations.md)。

本地或固定版本验证不等于新主干已安装，也不等于企业生产验收。目标环境的 TLS、HA、AMD64、容量/SLA、真实身份源与业务端点需要对应证据。
