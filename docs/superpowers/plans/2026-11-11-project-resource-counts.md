# Project Resource Counts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 在项目列表展示遵循当前账号读取权限的环境数、API 数和项目应用数。

**Architecture:** 在 GovernanceService 新增 ProjectsAsync，为分页项目生成独立 ProjectListItemDto。复用 CollectionAuthorizationQuery，在 SQL 中按权限和该页项目范围计算三类计数，前端直接展示响应字段。

**Tech Stack:** .NET 10、EF Core、PostgreSQL、React、TypeScript、xUnit。

**Spec:** docs/superpowers/specs/2026-11-11-project-resource-counts-design.md

## Global Constraints

- 项目应用数只统计 ProjectId 等于项目的可读应用。
- 有权限且集合为空返回 0，无相应读取范围返回 null，界面显示 —。
- 先筛选并分页，再对该页项目批量查询计数。
- 详情、保存和 scope-tree 继续使用 ProjectDto。

## Review Focus

- 仅有环境权限的账号应只得到可见环境数，项目级业务计数为空。
- 功能权限撤销及角色的组织限制应立即影响已有会话的计数。
- 重叠组织、项目和环境授权应避免重复计数。
- 停用业务祖先应使 API 与应用计数不可读，治理环境数仍遵循治理权限。
- 空页、分页截断与资源状态变化应有正确的 total、page、pageSize 和计数。

## Task 1 — 后端计数与权限回归

**Files:** src/WebApi.Contracts/Governance/GovernanceContracts.cs、src/WebApi.Infrastructure/Governance/GovernanceService.cs、src/WebApi.ControlPlane/Governance/GovernanceEndpoints.cs、tests/WebApi.Integration.Tests/ProjectResourceCountsTests.cs。

**Interfaces:** ProjectsAsync(Guid organizationId, ActorContext actor, int? page, int? pageSize, CancellationToken ct = default) 返回 Task<PageResult<ProjectListItemDto>>。ProjectListItemDto 保留 ProjectDto 的全部行字段，增加 int? EnvironmentCount、ApiCount、ApplicationCount。

- [x] 增加通过真实 HTTP 验证三项计数、空集合、共享应用排除、权限和范围隔离、停用祖先、重叠范围、分页及原有响应兼容性的集成测试，运行并观察现有接口缺失字段的失败。
- [x] 实现独立列表 DTO 和 ProjectsAsync。环境范围按 ProjectId 分组计数非空 EnvironmentId，API 与应用从已授权项目范围批量投影计数。端点调用新方法。
- [x] 运行 ProjectResourceCountsTests、GovernanceTests 和 CollectionAuthorizationQueryTests，确认权限语义与既有服务一致。

## Task 2 — 列表展示与交付验证

**Files:** console/src/pages/Governance.tsx。

**Interfaces:** 列表行直接读取 environmentCount、apiCount、applicationCount，使用现有 Table 的 0 与 null 展示规则。

- [x] 项目列表新增环境数、API 数、项目应用数三列。
- [x] 运行控制台现有测试及 TypeScript/Vite 构建。
- [x] 审查完整差异并记录测试结果，提交到 codex/project-resource-counts-20261111 分支。

## 执行说明

用户已确认设计与应用数口径，直接在当前隔离工作区执行。使用独立测试资源运行数据库集成验证。

## 验证结果

- ProjectResourceCountsTests 新增 10 项真实 HTTP 场景，先确认缺失字段失败，再实现并通过。与 GovernanceTests、CollectionAuthorizationQueryTests 合计 29 项通过，0 失败、0 跳过。
- scripts/check-console.sh 完成 321 项测试，0 失败、0 跳过，并通过 TypeScript 检查与 Vite 6.4.3 构建。构建存在大文件分块提示。
- 独立代码审查未发现需修复的实质问题。未进行界面视觉验收及性能压测。
- 原始失败与最终成功日志保存在本地 .runtime/project-resource-counts-20261111。
