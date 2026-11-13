# OpenAPI 来源导入与消费方凭证

本页按[当前状态页](deployment/current-project-status.md)列出的源码输入说明现有行为。导入与凭证均只先写入工作配置，进入网关运行仍需正常审批发布。

## 来源与导入会话

当前导入会话接受 JSON/YAML 文本、文件内容及受控 URL。文件在请求中作为内容传入，不是允许服务端读取任意本机路径。根来源、附带文件及网络读取条件由[ImportBundleReader](../src/WebApi.Infrastructure/Catalog/ImportBundleReader.cs)和[请求契约](../src/WebApi.Contracts/Catalog/ImportSessionContracts.cs)校验。

- 文本/文件来源可附带具有安全相对逻辑名称的资源；缺少的外部引用不会被自动补取。
- URL 来源及所需引用可以在项目来源政策、部署地址限制、DNS 校验、超时和字节预算内读取，再冻结为来源包。抓取不跟随重定向，不使用代理或 Cookie，不等于允许任意内网地址。
- 来源包保存原文、解析结果及摘要。缺失或不支持的引用使相关 Operation 不可导入，不将不完整语义解释为已经支持。

当前会话接口位于 `/api/v1/openapi/import-previews`：创建预览、读取、提交和撤销。预览有效期 20 分钟；有效预览上限为每项目 100、每用户每项目 5。提交重新验证操作者、目标范围、来源政策修订和预期 Bundle Hash，在业务事务中使用幂等执行器；重放历史提交也检查已保存目标的可见性。

依据：[ImportSessionEndpoints](../src/WebApi.ControlPlane/Catalog/ImportSessionEndpoints.cs)、[ImportPreviewService](../src/WebApi.Infrastructure/Catalog/ImportPreviewService.cs)、[ImportSourceFetcher](../src/WebApi.Infrastructure/Catalog/ImportSourceFetcher.cs)。来源政策和详细预算见[契约管理手册](deployment/contract-management-completion-runbook.md)。

旧 `/api/v1/openapi/import-preview`、`import-commit` 接口仍保留其文本请求形式，不能与新的带 ID 来源会话混为一个协议；其提交同样已接入幂等执行器。依据：[旧接口映射](../src/WebApi.ControlPlane/Catalog/OpenApiImportEndpoints.cs)、[OpenApiImportService](../src/WebApi.Infrastructure/Catalog/OpenApiImportService.cs)。

## 目标、原文及语义边界

按所选 Operation 映射 API Version；新建 API、给已有 API 建版本及使用已有草稿是不同目标。已有目标需提供对应修订，覆盖 Route 需明确目标和修订。权限、API/版本/Cluster 范围或路由冲突会阻断提交，不留下部分导入结果。

参数、Schema、当前 Operation 及原始来源分别保存。固定来源包并不代表完整 OpenAPI/JSON Schema 语义；未登记、动态或无法证明的内容仍按相应能力限制处理。比较与风险评审的结果也不替代实际运行验收。

## 应用、凭证与授权

项目级应用只能在其项目内授权；组织级应用可以在同组织内按实际 API/环境范围授权。API 与环境须属于同一项目，源、目标操作均须通过实际授权检查，跨组织请求拒绝。不能将其概括为“任何跨项目授权都拒绝”。

凭证 Secret 仅创建时返回，数据库保存摘要及末尾标识，不能再次读取完整 Secret。创建响应不缓存；管理详情及普通审计不返回 Secret 或凭证摘要。撤销后的凭证不能重新激活，有效期及授权窗口由网关按运行快照检查。

使用统计、普通轮换、影响预览和当前/历史部署证明已有实现，见[账号与凭证操作](deployment/account-credential-operations.md)。SSO 已有独立治理与登录入口；安全检查点/租约签发和网关安全准入仍关闭，不能据此宣称即时紧急撤销已启用，见[安全操作说明](deployment/credential-security-operations.md)。

依据：[ApplicationService](../src/WebApi.Infrastructure/Applications/ApplicationService.cs)、[ApiKeyMiddleware](../src/WebApi.Gateway/Security/ApiKeyMiddleware.cs)。
