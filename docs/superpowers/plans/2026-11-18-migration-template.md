# Test database migration template Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 减少普通集成测试重复迁移，同时保留隔离与原始验证门槛。
**Architecture:** 每类生命周期内创建真实迁移的封闭模板，显式选择的ApiFixture克隆独立数据库；默认路径保留。
**Tech Stack:** .NET 10 / EF Core / Npgsql / PostgreSQL / xUnit，版本不变。
**Spec:** [设计](../specs/2026-11-18-migration-template-design.md)

## Global Constraints

- 旧资料、历史失败和所有测试日志保留；产品、依赖、CI门槛和并发不变。
- 默认初始化、权限/安全/故障/超时/迁移测试保持原始路径。
- 本机全量与GitHub必需检查通过后才合入main。

## Review Focus

- 并发克隆与模板销毁竞争，关闭后不再发布新数据库。
- 用户与业务行跨克隆泄漏，必须真实写入后验证后续克隆为空。
- 迁移失败产生半成品，必须报错、清理且禁止后续克隆。
- 模板连接未释放，须实际禁止连接并完成克隆。
- 编译或运行变化使用旧模板，须无持久查找、固定输入指纹、新实例新数据库。

### Task 1: 模板与隔离回归

Files: `tests/WebApi.TestSupport/Support/{PostgresDatabase,MigratedDatabaseTemplate,ApiFixture}.cs`、`tests/WebApi.Integration.Tests/MigratedDatabaseTemplateTests.cs`。
Interfaces: `MigratedDatabaseTemplate.InitializeAsync()`、`CreateDatabaseAsync(): Task<PostgresDatabase>`、`DisposeAsync(): ValueTask`；`ApiFixture(MigratedDatabaseTemplate)`显式选择。

- [x] 添加测试与最小接口，运行并确认未实现行为失败。
- [x] 实现生命周期、真实迁移、封闭模板、互斥克隆与失败清理。
- [x] 运行新增测试，核对真实数据隔离与数据库删除。

### Task 2: 明确选择与性能对照

Files: 四个已命名业务测试类、`Support/MigratedDatabaseTemplateFixture.cs`、`docs/deployment/migration-template-results-20261118.md`。
Interfaces: xUnit `IClassFixture<MigratedDatabaseTemplateFixture>`拥有模板；共享Setup新增可选模板参数，默认保持不变。

- [x] 记录优化前同一81项集合的两轮结果。
- [x] 只修改普通业务方法初始化，保留全部断言与其余方法默认路径。
- [x] 执行同一集合两轮优化后对照，记录收益与范围，不扩大外推。

### Task 3: 全量验证与合并

- [ ] 原脚本本机Domain、Integration、Gateway、Console及Python全部执行通过。
- [ ] 新的独立审查检查整个变更；修复实质问题后完成对应回归。
- [ ] 保存文档/证据，提交并创建PR，等待四项GitHub必需检查成功。
- [ ] 核对保护、确切提交和主线后合并，跟进main自动复跑；保留记录。
