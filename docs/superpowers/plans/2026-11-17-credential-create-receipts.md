# 凭证创建幂等与秘密回执 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox syntax for tracking.

**Goal:** 同键创建只提交一次，秘密只在首次响应展示，回放重新验证权限。

**Architecture:** 在现有受审计事务中调用主线幂等执行器，持久化仅包含 CredentialDto 的元数据。控制台明确处理秘密不可再次获取的回执。

**Tech Stack:** 现有 .NET/EF Core/PostgreSQL、React/TypeScript/Node；不升级依赖。

**Spec:** `docs/superpowers/specs/2026-11-17-credential-create-receipts-design.md`

## Global Constraints

- 独立分支 `codex/credential-create-receipts-20261117`，旧治理原件与证据保持不变。
- 不启用环境绑定或安全签发，不修改数据库迁移，不放宽CI门槛。
- 实现/验证/推送/通过全部必需检查后合并，已由本次用户请求授权。

## Review Focus

- 并发不能出现两次秘密响应或两个凭证/回执/审计。
- 回放先重新授权，失效角色、只读/跨项目范围、禁用账号不能取得旧结果。
- 首次响应丢失和进程重启后只能取得元数据，不能持久化明文秘密。
- 同键跨操作者/应用与UTC等价请求的隔离和冲突语义须明确。
- 提交失败不留下凭证、回执、审计或应用修订；前端回放不能误报新秘密已展示。

## Task 1: 创建接口与元数据回执

**Files:** 新建 `src/WebApi.Infrastructure/Applications/CredentialCreateReceiptService.cs` 与 `tests/WebApi.Integration.Tests/CredentialCreateReceiptTests.cs`；修改 `ApplicationContracts.cs`、`ApplicationService.cs`、`ApplicationEndpoints.cs`、`ControlPlaneApp.cs`。

**Interfaces:** `CredentialCreateReceiptService.CreateAsync(Guid applicationId, CredentialCreateRequest request, CommandRequestContext command, ActorContext actor, CancellationToken ct)` 返回含可空秘密及 `SecretUnavailable` 的 `CredentialCreateResponse`；现有普通创建入口全部接此路径。

- [x] 基线现有凭证/幂等/兼容测试。
- [x] 写真实HTTP测试：并发、重启回放、冲突、缺失键、UTC规范化、权限/作用域/操作者隔离、回滚及秘密不落回执/审计/GET；在未实现时确认关键用例失败。
- [x] 实现元数据回执，保留现有锁、授权与状态校验，注入并接通入口。
- [x] 运行新增测试与相关基线，修复真实失败并保留日志。

## Task 2: 控制台回放状态

**Files:** `console/src/pages/Credentials.tsx`、凭证创建结果小组件及对应行为测试。

- [x] 先写首次成功/回放无秘密/错误结果的行为测试并确认失败。
- [x] 首次展示API Key，回放显示明确恢复提示，不隐式新建；保留轮换创建回调。
- [x] 完成控制台测试、构建及相关凭证界面验证。

## Task 3: 评审、完整门禁与合并

- [ ] 完整差异审查，独立评审并处理重要发现。
- [ ] 保留各次原始测试结果；四项必需检查与其中Python检查全部通过。
- [ ] 固定已验证提交，核对main和分支保护后合并；继续核对合并后main检查。
- [ ] 重新核对旧433项输入/G14状态/Git状态未变，仅清理本次拥有的临时测试资源。
