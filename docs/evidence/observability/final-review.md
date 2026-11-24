# 通用核心 E2E 凭证清理独立审查

本轮独立复审覆盖通用核心 E2E 退出清理及回归。上一轮三源代码复审保存在 Git 提交 `bd51bcf` 的同名文件。三源业务、故障与浏览器回执在本轮重新生成。下表的原生退出和代码复跑证据位于[本机核验报告所在目录](../../../deliverables/p2-core-cleanup-20261124-9b20036/core/independent-review.md)。

| 审查信息 | 值 |
|---|---|
| 审查人 | Codex 独立验证代理 `/root/core_live` |
| 复核时间（UTC） | 2026-11-24T03:20:44+00:00 |
| 比较基线 | `bd51bcf44a35bb3315cf1a48d571ce11d0bdab0d` |
| 被测产品 | `9b20036e6c7b945da1990b18f115a1118d1737a6` |
| 产品树 | `cf0d4274cb29bb09b3cdeae07dae578b9bcb9f2c` |

## 结论

本机两个随机 Docker 项目中，官方入口 `bash scripts/e2e.sh` 均在返回调用方前清除 4 个首次改密密码及 6 个基础凭证文件。成功路径退出 0，SDK 通过 4 项，原生故障检查通过 7 项。失败路径在真实首次改密后收到验证适配器返回的 37，官方入口保留退出码 37。两次退出时本项目容器、网络、卷均已清零，临时目录都保留了可校验的 `context.json`。本次修复审查没有阻断项。

| 本机原生路径 | 调用方收到退出码 | 换密文件，生成后 → 原生退出后 | 基础凭证，生成后 → 原生退出后 | 退出后容器 / 网络 / 卷 | 核心结果 |
|---|---:|---:|---:|---|---|
| 成功 | 0 | 4 → 0 | 6 → 0 | 0 / 0 / 0 | SDK 4 通过、0 跳过，故障 7 通过 |
| 确定性失败 | 37 | 4 → 0 | 6 → 0 | 0 / 0 / 0 | 首次改密完成后触发，SDK 与后续故障场景尚未开始 |

## 代码审查

- 对固定 Git 对象核实了被测提交的直接父提交、树及三个变更文件。核对 `scripts/prepare-e2e.mjs` 中四个实际身份与 `scripts/e2e.sh` 的清理名单一致。
- 新建项目目录使用独占创建和 0700 权限。在首次写入机密前安装 EXIT 处理。处理函数保存主流程退出状态，单独接收 Compose 与文件清理错误，即使 Compose 失败也继续逐名删除。主流程已有错误时优先返回原码，成功时返回清理状态。
- 删除范围由当次项目目录和十个确切文件名限定。回归同时保留本项目相似名称文件、另一项目同名文件。工作流已登记新增用例，固定提交比较的 `git diff --check` 与两个版本的 `bash -n` 均通过。
- 将同一份产品新增回归分开加载到基线与产品的原始脚本。基线 4 项失败，产品 4 项通过。范围覆盖正常结束、部分改密后退出、主流程与 Compose 同时失败，以及成功后 Compose 拆卸失败。

## 原生验证与证据顺序

两个独立浅克隆分别检出被测提交并离线构建控制台。克隆中的官方 E2E 脚本逐字节匹配 Git 对象，SHA-256 为 `984179049fd1f50182e5f719b7b23f66d353e49286b4c2a76f406ff8482a020e`。临时 Compose 配置使用本机已有的固定摘要镜像、只读 NuGet 种子卷与空包源，实现隔离构建。两次串行使用预检空闲的 `10.251.241.0/24` 和 `127.0.0.1` 端口 4182、4186、4187、5092。每次执行前均查实本项目名下资源为零。

1. 官方 `prepare-e2e.mjs` 对四个账户逐个完成首次改密，再写入上下文。适配器在随后第一条 Compose 命令处核验四个常规文件及四条对应的文件引用，仅采集文件名和计数。
2. 成功项目 `webapi-enterprise-e2e-1795490201-2519` 将命令转发给本机 Docker，实际 SDK 测试与七项故障检查均走官方实现。本轮重新写出了场景、故障、账户凭证轮换三份产品回执。
3. 失败项目 `webapi-enterprise-e2e-1795490362-29538` 在同一观察点向官方脚本返回 37。适配器未执行该次控制面重建，原生 EXIT 随后自行执行 Compose 拆卸与文件清理。
4. 调用方等候原生 Bash 退出后，先记录目录文件及 Docker 项目标签对应的剩余容器、网络、卷，再独立留存经白名单校验的非敏感上下文。外层最后才移除自己新建的运行目录。受测密码采用目录元数据取证，全程未将密码值写入验收回执。

## 证据定位

| 内容 | 相对于本机核验报告的文件 |
|---|---|
| 汇总及两次清理回执 | `core-live-summary.json`、`native-success-cleanup.json`、`native-failure-cleanup.json` |
| 成功与失败原始日志 | `success/evidence/runner.log`、`failure/evidence/runner.log` |
| 两次外层清理前观察 | `success/evidence/native-exit-observation.json`、`failure/evidence/native-exit-observation.json` |
| 两次改密观察 | 两个 evidence 目录中的 `after-first-change.json` |
| 成功路径三份产品回执 | `success/evidence/core.json`、`success/evidence/faults.json`、`success/evidence/rotation.json` |
| 非敏感上下文 | 两个 evidence 目录中的 `retained-context.json` |
| 代码独立复跑 | `review/product-source-review.json`、`review/product.diff`、`review/baseline-cleanup-fixture.tap`、`review/product-cleanup-fixture.tap` |
| 文件校验 | `core-live-evidence.sha256` |

结论适用于所列产品 SHA、两次本机 Linux Arm64 容器运行和具体失败点。机器回执分别记录了 SDK 与故障覆盖、文件清除及退出状态，可用于复核打包后的副本。
