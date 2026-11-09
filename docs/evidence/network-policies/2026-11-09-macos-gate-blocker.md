# network-policies 验收 — macOS 本机受阻记录(2026-11-09)

**结论**:5 套覆盖 gate(P3-01,`verifyNetworkPolicySuites`)在本机(macOS 15 / OrbStack)无法全绿 → 无法产出 `suites-result.json` → 后续场景(P3-02..P3-12)/ CUA(P3-11)/ 保护(P3-12)/ 验收 / `Scenario=NetworkPolicies` dotnet 测试均未执行。network-policies 流水线**留待 Linux runner 完成**。这是 macOS/OrbStack 的环境限制,不是产品 bug。

## 已完成的本地准备(可直接被 Linux run 复用或参考)
- **冻结 R' = `c946bbb`**:修 lock-race 死锁。c107e93 让 `withRuntimeLock` 改为 fail-closed(restore-target lock + 进程内 queue),旧 lock-race 测试依赖"同目标并发调用可排队"已不成立 → 确定性自死锁。新测试断言 fail-closed 契约(并发同目标被 `RESTORE_TARGET_LOCK_HELD` 拒绝、释放后干净重获)。提交于 main,**未推送**。
- **镜像** `webapi-enterprise-runtime:c946bbb`(sha256:`3ff7b5b5…`,User `10001:10001`,label `org.opencontainers.image.revision=c946bbb`);`.runtime/local-build/release.json` 同步。
- **policy-runner 3 项失败**:根因是**仓库路径含空格**(`…/WebAPI Platform Enterprise/…`)使 `process.execPath` 带空格,生成的 fake `docker` 桩 shebang(`#!${process.execPath}`)被截断为 `/Users/leo.cui/Library/Application` → `bad interpreter`。用**空格无关** node(`/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node`)即 4/4 全绿(已验证)。
- **驱动已备好**:`.runtime/network-campaign/run-gate.mjs`(R' + 空格无关 node + pnpm,`verifyNetworkPolicySuites`)、`run-gate-root.sh`(chown 目录→root→跑 gate→chown 回 leo.cui,含 `safe.directory` env 注入、清 stale `fixed-source`/`public`)。

## 剩余 blocker(macOS 特有,同一 run 内三类属主要求互斥)
以 root 跑 runtime 套件:298 测试,287 过,**11 挂**,分三类:

1. **4 个 chown 测试**(`tests/runtime/migration-evidence-archive.test.mjs`):`fs.chown(file,10001,10001)` 必须成功。归档内条目须 10001:10001(校验器 `migration-evidence-archive.mjs:83` 硬断言)——这是"证据须属运行时用户而非 root"的**安全不变量**,不能改成当前 uid。→ 需 **root 或 uid-10001**(501 下 EPERM)。
2. **1 个必需 docker 夹具测试**(`tests/network-policies/scenario.test.mjs` `real fixture echoes raw JSON and headers…`):验收 `validateFixedNetworkSuites:124` **强制**要求该行以 `✔` 出现。OrbStack 的 VM 只能 bind-mount **属主为宿主用户(501)** 的目录;以 root/10001 跑时夹具目录属主非 501 → `docker: … stat /tmp/webapi-network-fixture-…/source: permission denied`。→ 只能以 **501** 跑(17:18 的 leo.cui run 证实:该测试在 501 下通过,那轮仅 4 chown + 3 policy-runner 挂)。
3. **9 个 restore/volume 测试**:`restore-target-lock.mjs:59` 校验临时目录**每个祖先**属主 ∈ `{0, getuid}`。以 root 用 501 的 `TMPDIR`(`/var/folders/gz/…`,501 属主)命中该祖先 → `Unsafe restore parent owner`(501 下则祖先为 0/501,全过)。

**互斥结论**:root 能过 chown,但挂 docker 夹具(VM 挂载)+ restore 祖先;501 能过 docker 夹具 + restore,但 chown EPERM。同一 run 无法全绿 → 冻结 gate 无法在本机写出 `suites-result.json`。

## 完成 network-policies 所需
一个 **Linux runner**(容器或 CI;root 或专用 uid-10001,原生 docker,无 VM 文件共享限制):
1. 跑 5 套 gate(`verifyNetworkPolicySuites`,R' 或当时冻结版)产出 `suites-result.json`(runtime 套件在该环境下 chown=self、restore 祖先在容器 `/tmp` 属 root、docker 夹具走原生 docker)。
2. 本机(501)接场景 `runNetworkPolicyScenario` P3-02..P3-12、CUA(P3-11,15 项无头 Playwright)、保护(P3-12,只读前后快照)、`assembleNetworkPolicyEvidence`、`runNetworkPolicyAcceptance`、`Scenario=NetworkPolicies` dotnet 测试 → `docs/evidence/network-policies/verification.json`。

Linux 环境下上述三类属主要求不再冲突:chown 10001(=self,若以 10001 跑)、restore 祖先(root `/tmp`)、docker 夹具(原生 docker 无属主限制)可同时满足。
