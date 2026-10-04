# 真实监控与告警实施记录

计划：docs/superpowers/plans/2026-10-04-observability-alerting-implementation.md。基准f085857。用户已确认设计和计划，沿用Native。

Task 1进行中：查询范围7d / 100条 / 30秒时钟偏差；先观察7项失败，再观察领域全量23项通过。原真实API90项及前端9项基线通过。尚未接入采集源，尚未完成6页真实链路。

Ruling: 沿用此前确认的独立enterprise功能分支就地实施，保留原型；如需拆分可按基准提交分离。测试只读模式沿用核心真实值read，不引入read_only。

Task 1验证：新增7项RED失败源为未实现校验；GREEN全量领域23通过。原网关12通过；共享测试支持已随网关项目编译。新增契约与校验仍不代表外部采集已完成。

## Task 2：隔离 OTLP 协议栈

固定 SDK 实测为 10.0.401、ASP.NET / .NET Runtime 10.0.12、aarch64。OTel SDK / Hosting / Exporter 1.19.1 与两个 Instrumentation 1.19.0 锁定还原成功，解决方案编译 0 warning / 0 error。四种新镜像不可变摘要在 images.lock.json。

部署行为测试 4/4 通过；新增配置前实际 RED 2/2，端口校验补充先 RED 后 GREEN。最终 protocol-smoke.json 记录真实 counter=1、日志标记找到、对应 Trace 找到，Loki / Tempo 实际容量各 1 GiB；结束精确项目 0 容器 / 0 卷 / 0 秘密文件。此证据是合成 OTLP 协议验证，尚不是网关 E2E。

决策：官方 Collector 0.162.0 通用标签不可用，固定实际拉取的 ARM64 发行摘要，AMD64 未验收；此 Docker internal 网络不映射主机端口，采用独立普通 bridge + loopback 发布，生产需出口网络策略；隔离 Loki/Tempo 使用有界临时卷，持久生产存储需配额及备份；SDK 自观测不涵盖导出失败，后续接入增加稳定平台诊断。

## Task 3：网关有界采集

六项指定测试实际RED后GREEN：旧请求跨版本切换仍保留原序号/配置/实际目标，401未知应用与W3C兼容字段，已发送200头的断开不计成功，三种信号敏感字段缺失，有界队列满仍转发成功，精确健康请求不进业务量。导出失败恢复及实际YARP状态读取组件检查通过。

额外发现两项边界并观察RED后修正：70000字节Collector响应原先无内存边界，已改HeadersRead和64KiB限制；空Destination pool原先出现并未实际调用的client span，现仅实际ProxiedDestination UUID可导出client span。最终网关全量22/22通过，含原12项路由/切换/LKG检查，0 skipped。

请求记录只做SDK计数与拥有独立存储的JSON快照入队，默认容量2048、每批最多512，I/O仅在后台。健康状态来自YARP；字段、秒histogram buckets与诊断口径见contracts/observability.md。此阶段测试使用内存sink作组件观察，未宣称网关至三源E2E或六页UI已经验收。

决策：仅注册受控Gateway ActivitySource并对字段采用白名单，省去自动URL/异常/events/baggage采集；保证Gateway server到实际代理client，不包含未插桩后端内部调用。SDK指标汇总后立即复制至显式JSON队列，不保留可复用MetricPoint/Activity/LogRecord；自定义OTLP JSON适配器需后续真实Gateway闭环证明。
