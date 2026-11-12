# 治理授权、写入事务与审计

本页按[当前状态页](deployment/current-project-status.md)记录的源码输入解释授权行为。角色、权限编码或数据表存在，不代表对应运行链路已启用；历史裁定见[实施决策](decisions.md)。

## 账号、功能权限和数据范围

服务端同时检查用户有效状态、首次改密限制、角色功能权限及资源实际范围。Scope 的访问模式为 `read` 或 `read_write`；组织、项目、环境范围分别匹配相应层级，范围取并集，再受角色所属组织及功能权限约束。环境授权不会自动获得父项目或组织的完整权限。平台级操作另需真正的平台管理员角色，不能仅凭同名业务权限绕过。

依据：[AuthorizationService](../src/WebApi.Infrastructure/Security/AuthorizationService.cs)、[CollectionAuthorizationQuery](../src/WebApi.Infrastructure/Security/CollectionAuthorizationQuery.cs)、[ScopeMatcher](../src/WebApi.Domain/Security/ScopeMatcher.cs)。治理停用/启用与业务访问的上级状态要求不同，以对应接口代码为准。

全局用户治理、整体角色和 Scope 分配由平台管理员处理；组织资源及组织自定义角色按相应权限管理。系统角色不能任意编辑，自定义角色不能授予操作者未持有的功能权限。最后一位可用本地平台管理员受[恢复保护](../src/WebApi.Infrastructure/Sso/LocalAdministratorGuard.cs)约束。

## 命令、并发和审计

关键业务命令使用数据库事务和统一授权锁，事务内重新检查身份与权限，授权变更与受保护写入按同一锁协调。修订冲突通常返回 `412`，写权限不足返回 `403`；无权详情可能按资源接口映射为 `404`，不能将所有接口的错误处理简化为同一规则。

[AuditedCommandExecutor](../src/WebApi.Infrastructure/Persistence/AuditedCommandExecutor.cs)捕获字段白名单；业务提交与其审计写入在同一事务，审计失败不能独立留下成功业务。密码、秘密和会话材料不属于业务审计白名单。

源自 `00f45a8` 的[审计范围查询](../src/WebApi.Infrastructure/Governance/AuditAccessQuery.cs)还会对授权迁移前后每个快照解析真实环境及项目，再匹配读取者范围；历史记录原文不改写。该修复已随 PR #5 合入 main `3774ccc`；本次核实的固定主干 `50d6fb9` 继续包含该约束，版本及运行资格见状态页。

## 初始化与已扩展模块

权限字典通过显式初始化建立，首管理员通过受限密码文件显式创建，不产生默认演示账号。首次改密、SSO、监控告警、系统设置、外部通知和跨环境交付已有相应实现及端点注册，不能继续沿用首期“仅保留权限编码、模块未实现”的说明。入口见[ControlPlaneApp](../src/WebApi.ControlPlane/ControlPlaneApp.cs)及[资料总目录](README.md)。

统一写锁优先保证授权提交一致性，不代表生产吞吐已经验收；SDK、数据库、HTTP、浏览器和目标部署证据分别解释。
