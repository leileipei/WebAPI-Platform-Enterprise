# 网关重启就绪修复结果（升级前）

2026-10-06。本批修复完成于feature/gateway-restart-readiness隔离工作树，基线acb10a5f06a1734aec40a5d49ec1355d9fa191c8，9个源码/测试文件摘要见commit-scope.json。未自动提交、push/merge或升级4192。

## 修复行为

成功发布后的新当前实例应用同一当前desired快照，认证、实例、版本、序列、摘要、协议能力与UTC近期应用时间全部校验后，记录独立runtimeApplication和去重runtime_applied事件。历史GatewayAck、ReleaseTarget、ReleaseRecord与release.succeeded审计保持；新实例仍不能补Publishing冻结目标。

更换实例清除旧确认并保留其它Metadata；旧期望/失败/已回滚发布、错误身份/摘要/序列/时间不能生成确认。维护查询严格比较JSONB类型和当前精确快照，结合fresh/启用/Ready/版本一致后才显示Ready，兼容原实例有效历史ACK。没有新增表、迁移、外部管理API或业务发布。

回归另复现既有维护锁正常释放窗口的ENOENT竞态，新增真实文件确定性测试并仅在该窗口重试；保留owner/外来文件/符号链接拒绝规则。没有扩大为全部锁竞态重构。

## 实际验证

- 611项C#：Domain196、Integration363、Gateway52，0失败0跳过。
- 167项runtime/console：0失败0跳过。
- 20项真实PostgreSQL维护SQL：新receiptReady、错实例/发布/版本/序列/摘要/类型/null/数组/缺字段拒绝、心跳/禁用/失败/新desired拒绝、历史ACK兼容。
- 双网关真实LKG重启、重新应用与业务流量成功，实例改变，历史发布仍1条和原2份ACK，独立运行事件2条。控制面、网关和Backend为真实HTTP进程，数据库为独立test库；不是原4192已升级证明。
- 同实例3并发确认：仅1条运行事件、2份重复响应，原2历史ACK不变，附加Metadata和协议能力保留。
- 根因红绿：旧应用重启ACK409→运行确认接受；旧状态SQL Degraded→20项查询通过；维护锁正常释放ENOENT→167项回归通过。审查JSON类型问题先以Ready/应Degraded红，再严格JSONB比较转绿。
- 一次独立只读审查：Critical0、Important0、Minor1；作者一轮修复，唯一Minor已红绿验证，未进行第二轮独立复审。

原始测试日志/TRX留在私有.runtime/gateway-restart-readiness，公开verification.json保留结果，交付包不含原数据库、密码或会话。未执行全批外部Compose EndToEnd/企业生产验收。

## 当前本机状态

新维护SQL在4192真实schema只读验证通过；原应用仍0b42f9e、v4/seq4，13服务Running、四诊断源Available，尚无新运行receipt，aggregate仍Degraded。原SSO、账号、数据和已封存交付未被本批开发替换。本批源码测试成功不能写成原平台已经Ready。

4192升级方案见gateway-restart-readiness-upgrade-review.md，运行行为与回退见gateway-restart-readiness-runbook.md。按已确认约定，下一步需对一次本地提交、冷备份与停机升级作最终授权；不push/merge，不删原数据，不新增业务发布。

## 证据索引

- docs/evidence/gateway-restart-readiness/diagnosis-and-proposal.md：已确认设计与根因。
- verification.json：611/167/20与红绿、审查边界。
- code-review.md：一次审查及作者修复。
- commit-scope.json：9个拟提交文件SHA256。
- persistent-readonly-status.json：真实schema兼容与尚未升级。
- execution-ledger.md：完整阶段记录。
- retained-deliveries.json / privacy-scan.json：既有交付保留和补充包秘密检查。
- delivery-seal.json：包外ZIP摘要及CRC读回。
