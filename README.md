# WebAPI Platform Enterprise

.NET 10 控制面、PostgreSQL、Redis、Worker、双 YARP 网关和 React 管理控制台组成的 API 管理工程。管理页面使用真实数据库、Cookie 会话及节点确认记录；保存工作配置与发布到网关是不同操作。

## 从哪里开始

- [项目资料总目录](docs/README.md)：功能、源码、运维、验收与历史资料的统一入口。
- [当前源码与验证状态](docs/deployment/current-project-status.md)：区分 GitHub 主干、本地修改、固定版本验证和实际安装。
- [运维与交付目录](docs/deployment/README.md)：按用途查找手册、阶段交付和历史安装记录。
- [40 页能力映射](docs/console-coverage.md)与[逐项需求台账](docs/evidence/work-package4/requirements-status.md)：页面有入口不等于对应需求全部验收完成。

GitHub 主干与本地目录可能处于不同提交。阅读“当前”“已完成”“已部署”时，先核对记录的提交和日期；不同工具留下的总结不能覆盖实际代码及固定版本证据。

历史本地验证和“尚未公开”“未推送”等表述仍绑定`9d01066`及各轮具名输入时点；新增GitHub运行单列真实提交与运行号，不回填旧记录。实际发布及合并状态应核对本仓库PR和对应CI，历史失败、Unknown及累计保全incomplete不会因发布而解除。

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

2026-11-14 核对固定主干 `48741be7904accce7795dce3827f2971e6de7847`：[PR #11](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/11) 已合并，PR 与合并后 main 的四项必需检查分别全部通过，每轮 4386 项、0 失败、0 跳过，前端类型检查与构建通过。主分支仍要求严格同步及 `console`、`contracts-domain`、`contracts-integration`、`contracts-gateway`；未放宽产品预算、断言或保护规则。

登录诊断、缓存回归和 Python CI 已进入该主干。剩余本地分支与未提交资料的内容审计见[本地资料核对](docs/deployment/local-material-audit-20261114.md)。历史两次 CI 失败的根因仍为 Unknown，各轮原始结果保留在[状态记录](docs/deployment/current-project-status.md)；后续成功不改写旧失败，也不代表已部署或完成全部需求验收。

依赖版本以 [SDK 配置](global.json)、[后端集中版本](Directory.Packages.props)、[前端依赖](console/package.json)及对应锁文件为准。当前 SDK 固定为 `10.0.401`，禁止自动滚动到其他版本；CI 配置使用 Node `24.22.1`、pnpm `11.25.0`。Python 离线模块运行 `python3 -B -m unittest discover -s tests/local_diagnostics -p 'test_*.py' -v`，不需要下载依赖或启动现场环境。

前端先执行 `pnpm --dir console install --frozen-lockfile`，再执行 `bash scripts/check-console.sh`。可用 `WEBAPI_NODE`、`WEBAPI_PNPM` 指定已有工具。

后端优先执行 `bash scripts/check-contracts.sh domain`、`integration`、`gateway`。各次使用独立 owner、密码、数据库、依赖缓存和固定 SDK 镜像，退出时按归属清理自己的测试资源。旧 `scripts/check.sh` 使用固定测试项目，不应在共享环境并发运行。

固定端口 E2E、故障演练、性能实验和持久环境维护须按[运维目录](docs/deployment/README.md)选择对应入口，不能与普通回归混用。交付打包入口为 `scripts/package.sh`；运行目录、秘密、数据库转储和依赖缓存不属于公开资料。

## 能力边界

源码包含组织/项目/环境、API 与契约、路由策略、应用凭证、审批发布、监控告警、SSO 和跨环境交付等实现。普通凭证轮换及撤销依赖再次审批发布；安全检查点/租约签发仍关闭，网关安全准入尚未接线，详见[安全操作说明](docs/deployment/credential-security-operations.md)。

本地或固定版本验证不等于新主干已安装，也不等于企业生产验收。目标环境的 TLS、HA、AMD64、容量/SLA、真实身份源与业务端点需要对应证据。
