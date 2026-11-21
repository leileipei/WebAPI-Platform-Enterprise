# API 授权矩阵实施计划

> 执行使用 `superpowers:subagent-driven-development`。验证进度与失败记录单独保存。

**目标** 补齐原第 21 页矩阵、范围、批量授权、有效期、撤权与影响预览。

**基线** `3b8a672a99d84e17ea9ff887b35267bdaf326eba` 已完成 main 四项复跑。

**设计** [API 授权矩阵设计](../specs/2026-11-21-api-permission-matrix-design.md)

## 公共契约

路径以 `/api/v1/applications/{applicationId}/permission-matrix` 为根。JSON 使用 camelCase。Guid 为标准字符串，时间为 ISO 8601 日期时间。可空属性可以是 null。GET、预览及提交设置 `Cache-Control: no-store`。

| 请求 | 契约 |
| --- | --- |
| `GET ?projectId=&page=&pageSize=` | 返回 `applicationId, projectId, evaluatedAt, environments, items, total, page, pageSize`。环境为 `id, code, name, isProduction, canManage, canGrantLegacy, canGrantRouteMethods`。API 行为 `id, code, name, grants`。grant 为 `id, environmentId, mode, validFrom, expiresAt, revision, state, routeMethods`。`state` 为 `Pending`, `Active` 或 `Expired`，`routeMethods` 使用现有路由方法类型。 |
| `POST /preview` | `projectId, changes`；每项为 `apiId, environmentId, action, permissionId?, revision?, validFrom?, expiresAt?, mode?, routeMethods?`。`Save` 使用模式 `LegacyApi` 或 `RouteMethod` 以及起止时间，后者包含显式方法。创建不传授权 ID/修订，更新传两项。`SetWindow` 保持现有模式及方法，只提供 ID、修订和窗口。`Revoke` 只提供 ID 和修订。 |
| 预览结果 | `previewToken, expiresAt, requiresProductionConfirmation, items`。影响项为 `apiId, environmentId, isProduction, before, after, running`。before/after 为 null 或 `mode, state, validFrom, expiresAt, methodCount, methods`。`methods` 为 null 或 `routeId, path, authorizationRevision, methods` 列表，path 可空。运行依据为 `status, configVersion, deploymentSequence, grant`，状态 `Confirmed`, `Unconfirmed`, `NotVisible`。每项按传入单元身份匹配。 |
| `POST /commit` | 原预览请求另带 `previewToken, confirmProduction`，请求头 `Idempotency-Key`。回执 `appliedCount, activation`，activation 为 `WorkingConfiguration`。生产确认缺失和旧预览用 409，授权修订过期用 412。 |

保留 `GET /applications/{id}/route-method-options?environmentId=&apiId=`。所有生产、并发及权限约束由后端作最终判断。

## Task 1 HTTP、事务和权限

范围 `src/WebApi.Contracts/Applications/PermissionMatrixContracts.cs`、`src/WebApi.Infrastructure/Applications/`、`src/WebApi.ControlPlane/Applications/ApplicationEndpoints.cs` 及 DI 注册、必要审计选择、`tests/WebApi.Integration.Tests/ApplicationPermissionMatrixTests.cs`。其余产品和历史测试不做重构。

- [ ] 用现有 `ApiFixture` 增加真实 HTTP 失败回归并记录 RED。覆盖矩阵跨页及应用/项目/环境读取权限，PROD 明确确认，同键重放不重复写入，跨 Scope 整批拒绝，预览/路由修订失效，父子撤权和故障整批回滚。保留本机日志。
- [ ] 实现 GET 和预览/提交。复用 `ApplicationService`、`AuthorizationService`、`IdempotentCommandExecutor`、`RunningDeploymentReader` 与 Data Protection。批次操作共用事务，逐环境审计。锁按环境 ID 顺序获取。预览内验证与提交相同的约束。
- [ ] 修复旧撤权路径的路由子记录删除及安全版本推进，验证原有授权移动、显式模式和严格切换行为。
- [ ] 优先运行 `bash scripts/check-contracts.sh integration --filter 'FullyQualifiedName~ApplicationPermissionMatrixTests|FullyQualifiedName~RouteMethodAuthoringTests|FullyQualifiedName~GovernanceLegacyApplicationCompatibilityTests|FullyQualifiedName~AuditPermissionMoveScopeTests'`。独立子网用 `10.251.219.0/24`，运行前只读核对。

## Task 2 控制台矩阵及预览

范围 `console/src/pages/Applications.tsx`、`console/src/pages/ApplicationPermissions.tsx`、`console/src/applications/permission-matrix*` 相关组件和类型、必要 CSS。保持已实现授权选项与项目 Shell 语义。

- [ ] 使用公共契约实现 API/环境矩阵和跨页多选，环境仅取后端返回。支持单条编辑、批量授权、有效期及撤权。保留原服务器时间精度。使用现有路由方法选项和校验模块。
- [ ] 增加工作授权 `Pending/Active/Expired`、生产标签、前后方案、当前运行依据，以及提交明确确认。处理 401/403/404 权限失效，409 重新预览，412 刷新修订，网络结果不明确时同键核对。验证进入/退出、键盘焦点及未提交输入保护。
- [ ] 使用已安装标准 Node 运行 Console 测试、TypeScript 和 Vite 构建，保存结果。冻结产品 SHA 后参加浏览器联调。

## Task 3 固定提交浏览器与追溯

范围 `scripts/acceptance/`、测试专用浏览器主机及本批证据、两份状态入口和当前台账。保留旧夹具及失败。生产、共享应用和只读账号均为本批合成数据。

- [ ] 固定产品提交后从 Git 导出，使用独立 loopback、PostgreSQL、Redis 和实际 ConsoleHost。绑定源和静态资源，记录项目/环境授权、多选、生产确认、过期标记、撤权及只读或失权后的浏览器证据。
- [ ] 核对每个需求 ID 的实际结果，保留缺证。归档 PR #23 main 复跑回执和 B02 日志、截图与源证明，保持原历史台账原字节。
- [ ] 完成独立审查、分支本机全量和 GitHub 四项必需检查。记录新 PR 的精确 head 与核验结果。
