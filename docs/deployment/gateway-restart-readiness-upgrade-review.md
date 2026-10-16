# 网关重启就绪修复接入4192：升级审阅方案

2026-10-06。本方案为开发和隔离验证后的升级准备。当前尚未提交、构建长期新镜像、停机或升级4192；原0b42应用和acb维护工具保持运行，v4/seq4不变，SSO IdP4194保持。

## 拟授权范围

只在feature/gateway-restart-readiness隔离工作树固定一个本地提交：以docs/evidence/gateway-restart-readiness/commit-scope.json的9个源码/测试文件和摘要为准。不提交原工作区修改、秘密、备份、依赖、bin/obj或历史交付，不push/merge。执行记录和补充说明单独保留。

长期构建继续使用Git archive不可变提交，固定现有依赖与Node/SDK。本批无数据库迁移。现有SSO、账号、角色/Scope、密码、原平台owner、绑定、端口4192/4196/4197、IdP4194、7个业务卷和原业务v4/seq4全部保留。

## 授权后执行

1. 重验9文件摘要、主工作区和历史交付不变，再固定一次本地提交。前端依赖使用现有SSO工作树的锁定依赖，经package.json/pnpm-lock.yaml字节核验；依赖链接不纳入提交。
2. 用buildRelease从新提交Git archive构建长期镜像，核验sourceRevision/镜像user10001/依赖摘要。构建失败不触碰4192。
3. 保留原release、runtime、manage.sh/manage-idp.sh、tooling.json和旧镜像。通过owner锁停止原平台冷备份7卷、逻辑dump、全部账号秘密、dp-keys、SSO配置和审核员凭据，逐文件SHA读回；IdP另外独立4卷备份。私有备份不进入交付包。停机时间不作预估承诺，失败先恢复原服务，停止升级。
4. 备份在全新disposable owner空卷恢复验证，不覆盖原卷。用于原平台恢复的SSO配置按既有规则disabled，不让副本自动指向原IdP。原账号/业务数据读回通过后清理仅本批副本。
5. 使用新提交工具initializeRuntime，显式directory=enterprise/.runtime/local、bootstrapUsername=原admin和新releaseFile；不在新工作树无参数init。已初始化环境不bootstrap或重置密码。SSO overlay每次从受控配置重新生成并核验client-secret外部卷owner。
6. 将新提交scripts/deploy归档到原目录tooling/{commit}并验证全部文件SHA，更新两个manage入口。既有IdP owner/project/Subject/Provider保持，不重新seed或重绑。
7. 等待双网关新实例加载相同v4/seq4并生成runtimeApplication，maintenance status须两节点runtimeConfirmed=true、fresh/Ready、当前等于desired，aggregate Ready。原历史ACK/目标/发布/audit摘要不变；不得伪造ACK或新增业务发布。
8. 验证原admin、两名审核员、sso-demo-viewer和Keycloak真实登录；Viewer/read及写入拒绝保持，SSO默认跳转保持原设置。双网关/orders实测200、原数据/密码摘要保持、四诊断源Available。正常固定维护入口再restart并复验Ready及SSO，记录真实结果和脱敏UI。
9. 输出独立升级补充包，不覆盖15份历史交付ZIP。失败保留证据，回退旧release和旧工具入口，不删除原卷；不覆盖已经产生业务写入的数据。

## 当前验证边界

详见verification.json与code-review.md。C#测试是隔离真实PostgreSQL和进程内真实控制面/双网关/LKG/流量，另有维护SQL真实PG检查；不是原4192升级验收，也没有执行全部外部Compose EndToEnd场景。升级后Ready与真实SSO仍须再次验收。

## 为什么仍需最终授权

已确认范围为“先开发/隔离测试/审查，4192升级另行审阅”。既有计划约定“不自动提交”，而buildRelease要求Explicit immutable source revision required，因此此时需要明确授权一次本地提交、冷备份与4192升级，接受维护停机。这不包含推送、合并、删除数据或新增业务发布。
