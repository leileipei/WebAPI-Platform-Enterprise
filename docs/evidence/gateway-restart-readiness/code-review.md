# 一次独立代码审查

2026-10-06，审查只读，基线acb10a5，当前工作树未提交diff及新增文件。一名独立审查者，未派遣额外审查，不进行第二轮。

原审查结论：Critical0、Important0、Minor1，Ready to merge Yes。新当前实例不能补Publishing冻结目标、环境锁下再验身份与幂等、重新注册清理及保留Metadata、ACK/运行receipt分别核验、维护锁释放窗口的最小修复均符合已确认方案。

Minor：SQL ->>文本对数字1与字符串"1"等错误JSON类型宽松，可能把损坏的receipt读成confirmed。虽无Metadata外部写入口，本批作者仍补强fail closed：真实PG新增类型测试先RED（actual Ready / expected Degraded），改为JSONB值与to_jsonb比较，随后包括字符串/null/数组/缺字段的20项检查全部GREEN。

另补同实例3并发确认的真实API覆盖，验证2份重复响应、仅1条runtime_applied事件和原2历史ACK，operatorTag和协议能力Metadata保留。这是现有环境锁保证的补充覆盖，不声称它在修复前失败。

作者一轮修复与最终全量回归，不进行第二轮独立复审。最终结果见verification.json；第一次审查读到的是610 C#、167工具、14 SQL，作者补强后结果须以最终证据为准。

## 审查边界与处理

- 4192升级/备份恢复/生产：本批只在隔离源码验证，尚未执行；后续需最终授权并真实验收。
- receipt定期到期：按确认方案，近期AppliedAt在写入校验，之后以当前实例/desired/hash与新鲜心跳证明持续运行；不额外设固定周期使正确运行节点无故降级。
- 既有死进程锁清理等其他替换竞态：本批只修复真实触发的EEXIST后正常释放窗口，未扩大锁体系重构，不能声称所有锁竞态已经消除。
- 独立审查不重跑写入测试，核验实际日志。一次只读SQL尝试被Docker socket沙箱权限拒绝，审查者停止此路径；主执行者获已有任务范围的执行权限后完成真实SQL与全量测试。

无Critical/Important未解决项；唯一Minor已做红绿验证，没有遗漏的暂缓Minor。
