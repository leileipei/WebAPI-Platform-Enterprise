# Runtime Snapshot 协议与兼容边界

本页按[当前状态](../deployment/current-project-status.md)记录的源码区分普通运行快照和未接线的安全治理方言；不能把首期 2.0 说明当成当前唯一协议。

## 普通发布的版本选择

[SnapshotCompiler](../../src/WebApi.Infrastructure/Releases/SnapshotCompiler.cs)按实际使用的策略选择版本：

| 版本 | 触发能力 | 边界 |
| --- | --- | --- |
| 2.0 | 基础认证及超时 | 认证和超时折叠到 Route 基础字段 |
| 2.1 | 共享限流、熔断等高级绑定 | 带来源修订的策略及运行绑定 |
| 2.2 | JWT、重试或缓存 | 保留对应认证和策略配置 |
| 2.3 网络策略 | CORS、IP 过滤或 Transform | 不携带安全治理要求字段的网络策略方言 |

节点能力通过实例绑定元数据读取；发布仍须校验全部目标节点的协议能力，不能只比较 AppVersion 或版本数字。依据：[SnapshotSchemaCapabilities](../../src/WebApi.Infrastructure/Gateway/SnapshotSchemaCapabilities.cs)。

## 安全方言不等于可运行能力

[SnapshotDialectClassifier](../../src/WebApi.Domain/Runtime/SnapshotDialectClassifier.cs)按版本及治理字段识别网络 2.3、治理 2.3/2.4 等方言；解析成功不是安全授权。当前[RedisSnapshotStore.Validate](../../src/WebApi.Infrastructure/Runtime/RedisSnapshotStore.cs)拒绝治理 2.3/2.4，[网关激活](../../src/WebApi.Gateway/Configuration/SnapshotActivation.cs)复用该校验。安全签发及网关准入未接线，不能写成治理快照已可正常下发运行。

Envelope、原始字节、环境绑定、发布序列及历史回滚的完整性约定仍需保持。节点声明支持一个版本，也不构成当前安全准入已启用的证明。

## 2.0 历史字段说明

以下保留首期 2.0 字段与阶段说明，只适用于其固定协议；2.1–2.3 的扩展以当前契约及编译器为准。

Payload字段：schemaVersion、environmentId、configVersion、generatedAt（UTC）、routes、clusters、policies、applications。Envelope字段：releaseId、deploymentSequence、configVersion、payloadHash、sizeBytes。内部传输DesiredConfigResponse的payload为原始UTF-8字节的Base64；Hash计算解码后的实际字节，不计算JSONB查询副本，也不把自身放进payload。PG bytea、Redis及LKG必须保留相同字节。

Runtime Route只含业务身份、模板、方法、Cluster运行引用、matchOrder、timeoutMs、requireApiKey。Cluster运行ID由原生SourceId及实际配置内容派生，保留SourceId供治理定位；共享原生Cluster在一个Snapshot内可有多个被冻结配置。未选API保留其原运行引用，不能因另一API发布改变后端。仅保留被路由使用的Cluster。

应用运行数据只有身份、状态、Credential摘要/窗口与API授权/窗口；没有Owner、revision、创建人、审批、原文或明文Secret。所有环境消费方配置取提交时冻结值；只将有运行路由的API授权编入Snapshot。未发布草稿不进入运行路由。客户端必须每次准入检查UTC窗口，不能缓存认证成功后永远放行。

首期Runtime策略仅authentication（mode: ApiKey/Anonymous）与timeout（timeoutMs: 1..300000）。策略只允许定义字段，未知类型/模式/多认证绑定拒绝。Schema在编译时检查JSON及基础Schema结构，完整JSON Schema语义和Runtime请求体校验不在首期。上游按控制面部署允许列表复验，拒绝userinfo及非法地址。

静态、参数、通配符依次匹配，同类型中priority高优先。环境内方法与同形模板不可重复。SchemaVersion必须2.0，Snapshot ConfigVersion必须正数；有效空路由Snapshot用于停用全部路由，不等同于未配置节点。回滚可重用较低configVersion的历史payload，节点更新顺序必须依赖更高deploymentSequence。

例：Envelope configVersion=2、deploymentSequence=8。两节点各自校验payloadHash与sizeBytes及environmentId，构建完整generation并成功持久化后才ACK。部分ACK仍Publishing，不能显示全局成功。这部分运行行为在网关任务验证，本阶段仅完成编译协议与领域校验。
