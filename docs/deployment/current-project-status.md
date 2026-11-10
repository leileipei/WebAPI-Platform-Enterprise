# 当前项目状态与证据入口

复核日期：2026-11-10。产品源码基线及本次在线读取的 GitHub `main` 均为 `7626115c769ae0926d38109ace46b2f5017bfcc3`。当前独立目录另有本地 CI 提交 `536efe1`、`8b75524`；本次文档整理以 `8b75524` 为输入，未改变产品实现。本页是固定日期的事实索引；后续代码、安装或验证变更须重新核对。维护者已选择只保留本地提交；本次不推送、不创建 PR、不启用远程规则。

## 源码、验证、安装分别记录

| 层次 | 本次核实的事实 | 结论边界 |
| --- | --- | --- |
| 源码 | G01–G13 相关实现已通过 `57f2ef2` 合入；主干推进至 `7626115` | 合入不代表所有安全路径已接线 |
| 本地 CI 配置 | `536efe1` 完成 console；`8b75524` 完成三个 contracts-* 检查 | 配置和本地验证已完成；GitHub 尚未执行，也未成为主干必需检查 |
| 普通凭证轮换/撤销 | 新旧 Key 并存与旧 Key 撤销通过两次审批发布生效 | 工作配置撤销不改写旧快照，不承诺即时拒绝 |
| 安全组件 | 账本模型、签名/验证、ACK 验证、网关安全状态组件及测试已存在 | 生产签发、ACK/当前证明端点、网关安全准入尚未接线 |
| G14 普通发布故障 | 最小 3 项 + 扩展 4 项共 7 项通过；四类 reviewed-evidence 记录 4/4 通过 | 属于本机隔离验证，不证明安全准入已启用或生产容灾通过 |
| 最新网络策略交付 | 固定 `c946bbb`，4540 通过/0 失败/0 跳过；SDK E2E 1/0/0 | 该版本的五组测试，不能冒充本次对 `7626115` 的后端全量重跑 |
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
| [需求台账及 G 系列补记](../evidence/work-package4/requirements-status.md) | 原 454 单元仍为历史基线：33 已关闭、40 部分、18 缺失、363 待复验；不能计算当前项目完成率 |
| [G14 故障矩阵](governance-g14-fault-matrix.md) | 普通发布故障、全部节点 ACK、超时后显式 retry、worker 恢复顺序 |
| [G14 最小子集](../evidence/governance-merge/g14-minimal.json) / [扩展矩阵](../evidence/governance-merge/g14-matrix.json) | 3 项与 4 项，不重复累计为两套 7 项 |
| [SSO](../evidence/sso/verification.json) / [设置](../evidence/settings/verification.json) / [流量策略](../evidence/policies/verification.json) | 各自记录 `complete: true`；各版本独立解释 |
| [2026-11-10 网络策略交付](../evidence/network-policies/2026-11-10-c946bbb/delivery-index.md) | 固定代码、镜像、独立 owner、五组测试、15 项页面操作及 SDK E2E |
| [网络策略工件清单](../evidence/network-policies/2026-11-10-c946bbb/delivery-manifest.json) | 本次只读重算 45 项 SHA-256，全部匹配；没有重新生成或修改原证据 |
| [G13 备份演练](governance-g13-upgrade-rehearsal.md) | 临时 seeder 构造五事件链，验证 dump/restore 后链一致；不代表生产写链或检查点引导已运行 |

首次只读检查在产品基线 `7626115` 完成控制台 321 项与类型检查；最初因回环监听权限受限出现 EPERM，允许测试使用本机回环后全量通过，未修改用例或断言。随后两批 CI 准备有独立的固定输入和本地记录：

| 本地批次 | 实际结果 | 范围 |
| --- | --- | --- |
| [前端首批](console-ci-phase1.md)，提交 `536efe1` | 空依赖目录及空缓存安装73个依赖成功；321项通过，0失败/跳过；类型检查与Vite构建通过 | 本机macOS；保留SSR端口冲突日志及大chunk告警，未改测试或阈值 |
| [后端第二批](contracts-ci-phase2.md)，提交 `8b75524` | 固定SDK10.0.401镜像：Domain1251、Gateway555、Integration2115，合计3921通过、0失败/跳过；每套首次执行 | 本机Linux ARM64容器，已缓存镜像，按原脚本在可用时复制只读依赖种子 |

Integration测试本身耗时34分21秒，另有恢复与编译时间；GitHub冷拉镜像、无种子恢复、ARM runner可用资格与45分钟预算仍未验证。三套精确测试项目的容器/卷/网络及临时目录已清理；结束核对时原资源标识仍在、原11份文件摘要未变。该保全核对不能代替原环境健康或业务验收。

这些分批结果不能冒充一个新生产发布包；本轮没有重新执行G14故障、真实SDK E2E或页面操作验收。

## GitHub 仓库治理与本地配置

- 本次在线复核：GitHub `main` 仍为 `7626115`，Actions运行记录总数仍为0，分支接口 `protected: false`，规则集列表为空。
- 远程主干尚未包含这两批工作流；本地已有 `console-ci.yml` 与 `contracts-ci.yml`。因此“远程无CI”和“本地已准备CI”同时成立，不能宣称GitHub全绿。
- 首次检查开放PR与Issue均为0；本次整理未重新统计。更细的保护管理接口曾因连接权限返回403，本次未重试或修改设置。
- Releases 列表为空；tag 与正式 Release 是不同交付对象。
- [自动检查与主干保护方案](github-quality-gates-proposal.md)已选择A，两批本地实现与验证完成；远程发布、首次实际运行和规则启用均未执行。

## 历史文档的阅读方式

- [架构评审](../../architecture-review.md)保留 2026-11-07 基线和局部补记，顶部有本次纠偏说明；其中旧状态不能覆盖本页引用的新证据。
- [本机运行状态历史](current-local-status.md)与[早期交付索引](current-local-delivery-index.md)记录各自安装当时的事实，不能证明今天的运行健康。
- 早期交付索引引用的三个 2026-10-05 ZIP 在当前工作区缺失；原链接保持不变，本次未恢复文件或把它们标成可用。交付包恢复须另查可验证备份。
- 后续实际安装应分别固定源码提交、镜像摘要、维护工具版本及现场核验时间；不得只把新提交号写入旧安装记录。
- 此前位于主目录的八份文档修正已复制到本独立目录并更新上述状态；主目录原文件与未提交状态仍保留，不删除、不覆盖，也未混入两批历史CI提交。

## 下一阶段尚待完成

1. A方案两批本地准备已完成。只有维护者改变“仅保留本地”的决定后，才进入远程发布与实际GitHub运行；通过后再选择必需检查，重点核对Integration全程预算。
2. 主干保护策略须与真实协作者和评审能力匹配；不能把唯一作者无法完成的自我批准配置成永久阻断。
3. 安全签发、独立见证、恢复与网关准入链路需单独设计、实现和评审；当前关闭状态保持不变。
4. 目标环境容量、数据库全局锁竞争、部署恢复和企业业务验收需另立固定版本执行范围；第2包现有暂停及历史 unknown 不因本次文档修正而解除。
