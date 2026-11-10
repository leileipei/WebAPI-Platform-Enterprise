# GitHub 自动检查与主干保护提案

原提案日期：2026-11-10。原输入基线：`7626115c769ae0926d38109ace46b2f5017bfcc3`。状态：**四项CI和main保护已落地，2026-11-11读回核验**。下方可选路径与实施建议保留为原提案历史，不表示这些已落地事项仍待选择。

## 实际落地状态（2026-11-11）

- 固定主线`3774ccc7fd97a59ff4851d3a9f55179c4635c61f`包含[Console CI](../../.github/workflows/console-ci.yml)与[Contracts CI](../../.github/workflows/contracts-ci.yml)，分别在`ubuntu-24.04`和`ubuntu-24.04-arm`执行；Node固定`24.22.1`，pnpm固定`11.25.0`，Action引用固定提交。
- `console`、`contracts-domain`、`contracts-integration`、`contracts-gateway`四项实际检查均为`completed/success`。记录见[Console运行](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839001626)和[Contracts运行](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839002248)；本轮读取已有结果，没有重新触发业务验收。
- 实际作业超时为前端15分钟、Domain/Gateway各45分钟、Integration60分钟；这是CI作业预算，不改产品超时或测试断言。
- `main`已要求PR、四项必需检查、同步最新主干及解决审查讨论；管理员同样受约束，禁止强推和删除。独立批准人数暂为0，待有可参与的独立维护者时再提高。
- [保护规则接口](https://api.github.com/repos/leileipei/WebAPI-Platform-Enterprise/branches/main/protection)已成功写入并读回。原2026-11-10连接只读/403的限制作为历史记录保留，不代表当前管理权限。
- 正式Release列表仍为空；构建签名、生产发布/迁移门禁、目标环境与业务验收不由本次CI和分支保护结果替代。

本提案用于把现有验证脚本接到独立 GitHub runner，不改产品行为、测试断言、数据库锁策略或原4192部署。不把 CI 通过视为 SDK/浏览器、目标环境或生产验收。

## 原提案的可选路径

| 路径 | 首批交付 | 取舍 |
| --- | --- | --- |
| A：分两批落地（建议） | 第一批前端测试与构建；第二批三个后端套件，分别跑通后加入必需检查 | 可先建立已知可运行的门禁；后端 runner/镜像适配单独验证，首批绿不代表后端绿 |
| B：首批四类全部接入 | 前端 + Domain/Integration/Gateway 一起适配并验收 | 起步覆盖更广；固定 SDK 镜像、锁定恢复、磁盘及 Linux runner 差异集中在同一轮处理 |

两条路径均先验证再启用强制规则；安全准入、固定端口 E2E、性能实验、4192 升级不纳入本提案。最终实施设计须在选择范围后确认。

## 建议的稳定检查名称与实际入口

| 检查名 | 执行入口 | 首次 runner 建议 |
| --- | --- | --- |
| `console` | `pnpm --dir console install --frozen-lockfile` 后执行 `scripts/check-console.sh` | `ubuntu-24.04` |
| `contracts-domain` | `scripts/check-contracts.sh domain` | `ubuntu-24.04-arm` |
| `contracts-integration` | `scripts/check-contracts.sh integration` | `ubuntu-24.04-arm` |
| `contracts-gateway` | `scripts/check-contracts.sh gateway` | `ubuntu-24.04-arm` |

GitHub [runner 文档](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)列有公开仓库 Linux ARM64 runner。后端首次选择 ARM64 是复用现有验证架构的建议，不表示当前固定镜像已在该 runner 实测成功；需先验证镜像架构、Docker Compose、锁定 NuGet 恢复和可用磁盘。AMD64 验收另记，不通过更换镜像摘要或启用浮动 tag 静默绕过。

Node 应选择满足项目 Node 22+ 要求的固定维护版本，pnpm 固定版本；具体版本与安装方式在实施时核实并写入配置。GitHub Actions 引用固定到经官方仓库核对的提交，依据 [GitHub 安全引用说明](https://docs.github.com/en/actions/reference/security/secure-use)。这些安装和下载发生在独立 runner，不对现有本机环境做全局安装。

## 触发、权限与资源边界

- 在针对 `main` 的 `pull_request`、`push` 到 `main` 及手动触发执行；不使用具有额外权限的 `pull_request_target` 执行外来代码。
- 默认 `contents: read`，checkout 不持久化凭据；不需要产品密码、节点秘密、部署密钥或本机目录。
- 四类检查以稳定名字独立报告。后端每个 job 使用新 runner，矩阵设置 `fail-fast: false`；同一 PR 新版本可以取消旧轮次。
- 前端起步超时建议 15 分钟，后端每套 45 分钟；首次实际耗时超过预算即记录失败并分析，不靠删除断言或放宽产品超时换取通过。
- 不以路径过滤跳过已配置的必需检查；工作流被跳过、取消或依赖不可用不应成为虚假的成功。
- 后端复用 `check-contracts.sh` 的随机 owner、独立密码与精确归属清理；不调用旧固定项目的 `check.sh`，不扫描删除其他 Docker 资源。
- runner 取消、强杀或超时不保证 shell EXIT 清理完成；依靠一次性托管 runner 生命周期隔离，不宣称取消后的业务恢复已被验证。

## 输出与证据

公开仓库的 CI 日志和工件须按公开材料处理。只保留必要测试摘要、构建结果与测试数；不能整包上传 `.runtime`、密码、数据库转储、节点身份、配置快照、Cookie 或请求头。具体 TRX/日志工件需先验证不含随机秘密和敏感载荷，未完成这一步前不自动上传原始运行目录。

首轮需分别核对：运行源 SHA/事件、runner 架构、实际工具版本、每套通过/失败/跳过数、构建退出码与清理结果。不能把当前本机的 321 或历史 4540 直接写成 CI 通过数。

## 原提案的主干保护建议（实际启用结果见上方）

| 设置 | 建议 |
| --- | --- |
| 目标 | 仅 `main`，新规则先以实际可见且已跑通的检查名配置 |
| 合并入口 | 要求 PR，禁止直接推送、强推和删除；常规改动经过已选检查 |
| 必需检查 | A 首批只有 `console`；第二批各套跑通后再加入三个 `contracts-*`。B 全部跑通后加入四项 |
| 分支新鲜度 | 要求与最新主干同步，并重新验证合并候选 |
| 讨论 | 要求解决未结审查讨论 |
| 批准人数 | 核实有可参与的独立维护者后再要求至少 1 人；作者不能批准自己的 PR，不把单人仓库配置成不可合并 |
| 管理权限 | 明确可审计的应急处理负责人，避免长期默认绕过；具体人员须由维护者确认 |

原2026-11-10提案记录：当时GitHub连接只能读取部分仓库状态，保护管理接口返回403，且没有可用的规则写入工具。2026-11-11已通过具有管理员权限的CLI启用远程规则并读回核验，完成状态见上方；后续规则变更仍须重新在线核验。

## 原提案的验收与交付顺序

1. 选择 A 或 B，确定工具固定版本、runner 适配及实现设计；在可审阅分支中准备工作流和必要的门禁测试。
2. 做本地静态检查及与改动对应的验证；本地解析通过不等于 GitHub 实际通过。
3. 经明确的远程发布范围确认后推送分支并创建 PR；跟踪实际 Actions 到完成，保留首次失败。
4. 真实运行通过后再启用所选必需检查与主干规则，读回核对；不提前锁入尚未出现的检查名。
5. 更新[当前状态](current-project-status.md)，同时列出“配置已写入”“GitHub 已运行”“规则已启用”的分别结果。业务 E2E 与生产验收状态保持独立。
