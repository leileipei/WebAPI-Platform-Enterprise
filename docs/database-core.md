# 首期数据库模型

来源36表中保留32表，延期告警规则 / 事件、SSO Provider和系统设置；补充5表。API Version无environment字段，Route按环境关联。

- `organizations` → `Organization`，7字段
- `projects` → `Project`，9字段
- `environments` → `EnvironmentRecord`，11字段
- `api_groups` → `ApiGroup`，7字段
- `apis` → `Api`，12字段
- `api_versions` → `ApiVersion`，13字段
- `api_routes` → `ApiRoute`，14字段
- `api_parameters` → `ApiParameter`，9字段
- `api_schemas` → `ApiSchema`，9字段
- `upstream_clusters` → `UpstreamCluster`，10字段
- `upstream_destinations` → `UpstreamDestination`，9字段
- `policies` → `Policy`，10字段
- `route_policy_bindings` → `RoutePolicyBinding`，3字段
- `applications` → `ApplicationRecord`，10字段
- `application_credentials` → `ApplicationCredential`，12字段
- `application_api_permissions` → `ApplicationApiPermission`，9字段
- `users` → `UserRecord`，11字段
- `roles` → `Role`，7字段
- `permissions` → `Permission`，5字段
- `user_roles` → `UserRole`，2字段
- `role_permissions` → `RolePermission`，2字段
- `user_project_scopes` → `UserProjectScope`，7字段
- `approval_flows` → `ApprovalFlow`，6字段
- `approval_steps` → `ApprovalStep`，5字段
- `approval_tasks` → `ApprovalTask`，8字段
- `release_records` → `ReleaseRecord`，18字段
- `release_items` → `ReleaseItem`，7字段
- `gateway_config_versions` → `GatewayConfigVersion`，9字段
- `gateway_config_snapshots` → `GatewayConfigSnapshot`，5字段
- `gateway_nodes` → `GatewayNode`，13字段
- `gateway_node_events` → `GatewayNodeEvent`，6字段
- `audit_logs` → `AuditLog`，13字段
- `route_methods` → `RouteMethod`，4字段
- `release_targets` → `ReleaseTarget`，4字段
- `gateway_acks` → `GatewayAck`，9字段
- `outbox_messages` → `OutboxMessage`，10字段
- `idempotency_records` → `IdempotencyRecord`，8字段

Snapshot的payload_bytes是哈希对应原始UTF-8字节，JSONB查询副本不用于重新计算历史哈希。

## 约束与构建基线

所有外键采用Restrict，保留审计所需实体；以状态停用代替级联删除。复合引用约束保证项目属于组织、Scope环境属于项目、API分组属于项目、Cluster属于环境、Route方法属于同一Route环境。角色分系统 / 组织两种唯一范围。`route_methods`的(environment_id, method, normalized_path)唯一索引承担并发冲突保护，业务保存必须同事务维护。

迁移由EF模型生成，包括关联、唯一索引及有效期 / 超时检查；不在运行时自动建表或隐式种子。独立Migrator从受限Secret文件读取数据库密码，显式执行。`deploy/initial-core.sql`是对应幂等脚本。

冻结SDK10.0.401、Runtime10.0.12、EF10.0.12、Npgsql EF10.0.3；所有NuGet依赖有锁文件，镜像按deploy/images.lock.json中的多架构digest固定。此次本机验证为Linux ARM64，尚非AMD64或生产验收。
