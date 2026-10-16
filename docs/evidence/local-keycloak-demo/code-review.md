# 本批工具代码审查与作者修复

一名独立审查者，一轮全批只读审查；Base0b42f9e，当前工作树差异，未进行第二轮独立复审。审查者仅复跑新增9项测试通过；作者完整回归162项通过。

Critical0；Important2；Minor3。

1. 恢复Viewer仍绑定旧Provider：新增显式rebind-restored，仅备份来源和Subject匹配时停用旧身份源/Viewer、审计API重绑，恢复原状态和Viewer/read Scope。普通provision继续拒绝接管。
2. 初始化或解包失败不能安全续跑：新增Prepared/SecretsReady/Restoring/Restored/Ready阶段；start续跑既有秘密，逐卷staging/done与备份摘要保护，运行服务前完成全卷恢复；完成卷不覆盖。
3. 无单次请求期限：Discovery5秒内、管理员API10秒内取消请求，Discovery也受总剩余期限限制。
4. backup stop部分失败无finally：stop移入try/finally，失败仍尝试恢复IdP启动。
5. 外部秘密卷异常阻断平台stop：对stop/down/ps/logs/config只校验平台归属，创建/挂载操作仍严格核验IdP秘密卷双标签。

作者一轮修复，包括初始化中断、恢复中断、显式绑定治理的有意义测试。后续实测证据证明镜像UID、0600秘密、空卷恢复与真实登录；不是第二轮独立审查。

审查者暂未判断的项目：实际部署、浏览器登录、冷备份恢复、票据撤销、镜像UID及稳定Subject、卷内权限、4卷解包、固定工具归档和原账号/业务数据保留。全部由本批后续实施证据承担；不把审查报告代替这些验收。
