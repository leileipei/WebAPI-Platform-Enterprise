# 网关运行版本与 LKG

网关使用 YARP 2.3 的真实配置 Provider 和 `MapReverseProxy`。每次部署生成带 deploymentSequence 的 Route / Cluster ID，路由 metadata 引用其不可变运行 generation。请求按 YARP 实际匹配路由取得该 generation 的租约，路由、后端、认证数据一起保留到请求完成，不从另一个“最新认证指针”取凭证。

切换时短暂关闭新请求进入路由选择的准入门；已取得租约的请求继续访问旧后端，不等待旧请求结束才发布。完成 YARP 配置应用、交换完整运行 generation 后重新开放准入。被替换的 generation 在最后一个旧请求完成后回收。集合和方法列表冻结为只读，不能在激活后被修改。

激活顺序为校验协议、环境、版本、精确字节摘要及引用，验证允许的上游地址和 YARP 配置，持久化 LKG，再切换配置。`IConfigChangeListener.ConfigurationApplied` 确认实际配置已被 YARP 接受；ChangeToken 只是通知。失败恢复旧 Provider 与 LKG，不发送成功 ACK。

LKG 在节点独立目录内保存 Envelope 和原始字节，用同目录临时文件、文件 flush、原子替换和 Linux 目录 fsync 持久化。上一份有效副本另存。启动逐个验证主文件和上一副本；包括“摘要正确但代理拒绝”的情况，尝试下一份实际可用配置。写入禁止时继续旧流量，不确认新版本。所有 LKG 文件仅节点进程可读写，不能打包到公开交付物。

无可用 LKG 且远端不可达时业务请求返回 503。已有 LKG 时控制面和 Redis 故障不会停止既有允许流量；故障期间不能声称撤销策略已经同步。网关保留 `/health/live`、`/health/ready` 两个运维路径，其他健康前缀业务路径仍受正常准入与认证约束。

API Key 从 `X-API-Key` 读取，校验 accessKey 和 SHA-256、应用状态、凭证有效时间及 API 授权有效时间。时间按实际取得路由后的准入时刻检查。转发前剥离此 Header；测试后端仅报告是否观察到 Header，不返回原值。内部版本及 trace Header 在网关覆盖，客户端不能伪造后端观察到的部署序列。

Destination 权重实际参与 RoundRobin、Random、LeastRequests 和 PowerOfTwoChoices；FirstAlphabetical 保持按标识排序选择第一个健康候选。YARP 负责健康可用候选及代理请求，网关对候选应用权重，再交给 YARP 转发。Route 的 timeoutMs 使用实际请求超时设置。

更新同时使用 Redis 通知和定时读取控制面目标；控制面暂时不可达时可使用 Redis 中完整的已提交目标。ACK 丢失会重试同一 Envelope。服务级测试运行两个真实 Kestrel 网关、真实控制面、真实 PostgreSQL 与 HTTP 后端，节点独立 LKG 目录跨重启保留；独立容器与持久卷故障验收在整套 Compose E2E 中完成。

依据：[YARP Provider 生命周期](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/config-providers?view=aspnetcore-10.0)、[2.3 应用回调接口](https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Configuration/IConfigChangeListener.cs)。文档说明接口；发布成功仍以真实代理响应及节点 ACK 验证为准。
