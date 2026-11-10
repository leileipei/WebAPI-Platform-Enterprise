# 节点身份、运行事实和确认

控制面通过 `Nodes:Enrollments` 显式配置每个节点的 `EnvironmentId`、`NodeName`、`SecretFile`。配置文件只有路径；秘密来自挂载文件，至少 32 字符，不使用默认密钥。不同节点必须使用不同秘密。控制面读取后仅保留 SHA-256 摘要，数据库也只保存摘要。注册密钥固定绑定节点名称和环境，不能改请求中的环境冒充其他节点。

节点使用 `Authorization: Bearer <节点秘密>` 访问 `/internal/v1/nodes` 下的注册、心跳、目标读取和 ACK。管理会话、应用 API Key 均不能代替节点身份。管理 UI 只能访问脱敏读模型；内部目标读取含凭证校验摘要，仅授权节点可用且不缓存。

节点 ID 跨进程重启保持，instanceId 每次进程启动重新生成。重新注册清除上一个实例的运行读模型，之后由该实例报告真实运行版本。冻结目标不会因为重启、离线、停用而缩水，也不会把新实例替换进旧审批记录。未完成发布期间实例重启，需要保留原失败事实并建立新的恢复协调记录。

心跳只报告实例、实际 configVersion、deploymentSequence 和 Ready / NotReady / Degraded；不能充当 ACK，也不能凭空报告数据库没有的部署序列。配置编号可在回滚时下降，部署序列在同一实例内不能下降。

发布 ACK 校验当前启用身份、环境、冻结 nodeId / instanceId、releaseId、配置编号、部署序列和 SHA-256。只有全部冻结目标的有效成功 ACK 才将 Publishing 改为 Succeeded。重复结果幂等，不追加重复应用事件或成功审计；不同结果冲突。错误码采用固定短代码，不接受带秘密的异常文本。

超时或明确失败 ACK 将发布标记 Failed，保留已经应用的节点版本及全部目标，不声称自动恢复。ACK 接口本身也检查截止时间，Worker 未及时轮询不能让超时发布成功。首次持久化下发已封存 API Version，部分失败也不能继续编辑已运行内容。

成功发布后的正常重启另有当前实例运行确认：已登记的新实例须匹配当前 Active 环境的 desired 版本/序列、已 Published 配置和 Succeeded 发布，提交近期的成功应用结果及相同 payload hash。控制面保存独立 runtimeApplication 回执和 runtime_applied 事件，不替换历史冻结目标或 ACK，不改写原发布状态。Publishing/Failed 目标不能用该路径补成成功；节点重新换实例会清除旧运行回执。来源见[ACK 服务](../../src/WebApi.Infrastructure/Gateway/AckService.cs)和[运行确认结构](../../src/WebApi.Infrastructure/Gateway/RuntimeApplicationReceipt.cs)。运行工具还检查服务、观测源、环境状态、两节点启用/心跳新鲜度/Ready、实际与期望版本/序列及确认事实；单个回执不等于整体 Ready。

这些接口验证节点所报告的事实；真实网关只有实际完成代理配置激活及 LKG 持久化才允许发送成功 ACK，该部分由网关运行验证覆盖。
