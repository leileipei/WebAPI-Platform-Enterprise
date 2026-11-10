# 当前项目状态与证据入口

源码与仓库治理复核日期：2026-11-11。源码基线：`3774ccc7fd97a59ff4851d3a9f55179c4635c61f`（本地 `main` 与 GitHub `main` 一致）。原2026-11-10对`7626115c769ae0926d38109ace46b2f5017bfcc3`的本机验证按历史固定身份保留。本页是固定日期的事实索引；后续代码、安装或验证变更须重新核对。文档修正和读取既有CI结果均不产生新的产品运行验收。

## 源码、验证、安装分别记录

| 层次 | 本次核实的事实 | 结论边界 |
| --- | --- | --- |
| 源码 | G01–G13 相关实现已通过 `57f2ef2` 合入；主干推进至 `3774ccc`，含PR #5的CI及审计/outbox隔离修复 | 合入不代表所有安全路径已接线 |
| 普通凭证轮换/撤销 | 新旧 Key 并存与旧 Key 撤销通过两次审批发布生效 | 工作配置撤销不改写旧快照，不承诺即时拒绝 |
| 安全组件 | 账本模型、签名/验证、ACK 验证、网关安全状态组件及测试已存在 | 生产签发、ACK/当前证明端点、网关安全准入尚未接线 |
| G14 普通发布故障 | 最小 3 项 + 扩展 4 项共 7 项通过；四类 reviewed-evidence 记录 4/4 通过 | 属于本机隔离验证，不证明安全准入已启用或生产容灾通过 |
| 最新网络策略交付 | 固定 `c946bbb`，4540 通过/0 失败/0 跳过；SDK E2E 1/0/0 | 属于该固定版本的五组测试，不是对当前主线的本机全量重跑 |
| GitHub CI | 固定`3774ccc`的`console`、`contracts-domain`、`contracts-integration`、`contracts-gateway`四项检查均为`completed/success` | 本轮读取既有GitHub运行结果；不代表新的SDK、页面或生产验收 |
| 4192 本机安装 | 最新网络策略记录明确 `originalInstallation: not-upgraded` | 本次未操作或重新核对运行中的版本、镜像、工具、数据及健康 |
| 生产验收 | 仓库仍明确目标环境、企业 TLS/网络、HA、AMD64、容量/SLA 等另验 | 不宣称生产就绪 |

## 安全能力的直接源码依据

- [`ApplicationService.SaveCredentialAsync`](../../src/WebApi.Infrastructure/Applications/ApplicationService.cs) 修改凭证工作配置、修订与撤销时间，不等于网关立即换代。
- [`ApiKeyMiddleware`](../../src/WebApi.Gateway/Security/ApiKeyMiddleware.cs) 从当前运行 generation 读取凭证状态、有效期及 API 授权。
- [`CredentialRotationFlowTests.cs`](../../tests/WebApi.EndToEnd.Tests/CredentialRotationFlowTests.cs) 第 33–38 行验证：保存撤销后旧 Key 仍为 200，再次发布后旧 Key 为 401。
- [`SecurityLeaseIssuer`](../../src/WebApi.Infrastructure/CredentialSecurity/SecurityLeaseIssuer.cs) 的签发入口固定返回 `503 security_evidence_unavailable`。
- [`GatewayApp`](../../src/WebApi.Gateway/GatewayApp.cs) 未注册或调用 `SecurityStateStore`；现有就绪接口只核对配置 generation。
- [`P2-4 独立评审`](../evidence/governance-merge/p2-4-crypto-review.md) 的 PASS 是附条件的合并门禁；签发、独立见证、恢复与网关接线须完成后再做同深度评审。

因此，历史台账“G01–G13 已关闭”只保留其合并与评审事实，不能据此推导“即时撤销、正式轮换硬截止已上线”。修正后的[安全操作手册](credential-security-operations.md)以当前运行路径为准，原台账、评审与原始证据保持不变。

## 固定版本验证入口

| 证据 | 内容 |
| --- | --- |
| [需求台账及 G 系列补记](../evidence/work-package4/requirements-status.md) | 454单元中，按已有2026-11-10复验记录汇总为36已关闭、40部分、18缺失、360待复验；各项分别绑定原固定源码，不能计算当前项目完成率 |
| [G14 故障矩阵](governance-g14-fault-matrix.md) | 普通发布故障、全部节点 ACK、超时后显式 retry、worker 恢复顺序 |
| [G14 最小子集](../evidence/governance-merge/g14-minimal.json) / [扩展矩阵](../evidence/governance-merge/g14-matrix.json) | 3 项与 4 项，不重复累计为两套 7 项 |
| [SSO](../evidence/sso/verification.json) / [设置](../evidence/settings/verification.json) / [流量策略](../evidence/policies/verification.json) | 各自记录 `complete: true`；各版本独立解释 |
| [2026-11-10 网络策略交付](../evidence/network-policies/2026-11-10-c946bbb/delivery-index.md) | 固定代码、镜像、独立 owner、五组测试、15 项页面操作及 SDK E2E |
| [网络策略工件清单](../evidence/network-policies/2026-11-10-c946bbb/delivery-manifest.json) | 2026-11-10历史只读核验记录为45项SHA-256全部匹配；本轮没有重新生成或修改原证据 |
| [G13 备份演练](governance-g13-upgrade-rehearsal.md) | 临时 seeder 构造五事件链，验证 dump/restore 后链一致；不代表生产写链或检查点引导已运行 |

2026-11-10历史额外检查：源码基线 `7626115` 的控制台测试 321 通过、0 失败、0 跳过；TypeScript `tsc --noEmit` 通过。测试首次因回环监听权限受限出现 EPERM，允许测试使用本机回环后全量通过；未修改用例或断言。该轮没有重跑后端全量、G14故障、真实SDK或页面操作验收。2026-11-11本轮仅核对固定源码、台账汇总及线上仓库治理，不重复声明这些运行验收。

## GitHub 仓库治理（2026-11-11在线核验）

- 默认分支 `main`；开放 PR 0、开放 Issue 0。
- 已跟踪[Console CI](../../.github/workflows/console-ci.yml)和[Contracts CI](../../.github/workflows/contracts-ci.yml)，均在面向`main`的PR、推送及手动触发运行，无路径过滤。当前固定主线的四项检查全部通过，分别见[Console运行](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839001626)和[Contracts运行](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839002248)。
- `main`分支保护已启用：必须PR、上述四项检查、同步最新主干、解决未结讨论，管理员同样受约束，禁止强推及删除。独立批准人数暂为0，避免单人维护自批阻塞；[保护管理接口](https://api.github.com/repos/leileipei/WebAPI-Platform-Enterprise/branches/main/protection)已成功读回核验。原2026-11-10连接的403是历史权限记录，不能代表当前规则不可配置。
- 远端保留4个分支：`main`、通知恢复桥、性能基线和共享夹具。18个完整合并分支已清理，另5个已吸收分支保存为`archive/2026-11-11/*`标签后清理；原提交及本地工作区保留。恢复桥与实验基线不合入当前产品。
- Releases 列表为空；tag 与正式 Release 是不同交付对象。
- [自动检查与主干保护提案](github-quality-gates-proposal.md)保留历史方案，并补记四项CI及保护规则的实际落地结果；其余交付门禁仍分别验收。

## 历史文档的阅读方式

- [架构评审](../../architecture-review.md)保留 2026-11-07 基线和局部补记，顶部有本次纠偏说明；其中旧状态不能覆盖本页引用的新证据。
- [本机运行状态历史](current-local-status.md)与[早期交付索引](current-local-delivery-index.md)记录各自安装当时的事实，不能证明今天的运行健康。
- 早期交付索引引用的三个 2026-10-05 ZIP 在当前工作区缺失；原链接保持不变，本次未恢复文件或把它们标成可用。交付包恢复须另查可验证备份。
- 后续实际安装应分别固定源码提交、镜像摘要、维护工具版本及现场核验时间；不得只把新提交号写入旧安装记录。

## 下一阶段尚待完成

1. 按当前主线逐项复核历史台账的18项缺失、40项部分及360项待复验；保留旧固定版本记录，不以合并或CI通过批量关闭需求。
2. 四项CI和主干保护已落地；正式Release尚未发布，构建签名及生产发布/迁移门禁需另行提供验收记录。独立批准人数调整仍须匹配真实协作者和评审能力。
3. 安全签发、独立见证、恢复与网关准入链路需单独设计、实现和评审；当前关闭状态保持不变。
4. 目标环境容量、数据库全局锁竞争、部署恢复和企业业务验收需另立固定版本执行范围；第2包现有暂停及历史 unknown 不因本次文档修正而解除。
