# 后端自动检查第二批：配置与验证

日期：2026-11-10。输入提交 `536efe17ffa3db4229d152783096cdc58fe79747`，继承首批 `console` 检查。本批遵循用户“先保留本地提交”的选择，**不推送、不创建 PR、不修改主干保护**。

## 配置范围

[后端工作流](../../.github/workflows/contracts-ci.yml)以固定矩阵生成 `contracts-domain`、`contracts-integration`、`contracts-gateway` 三个独立检查，使用 `ubuntu-24.04-arm`，每套 45 分钟，`fail-fast: false`。某套失败不会取消其他套的 GitHub runner。

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

本机使用已缓存镜像，现有脚本在可用时只读复制依赖种子。因此 GitHub 干净 runner 的镜像冷拉取、无种子 NuGet 恢复、45 分钟预算和本仓库 ARM runner 实际可用资格仍须远程执行确认。

Integration 的 34 分 21 秒是测试本身耗时，另有恢复与编译时间，已经占用较多预算。保持当前45分钟配置，首次远程运行须重点核对全程耗时，不据本机结果保证预算充足。迁移隔离集合覆盖9个测试文件，按既有要求禁止与其他测试集合并行；本批未改变并行策略或产品超时。

资源标识保全仅说明本次核对未发现原标识消失，不等于已复验原环境健康、配置、镜像或业务数据。机器可读结果与各套原日志摘要保留在本地忽略目录的 `verification.json`，未将秘密或原始运行资料加入提交。

## 尚未发生的动作

本批不执行固定端口 E2E、G14 故障、性能实验、安全准入启用或4192升级。原目录文档、历史失败与暂停状态保持不变；没有 GitHub/Ubuntu ARM64 实际运行或主干必需检查已启用的结论。

设计：[第二批设计](../superpowers/specs/2026-11-10-contracts-ci-phase2-design.md)。计划：[第二批实施计划](../superpowers/plans/2026-11-10-contracts-ci-phase2.md)。首批记录：[前端检查](console-ci-phase1.md)。
