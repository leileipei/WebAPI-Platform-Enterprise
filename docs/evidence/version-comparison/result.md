# 版本比较与风险评审：本批验收结果

日期：2026-10-06。本批源码、独立固定来源验收、1440×1024界面QA及临时资源清理完成；4192未升级，未合并主目录或推送。范围为第11页版本比较与风险评审及其发布交接，不代表其他39页真实集成或生产验收完成。

应用源码提交：`943143fdf4022f55b599fd521cf2e4fbe74a502d`。本批实现提交为f0dc1db，验收用例修复为72b1c08，长片段布局修复为943143f。后续仅文档和证据提交不改变已验收应用来源。

实现：同一API两个版本的OpenAPI/维护参数/Schema比较；Complete/Limited/Invalid；独立变化和风险计数；持久历史、JSON/CSV、安全游标；追加风险评审、强确认；可选发布引用、冻结及提交/启动/后台构建复核。风险评审不替代两级正式审批与双网关ACK。

自动化检查：领域249、真实PostgreSQL集成405、网关52、控制台99、运行工具104，共909项通过；TypeScript及Vite通过。领域额外7项方向边界和执行器7项复跑通过；CSS修复后控制台99项及构建重新通过。套件来源及日志哈希见[check-results.json](check-results.json)，日志在交付包tests目录。新增两表的幂等SQL在隔离PostgreSQL执行两次验证。

固定来源：Git archive来源、锁定依赖镜像、运行工具哈希及静态资产SHA均固定；实际镜像ID见[source-manifest.json](source-manifest.json)。真实HTTP覆盖报告和强确认、评审撤权403、服务端目标交接、申请人自批403、两级独立审批、双网关ACK、旧无引用发布。最终门禁[verification.json](verification.json)的complete为true。

界面：12项动作通过，六张必需截图均为1440×1024并逐张查看；另验收超长片段及只读账号按钮。筛选不改变全量风险计数；导出在Chrome实际下载并读回，包含全部报告而非当前筛选行。内嵌浏览器下载事件未取得文件，不能将其记为下载通过。详见[ui-audit.md](ui-audit.md)、[ui-qa.json](ui-qa.json)及[export-verification.json](export-verification.json)。

清理：核验本批owner后移除临时环境，容器、卷、网络、秘密文件残留均为0；临时QA网址已失效。4192账号、数据和服务未由本批操作；主目录六项既有修改及历史未跟踪资料保留。

权限：完整契约需api.read/api.version.read/api.schema.read；风险记录另需项目可写api.approve。没有默认扩大长期用户角色或Scope。发布修改回执省略风险备注，授权GET/预览显示备注；旧候选字节及SHA保持原有算法。

限制：仅支持OpenAPI3.0 JSON及受限Schema语法；未支持约束显示Limited/Unknown，不能据此声称完全兼容。此次为本机隔离验收，未完成企业联调、容量、生产验收、全量WCAG或辅助技术测试。

交付：[delivery.md](delivery.md)；包读回结果[package-verification.json](package-verification.json)。后续4192升级须单独复核实时版本、数据与授权、冷备份及切换方案，并取得服务暂停和升级授权。
