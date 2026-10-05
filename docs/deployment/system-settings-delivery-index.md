# 系统设置交付索引

本轮交付：第40页系统设置与真实消费者、受控审计CSV、独立双网关验收及1440px页面QA。既有业务架构、权限、审批发布流程继续沿用。SSO、通知投递、SecretRef解析、自动清理和网关参数热更新未实现。

- 设计：`docs/superpowers/specs/2026-10-05-system-settings-design.md`
- 已批准计划：`docs/superpowers/plans/2026-10-05-system-settings-implementation.md`
- 中文结果：`docs/deployment/system-settings-validation-result.md`
- 独立审查：`docs/evidence/settings/final-review.md`
- 实际身份/回归/截图：`docs/evidence/settings/`
- 源码及验收包：`deliverables/WebAPI_Enterprise_系统设置源码及验收_20261005.zip`
- 外置摘要：同目录 `.manifest.json`，含包SHA-256及每个文件摘要；ZIP内含MANIFEST。

工作目录：`enterprise/.worktrees/system-settings`。分支 `feature/system-settings`；基线 `79990836dedb5a3d8962ae2f828090b08208aff2`。HEAD仅是基线，实际构建身份为sourceManifestHash；没有本轮源码提交。

## 运行与验证

使用已安装依赖，设置以下环境变量后在系统设置工作树根执行：

```bash
export WEBAPI_NODE=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node
export WEBAPI_PNPM=/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/bin/fallback/pnpm
export pnpm_config_verify_deps_before_run=false
bash scripts/check-settings.sh verify
```

verify依次运行完整领域、集成、网关、前端及运行器检查，构建随机隔离环境并执行实际E2E/浏览器。完成后须逐张查看截图并记录visualReviewComplete，再运行封装入口的 `--check-only` 检查。若另需完整回归可运行 `verify --reuse-e2e`；复用仅允许当前全部产品与工具摘要一致且截图已审阅。

仅启动新验收夹具：`bash scripts/check-settings.sh e2e --retain-for-review`。已有本轮查看夹具应先精确清理，避免重复占用。实际查看URL见 `verification.json` 的 review.url；或私有 `.runtime/settings-review.json`。临时凭据只保存在该目录0600文件，不写入文档/公开证据/ZIP。应用页面会话由登录建立。

重跑浏览器：`bash scripts/check-settings.sh browser`。它会在隔离数据库内重置测试安全与审计开关、创建和撤权QA账号，不应用到长期环境。

停止并清理：`bash scripts/check-settings.sh cleanup`，只处理settings随机project与匹配owner标签，先检查容器及卷归属，再删除本夹具卷和凭据；其他local/policies环境不受影响。失败日志在私有operations.log，成功证据不含秘密。

封装：`python3 scripts/package-settings.py --root "$PWD" --evidence "$PWD/docs/evidence/settings"`。旧源摘要、失败项、缺浏览器结果或未完成视觉检查均拒绝；源码/运行器测试包含这些失败门禁和秘密排除验证。封装后做CRC及逐文件摘要回读。

## 集成边界

工作树与本轮证据保留供审阅。没有额外授权，不提交、推送、合并或升级4192；后续长期部署应基于本轮具体差异与不可变提交另行执行冷备份/切换验收。既有封存流量策略及升级证明未覆盖或改写。
