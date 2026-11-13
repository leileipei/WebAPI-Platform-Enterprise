# 后端自动检查第二批：配置与验证

日期：2026-11-10。配置阶段输入提交 `536efe17ffa3db4229d152783096cdc58fe79747`，继承首批 `console` 检查。配置阶段遵循用户“先保留本地提交”的选择；后续取得明确授权后已发布并合入 PR #5，修正候选及 main 的实际检查均通过。主干保护未修改。以下本地验证记录保留原阶段范围，后续结果见文末。

## 配置范围

[后端工作流](../../.github/workflows/contracts-ci.yml)以固定矩阵生成 `contracts-domain`、`contracts-integration`、`contracts-gateway` 三个独立检查，使用 `ubuntu-24.04-arm`，`fail-fast: false`。起初每套 45 分钟；经首次远程超时分析和明确追加授权后，PR #5 阶段 Integration 调为 60 分钟。后续 PR #9 已在固定主干 `50d6fb9` 调为 90 分钟，Domain/Gateway 仍为 45 分钟；现行上限以固定版本工作流为准。某套失败不会取消其他套的 GitHub runner。

使用与首批相同的固定 checkout/setup-node 提交及 Node 24.22.1；默认 `contents: read`，不持久化 checkout 凭据。main PR、main push、手动触发，无路径过滤；并发组包含工作流与 PR/ref，不与首批相互取消。不设置失败忽略、自动重试、工件上传或部署。

执行入口仍是原 `scripts/check-contracts.sh`。它创建随机 owner、独立密码与 Compose 项目，在固定 SDK 镜像中复制只读源码到临时目录、锁定恢复、构建运行工具/迁移器后执行对应套件。EXIT 校验容器 owner 后清理本次精确项目与卷。取消/超时不保证 trap 执行，因此 GitHub 使用一次性托管 runner，不能外推为持久部署恢复证明。

## 平台与固定镜像

本次只读核实本机 Docker 为 Linux aarch64、Compose v5.5.0，三份已有镜像均为 linux/arm64；SDK 镜像配置读回 `DOTNET_SDK_VERSION=10.0.401`：

| 用途 | 固定摘要 |
| --- | --- |
| SDK | `e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317` |
| PostgreSQL | `b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24` |
| Redis | `858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499` |

保留原 Compose 中完整镜像引用，不换 digest、不用浮动 tag。本机宿主 SDK 为 10.0.403，与项目锁定版本不同，因此不使用宿主 dotnet 替代容器测试。GitHub [官方 runner 列表](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)提供公开仓库 `ubuntu-24.04-arm`；本机验证不能代表该 runner 已运行成功。

## 本次检查结果

| 项目 | 状态 |
| --- | --- |
| 工作流 YAML 与矩阵基本约束 | 已通过；配置前确认 contracts-ci.yml 缺失 |
| 原脚本语法 | `bash -n scripts/check-contracts.sh` 通过 |
| 原脚本、Compose、产品、现有测试与首批工作流 | 相对输入提交未修改 |
| Domain | 本次固定镜像 1251 通过、0 失败、0 跳过；脚本退出 0 |
| Integration | 本次固定镜像 2115 通过、0 失败、0 跳过；测试 34 分 21 秒，脚本退出 0 |
| Gateway | 本次固定镜像 555 通过、0 失败、0 跳过；测试约 5 分 8 秒，脚本退出 0 |
| 三套总计 | 3921 通过、0 失败、0 跳过；每套首次执行，无重跑 |
| 清理与原资源保全 | 三个精确 owner 项目的容器/卷/网络均为0，临时目录已移除；原186个容器、473个卷、36个网络标识均仍在，原11份文件SHA-256未变 |
| 独立审查 | Critical 0 / Important 0 / Minor 0；其要求的最终测试与保全核对已补齐 |

本地逐套执行、每套至多一次，失败不修改断言、不自动重跑。原始输出和资源身份基线保留在忽略的 `.superpowers/sdd/2026-11-10-contracts-ci-phase2/`，不上传公开工件，也不读取或公开生成的数据库密码。

上述本机运行使用已缓存镜像，现有脚本在可用时只读复制依赖种子，不能单独证明 GitHub 干净 runner 的表现。后续远程镜像拉取、依赖恢复、实际架构与检查结果另记如下。

本地 Integration 的 34 分 21 秒是测试本身耗时，另有恢复与编译时间；当时不能据此保证 45 分钟预算充足。迁移隔离集合覆盖 9 个测试文件，按既有要求禁止与其他测试集合并行；后续仅调整 CI 作业上限，未改变并行策略或产品超时。

资源标识保全仅说明本次核对未发现原标识消失，不等于已复验原环境健康、配置、镜像或业务数据。机器可读结果与各套原日志摘要保留在本地忽略目录的 `verification.json`，未将秘密或原始运行资料加入提交。

## 后续 GitHub 实际结果

发布范围为原四个提交及获追加授权的 `7694c4a`，合计五个提交、21 个文件，经 [PR #5](https://github.com/leileipei/WebAPI-Platform-Enterprise/pull/5) 合入 main `3774ccc`。新增 P2 回归使 Integration 从本地基线的 2115 增至 2128；历史 3921 项结果保留，不回填成新版本结果。

| 运行 | 实际结果 |
| --- | --- |
| [首次 Contracts CI](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42820580697) | Domain 1251、Gateway 555 通过；Integration 超过 45 分钟作业上限被取消，无完整测试汇总，不算通过 |
| [修正候选 `7694c4a` 的 PR 运行](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42828796227) | Domain 1251、Gateway 555、Integration 2128 通过，0 失败/跳过；Integration 测试 55 分 9 秒 |
| [main `3774ccc` 的 push 运行](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/42839002248) | 同三套分别 1251、555、2128 通过，0 失败/跳过；Integration 测试 52 分 44 秒 |

后两次均在 `ubuntu-24.04-arm` 实际执行；作业各自包含准备、构建、测试和清理，Integration 均在 60 分钟上限内完成。后端每次合计 3934 项，连同对应 Console 321 项各为 4255 项；不同轮次不累加。完整日志和源 SHA/事件核对结果保存本地，不公开原始运行目录。

## 仍未执行的动作

本批及后续 CI 整合不执行固定端口 E2E、G14 故障、性能实验、安全准入启用或 4192 升级。原目录文档、历史失败与暂停状态保持不变。GitHub/Ubuntu ARM64 已实际运行通过；2026-11-11 只读确认三项后端检查及 `console` 已列为 main 必需检查，本轮未写入规则。AMD64 和目标环境验收仍未由此获得证明。

设计：[第二批设计](../superpowers/specs/2026-11-10-contracts-ci-phase2-design.md)。计划：[第二批实施计划](../superpowers/plans/2026-11-10-contracts-ci-phase2.md)。首批记录：[前端检查](console-ci-phase1.md)。
