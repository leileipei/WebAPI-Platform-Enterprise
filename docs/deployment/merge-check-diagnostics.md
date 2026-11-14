# 合并检查失败与诊断修复

## 固定输入与已知失败

本修订基于 main `15f8711357bb8d941de077248ba75db997ec5459` 和 PR #11 `9c2652669f94842e0b5fa3f6a8c2a81a01b32385`。

- [PR #11 Integration](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/43491949503/job/132115121134)：2188 通过、1 失败。`DeliveryPersistenceTests.SavingRequiresTargetWriteAndReadingRechecksBothEnvironmentScopes` 在 Setup 登录响应校验返回 503，尚未执行保存与权限断言。原日志没有响应错误码或服务端阶段，因此该次具体依赖故障原因仍未知。
- [main Gateway](https://github.com/leileipei/WebAPI-Platform-Enterprise/actions/runs/43395130868/job/131784095154)：559 通过、1 失败。`NetworkPolicyCacheTests.Cache_CorsOriginsAndNoOrigin_AreSeparateFinalJsonPartitions(true)` 的跨节点响应 backend 值由 1 变为 2；原日志没有缓存读写结果，无法据此认定为 TTL、Redis 超时或分区键错误。

## 已复现并修复的缺陷

1. 登录诊断把内部预算取消记为 `Cancelled`，完整性检查却只接受 `Failed`。完整的预算超时回归修复前失败（expected complete / actual incomplete），修复后通过。只有已观察到 BudgetElapsed、预算取消且调用方未取消时才接受该完整路径；缺失阶段、串错请求、事件丢失和调用方取消继续保持 incomplete。
2. 缓存测试 `ObservedStore` 对 `Bypass` 也增加成功写入计数。真实缓存存储拒绝已失效条目的回归修复前失败（expected 0 / actual 1），修复后通过。现在成功计数只接受 `Stored`，另外保留尝试次数和有界的读写结果、耗时。跨节点响应逐字节比较、分区数、回源次数和命中次数断言保留。
3. Python 模块此前未被四项 CI 覆盖。现在在既有必需 `console` 作业加入标准库 unittest discovery，无路径过滤、无忽略失败。

## 诊断边界

登录观察器默认关闭，仅显式测试夹具开启。输出使用固定错误分类、SQLSTATE 白名单、关联过的 traceId 和阶段耗时；不输出口令、连接串、任意异常文本或凭证。诊断读取保留原响应体和原断言，诊断自身故障不得替换业务结果。

缓存观察仅在测试夹具中实现。缓存依赖不可用时，产品仍按既有约定旁路并回源；诊断必须区分 `Miss`、`Unavailable`、`Stored` 和 `Bypass`，不能把“方法返回”当成“条目已写入”。

## 保留的门槛

- 登录保护整体预算 1000ms，缓存决策默认预算 100ms。
- 既有排队 1 秒与登录保护 2 秒耗时断言。
- 四项必需检查：console、contracts-domain、contracts-integration、contracts-gateway。
- 原测试断言、测试并发配置、重试策略、工作流超时和分支保护。

本修订不将历史失败改标为通过。受控故障验证诊断机制，本地筛选通过不等于完整托管检查通过；历史缺失信息不能通过新的成功结果补造。最终合并依据用户授权条件和当前固定提交的实际检查结果。
