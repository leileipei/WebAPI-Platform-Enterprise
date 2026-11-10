# GitHub 自动检查与主干保护提案

日期：2026-11-10。两批配置及 P2 修复、Integration 作业上限修正已通过 [PR #5](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/5) 合入 main `3774ccc7fd97a59ff4851d3a9f55179c4635c61f`。修正后的 PR 及合并后 main 的四项检查均已通过；主干保护尚未启用。实际版本与发布状态统一见[当前状态页](current-project-status.md)。

本提案用于把现有验证脚本接到独立 GitHub runner，不改产品行为、测试断言、数据库锁策略或原4192部署。不把 CI 通过视为 SDK/浏览器、目标环境或生产验收。

## 原方案对比与已选路径

| 路径 | 首批交付 | 取舍 |
| --- | --- | --- |
| A：分两批落地（已选择） | 第一批前端测试与构建；第二批三个后端套件。两批已发布并完成 GitHub 验证 | 四套实际运行已通过，配置必需检查仍是独立待办 |
| B：首批四类全部接入（未选择） | 前端 + Domain/Integration/Gateway 一起适配并验收 | 起步覆盖更广；固定 SDK 镜像、锁定恢复、磁盘及 Linux runner 差异集中在同一轮处理 |

保持先实际验证、后启用强制规则的顺序；安全准入、固定端口E2E、性能实验、4192升级不纳入本方案。已实施设计与本地结果分别见[前端首批记录](console-ci-phase1.md)和[后端第二批记录](contracts-ci-phase2.md)。

## 已发布并实际通过的稳定检查名称与入口

| 检查名 | 执行入口 | 已验证的 runner |
| --- | --- | --- |
| `console` | `pnpm --dir console install --frozen-lockfile` 后执行 `scripts/check-console.sh` | `ubuntu-24.04` |
| `contracts-domain` | `scripts/check-contracts.sh domain` | `ubuntu-24.04-arm` |
| `contracts-integration` | `scripts/check-contracts.sh integration` | `ubuntu-24.04-arm` |
| `contracts-gateway` | `scripts/check-contracts.sh gateway` | `ubuntu-24.04-arm` |

GitHub [runner 文档](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)列有公开仓库 Linux ARM64 runner。本仓库已在上述托管 runner 完成修正候选和 main 的实际运行，包含固定镜像拉取、依赖恢复、构建和测试；一次成功不保证未来依赖可用性或容量。AMD64 验收另记，不更换镜像摘要或用浮动 tag 绕过。

实际配置固定Node24.22.1，前端另固定pnpm11.25.0；checkout v7.0.1及setup-node v7.1.0按官方仓库已核对的完整提交引用，具体SHA见首批记录。依据 [GitHub安全引用说明](https://docs.github.com/en/actions/reference/security/secure-use)，不用浮动Action标签。工作流的工具安装发生在独立runner；本轮未改本机全局工具或产品依赖。

## 触发、权限与资源边界

- 在针对 `main` 的 `pull_request`、`push` 到 `main` 及手动触发执行；不使用具有额外权限的 `pull_request_target` 执行外来代码。
- 默认 `contents: read`，checkout 不持久化凭据；不需要产品密码、节点秘密、部署密钥或本机目录。
- 四类检查以稳定名字独立报告。后端每个 job 使用新 runner，矩阵设置 `fail-fast: false`；同一 PR 新版本可以取消旧轮次。
- 前端作业上限 15 分钟，Domain/Gateway 为 45 分钟，Integration 为 60 分钟。首次 Integration 超过原 45 分钟上限而取消，保留该失败；经明确授权仅追加工作流一行修正，不改变断言、产品超时或迁移隔离。
- 不以路径过滤跳过已配置的必需检查；工作流被跳过、取消或依赖不可用不应成为虚假的成功。
- 后端复用 `check-contracts.sh` 的随机 owner、独立密码与精确归属清理；不调用旧固定项目的 `check.sh`，不扫描删除其他 Docker 资源。
- runner 取消、强杀或超时不保证 shell EXIT 清理完成；依靠一次性托管 runner 生命周期隔离，不宣称取消后的业务恢复已被验证。

## 输出与证据

公开仓库的 CI 日志和工件须按公开材料处理。只保留必要测试摘要、构建结果与测试数；不能整包上传 `.runtime`、密码、数据库转储、节点身份、配置快照、Cookie 或请求头。具体 TRX/日志工件需先验证不含随机秘密和敏感载荷，未完成这一步前不自动上传原始运行目录。

原本地前端 321 项、后端 3921 项属于配置阶段记录。P2 修复增加 13 个回归场景后，修正候选和 main 各自实际得到 Console 321、Domain 1251、Gateway 555、Integration 2128，合计 4255 通过、0 失败/跳过。main 的 [Console](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839001626) 与 [Contracts](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839002248) 由 push 触发，源码为 `3774ccc`；PR 与 main 的数量不相加。

首次超时运行、修正后 PR 运行和 main 运行分别保留，不能把取消轮次改标通过。PR Integration 测试耗时 55 分 9 秒，main 为 52 分 44 秒；完整作业另含准备和清理，均在 60 分钟上限内完成。实际 runner、步骤及完整日志已在本地忽略目录核对保存，不自动发布原始资料。

## 主干保护的具体建议（真实运行已完成，尚待配置）

| 设置 | 建议 |
| --- | --- |
| 目标 | 仅 `main`，新规则先以实际可见且已跑通的检查名配置 |
| 合并入口 | 要求 PR，禁止直接推送、强推和删除；常规改动经过已选检查 |
| 必需检查 | `console`、`contracts-domain`、`contracts-integration`、`contracts-gateway` 均有真实成功记录；当前必需检查仍未配置 |
| 分支新鲜度 | 要求与最新主干同步，并重新验证合并候选 |
| 讨论 | 要求解决未结审查讨论 |
| 批准人数 | 核实有可参与的独立维护者后再要求至少 1 人；作者不能批准自己的 PR，不把单人仓库配置成不可合并 |
| 管理权限 | 明确可审计的应急处理负责人，避免长期默认绕过；具体人员须由维护者确认 |

首次读取保护管理接口曾返回403；本轮未重试。未来远程启用须在有权限的连接或管理环境执行并读回核验，才能标“已启用”。本地整理不改变仓库权限或主干规则。

## 验收与交付顺序

1. 已完成：选择 A、两批工作流、本地验证、独立审查与限定范围发布。
2. 已完成：PR #5 首次真实运行及超时记录；取得额外明确授权后，仅将 Integration 作业上限改为 60 分钟。
3. 已完成：修正后四套 CI 全绿、合入 main、本地主分支同步及 main push 四套 CI 全绿。
4. 待办：确认主干规则和管理权限后，按四个真实检查名配置，并读回核验；本次资料更新不执行规则写入。
5. GitHub 运行与规则启用分别记录，资料整理公开范围、业务 E2E 和生产验收保持独立。
