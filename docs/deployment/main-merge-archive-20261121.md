# PR #19 至 #23 合并与主线复跑归档

核对日期：2026-11-21（北京时间）。本页按实际合并提交与 `push` 触发的主线运行归档。PR 运行、本机测试和主线复跑分别计数；历史失败不被后续成功替换。原始 API 回执和作业日志保存在本机封存目录。

## 合并身份

| PR | 受测分支头 | 合并提交 | 合并时间（北京时间） |
| --- | --- | --- | --- |
| [#19](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/19) | `7d0554253369027cd904f80b3b1fb9f9e8338d45` | `023a16609fa4ecf7d8b3c97f90d415a5cb1c842e` | 2026-11-20 03:00:14 |
| [#20](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/20) | `9e622f7fd305008ea668ae5c939784475405378b` | `14caa36ab3b419eee91451d8c8357a4a00464b13` | 2026-11-20 13:08:49 |
| [#21](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/21) | `1cef73b93e7813a2f4eceb8470b0e9586b241466` | `b569addb6bec037119be0ddd3c1976c24832abdb` | 2026-11-20 14:48:55 |
| [#22](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/22) | `6b0cf2c9be7a03d8e03042fb5188f5db4a438b0f` | `1ead23c6f8f2778f1547264533622a759962b729` | 2026-11-21 14:33:31 |
| [#23](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/23) | `fb96ffbfa88fb08ab0d64e71578111882a486e01` | `3b8a672a99d84e17ea9ff887b35267bdaf326eba` | 2026-11-21 21:07:31 |

各受测分支头均已包含在 `3b8a672` 主线历史，五次候选与各自合并提交的文件树均已重新核对一致。PR #19 的旧归档另记临时合并树 `bd6e8e4` 与候选一致；该临时对象当前本地不存在，本次仅保留其历史声明，不作为本次新核验结果。

## 主线自动复跑

| PR / main | console | domain | gateway | integration | 合计 | 主线运行 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| #19 | 452 | 1,426 | 631 | 2,632 | **5,141** | [Console 44593723799](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44593723799) · [Contracts 44593723769](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44593723769) |
| #20 | 461 | 1,426 | 631 | 2,632 | **5,150** | [Console 44679058241](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44679058241) · [Contracts 44679058592](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44679058592) |
| #21 | 463 | 1,426 | 631 | 2,632 | **5,152** | [Console 44690467114](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44690467114) · [Contracts 44690467107](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44690467107) |
| #22 | 463 | 1,426 | 639 | 2,671 | **5,199** | [Console 44876113139](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44876113139) · [Contracts 44876113132](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44876113132) |
| #23 | 468 | 1,426 | 639 | 2,671 | **5,204** | [Console 44907282212](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44907282212) · [Contracts 44907282252](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/44907282252) |

PR #19–#23 的四项必需检查均首次执行成功，失败与跳过均为 0。`console` 统计包括 Python、运行工具 Node 测试和前端 Node 测试：分别为 94 + 19 + 339、94 + 28 + 339、94 + 30 + 339、94 + 30 + 339、94 + 35 + 339。类型检查与构建另行通过。PR #23 的 Integration 于 2026-11-21 22:17:27（北京时间）完成，最终日志已封存并核对 2,671 项通过。

## 原资料采用与保留

- PR #19 的旧未提交补记来自基线 `023a166` 的 `production-readiness-20261120` 工作树。本机串行常规测试 5,122 项及额外运行工具 208 项保留原口径；首轮并行出现的登录 503 与 PostgreSQL 删库超时原件保留，不能由后续成功倒推具体原因。GitHub 的 5,141 项与本机 5,330 项不相加。
- `merge-ci-repair-20261114` 工作树基线 `4374afd` 的未提交台账大部分已由后续主线收录。仅补充尚未公开的[数据库销毁与登录准备耗时记录](integration-fixture-cleanup-login-timing-20261118.md)，保留 `8dd156a` 的 149 项样本、两轮诊断及首轮准备失败。原目录 25 项历史清单校验通过，5,528 个文件已另行归档并读回核对；不把旧分析的“下一轮”当成当前待实施计划。
- 状态候选 `829ba65` 的 PR #22 复跑和 P12 收件记录与 P12 专属会话的 [PR #24](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/24) 核对。P12 修订由该 PR 于 2026-11-21 22:19:19（北京时间）合入 `323e0dc9f4066570b0c050097bb318a288ba758a`，该 PR 四项检查的原始日志合计 5,204 项通过，主线复跑另行跟进。本分支按该合入结果接续且完整保留其 26 行新增正文；原候选提交和含私人批准信息的未提交副本均保留在本机。
- 主线已有的项目计数验收段落与历史 454 项台账保持原字节。三项隔离验收和其余 451 项待复验的口径不变；与旧 36/40/18/360 状态不相加。

## 当前门槛与备份

本次只读完整分支保护：四项必需检查为 `console`、`contracts-domain`、`contracts-integration`、`contracts-gateway`；严格同步、管理员约束和讨论解决均开启，禁止强推及删除 main，要求批准人数为 0。规则读取不改变配置。本机源码副本按同一测试入口执行，主线和 PR 的运行结果分别记录。

本轮本机复验固定 `3b8a672`，Console 468（Python 94、运行工具 35、前端 339）、Domain 1,426、Gateway 639、Integration 2,671，合计 **5,204 项通过、0 失败、0 跳过**，类型检查和构建通过。4,588 份冻结输入摘要一致；本分支仅追加文档，最终非文档输入另与该测试快照核对。主机使用已有 Node 24.22.3 / pnpm 11.25.0，托管工作流仍固定 Node 24.22.1 / pnpm 11.25.0。

首轮控制台因应用内置 Node 拒绝加载不同签名身份的 Rollup 原生模块而停止；使用已有同版本独立 Node 后通过。汇总时发现初始运行清单漏列 PR #23 新增的 5 项运行器防护测试，已从同一冻结源码补跑，当前工作流的五份运行工具测试文件各计一次。首轮失败、遗漏清单及补齐回执单独保留，后续通过不覆盖原日志。

本机检查前后的 537 个容器、1,167 个卷和 137 个网络身份全部保留，三个随机 owner 的自有测试资源已清理。本次本机检查与 GitHub CI 均不代表原安装升级、客户现场或生产验收。

本地主工作树从 `b569add` 以 `--ff-only` 同步到 `3b8a672`；同步时该提交已经 GitHub 合并，主线复跑仍在进行。P12 合入后已再次快进至 `323e0dc`，本分支同样以该提交为底；这两次快进均保持主工作树干净。本次归档 PR 的最终同步提交、P12 及归档 PR 的主线复跑与清理回执保存在同一本机封存目录的最终报告中。

本机封存目录为 `deliverables/local-sync-status-20261121.c97oq15m/`（位于项目外层、未纳入仓库）。已保存各工作树 HEAD、分支、状态、索引、暂存与未暂存二进制补丁、改动原件及 HEAD 原件。20 个工作树快照稳定，Git bundle 在空仓库通过验证、导入和完整对象检查，补丁重放结果与原件摘要一致。

- Bundle：`git/all-refs-and-reflogs.bundle`，108,093,292 B，SHA-256 `7cd79c4cd05a29acbdff15d99f3b7bd3659ec14e859697d9fbcd579f51d8faef`。
- 两份 stash `c886bca7065b04d28ed956e7e585dfab2078b14a`、`1af7f31530dbba8d3253c3ce78f4bb0ca7ed4422` 保留，恢复仓库已核对对象。
- `feature/notification-recovery-bridge` 与 `perf/pkcs-baseline-20261016` 继续保留，原分叉及用途不变。
- PR #20–#23 的分支头均已合入且可从备份恢复。PR #20 经无活跃会话占用核对后，以精确头租约删除远端分支并读回确认；原工作树以 `9e622f7` detached 状态保留，核对全部跟踪文件摘要与忽略文件路径清单不变后删除本地分支引用。PR #21 和 #23 有活跃会话，PR #22 仍关联 P12 技术资格，三者分支继续保留。旧台账、P12 候选、现场/G14 和产品验收证据工作树均未清理。

公开资料仅包含结论与必要校验值。审批原文、OA 编号、身份及私人证据不进入本次提交。
