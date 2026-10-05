# 流量策略交付索引

规格与实现：认证、超时、Redis 共享限流、节点独立熔断。当前实现使用原企业控制台设计系统，补齐第 16/17 页及路由、发布 Review、节点能力、日志与 Trace 的策略信息。

- [中文运行手册](../../deployment/traffic-policies-runbook.md)
- [数据字典](../../traffic-policy-data-dictionary.md)
- [独立审查与裁定](final-review.md)
- [实施账本](execution-ledger.md)
- [固定源码清单](source-manifest.json)
- [完整回归](regression.json)
- [真实双网关和三源验收](verification.json)
- [临时项目清理证据](disposable-verification.json)
- [1440px 交互与截图 QA](ui/qa.json)
- [规格覆盖矩阵](coverage-matrix.json)

独立评审入口记录在本机 `.runtime/policy-review.json`，不含于交付 ZIP。既有 4192 仍是原版本。新功能未自动提交或上线，生产性能和真实企业系统集成未执行。

最终独立评审入口：[打开策略中心](http://127.0.0.1:34245/policies)，账号 `e2e-admin`；密码在本机 `.runtime/policy-review.json` 指向目录的 `password` 文件中，仅本机私有保存。旧账号 `admin` 属于既有 4192 环境，与此随机合成验收环境分开。

已验证：完整回归487项（92领域、241集成、51网关、44前端、59运行脚本），真实双网关/三源9项业务场景及1项C# E2E，15张1440px截图逐图QA。独立审查发现的3项问题已修正；裁定及生产容量/SSO等边界见最终审查与运行手册。

[本批已跟踪文件变更](changes.patch)配合完整源码及source-manifest.json复核；新文件也包含于源码包。分支 `feature/traffic-policies` 和未提交修改保留，不自动合并。
