# 真实观测与告警运行手册

当前交付证明本机Linux ARM64真实业务链路及桌面UI；原40页原型4180、持久控制台4181及首期源码包保留。没有自动重配原持久控制面的数据源。浏览器只访问Control Plane，不持有源凭证。

## 构建与可销毁验证

需要Docker Compose、Node22+与pnpm，后端固定SDK10.0.401/Runtime10.0.12由镜像提供。前端在console执行pnpm install --frozen-lockfile，然后从工程根目录执行 scripts/check-console.sh（可用WEBAPI_NODE/WEBAPI_PNPM指定工具路径）。迁移包含原核心加告警增量，无演示规则或自动扩大原管理员权限。

工程根目录的验证入口：

```bash
./scripts/check-observability.sh smoke
./scripts/check-observability.sh e2e
./scripts/check-observability.sh faults
./scripts/check-observability.sh browser
```

smoke是合成OTLP协议证据；e2e是真实发布、业务请求和三源/规则闭环；faults在完整e2e基础上停止和恢复实际容器，检查代理、发布/回滚/LKG和租约接管；browser保持该随机夹具供浏览器操作。不得并发执行后端构建，因为共享bin/obj。不得设置WEBAPI_OBS_EXPECT_EMPTY或WEBAPI_OBS_ONLY_BOUNDARIES后把调试命令当完整验收。

browser输出本次observability-context.json位置，文件含各随机loopback入口；同目录context.json包含临时业务对象ID，password是0600合成临时账号密码（不要输出到共享日志或截图）。临时管理账号e2e-admin，独立审核账号e2e-approver/e2e-security；不是原持久local-admin。在浏览器使用本次console入口，按需要把查询窗口缩为已收录范围：新栈的1h包含缺历史，Partial是正确表现。结束只删除这个随机目录的browser.wait，等待父脚本退出并核验cleanup。不要删除整个.runtime或其他项目。

脚本使用随机项目标签webapi-enterprise-e2e-observability-<UUID>、预分配本机端口、独立PG/Redis/LKG及源卷、0600秘密。正常EXIT清理精确本项目；Compose无效overlay时使用同一精确标签后备，卷额外受已知名称白名单约束。若终端被强杀，先核对该次UUID及docker项目标签，再仅清理该项目容器/网络/已知卷及其合成秘密；禁止全局prune、删除全部e2e或触碰持久数据。最终需容器0、卷0、秘密文件0；退出失败不能据ready或局部PASS宣称验收通过。

## 实际部署配置

部署使用同一网络中可达的Collector/Prometheus/Loki/Tempo地址，或分别提供明确可达的内网地址。配置来自服务端，不接受浏览器URL或原生查询。镜像摘要固定在deploy/images.lock.json；compose.observability.yml是单进程本机配置，Loki/Tempo各1GiB tmpfs，不可当作生产持久栈。

|服务|配置|
|---|---|
|Gateway|Observability:Enabled=true；CollectorEndpoint；IpHmacSecretFile；TraceSampleRatio；可选CredentialSecretFile|
|Control Plane|Enabled=true；PrometheusUrl/LokiUrl/TempoUrl；IpHmacSecretFile；CursorSigningSecretFile；TraceSampleRatio与Gateway一致；可选CredentialSecretFile|
|Worker|Enabled=true与PrometheusUrl等源配置；Alerts:IntervalSeconds/QueryDelaySeconds/LeaseSeconds/MaxConcurrentEvaluations|
|所有配置|JSON用冒号层级；环境变量用双下划线，例如Observability__Enabled|

IP HMAC与游标签名文件分别为至少32字节随机值的标准Base64文本，文件权限0600，挂载只读；节点注册秘密独立且唯一。不要复用真实业务凭证作为采集密钥，不把秘密写入源码/URL/截图。CredentialSecretFile是可选源Bearer文本；此本机无鉴权源只绑定127.0.0.1，目标企业部署必须另验TLS、认证与网络控制。

默认Gateway队列2048、每批最多512、批等待1000ms、指标导出15000ms、根Trace采样0.1；合法QueueCapacity1–2048、BatchDelayMs10–5000、MetricExportIntervalMs100–60000、TraceSampleRatio0–1。指标和日志不随Trace采样削减；远端父节点采样决定仍有效。不能因100%本机采样声称每条企业日志均有Trace。

默认Worker槽15秒、源查询延后30秒、租约30秒、并发4；合法范围分别1–60/0–120/15–120/1–16。多个Worker共用同一PG并使用一致设置；PG时钟决定slot与过期。业务发布/Outbox与告警评估/静默到期是独立循环。

## 源异常、队列与覆盖

普通源查询超时/超8MiB/协议失败503 observability_source_unavailable，返回安全sourceType与重试信息；UI清除旧数值/详情，恢复后重新查询。监控、API详情、日志和Trace每30秒自动重验固定查询窗口，服务端权限或范围撤销后清除结果；会话刷新失败也不能继续展示旧值。API/应用后端健康维度暂不支持，显示—。无权限403/资源404/Scope停用409不是源异常。规则只读测试源故障返回200 Unknown，必须检查EvaluationState/Condition而不是只看HTTP200。

Collector不可用不阻断代理；队列满丢弃遥测，webapi_telemetry_dropped_total、webapi_telemetry_export_failures_total及webapi_telemetry_last_loss_timestamp_seconds提供诊断。三信号预先导出零基线，首次丢失时间使短中断也可识别。修复网络/凭据/源容量并检查webapi_telemetry_last_observed_timestamp_seconds是否持续推进。启用节点的Ready/NotReady/Degraded均期待采集，缺节点或45秒过期显示Partial；不得通过忽略故障节点制造完整覆盖。首尾与15秒离散的中间年龄检查不能代替毫秒级SLA。

已有缺口在相应历史窗口中保持Partial，当前采集恢复不补造丢失日志或Trace。Prometheus近似增量、histogram估计与逐请求账本不同。没有启用采集节点为NoData，不是空集合完整。大批量同纳秒Loki关键词格式化查询可超过10秒应用预算而503；缩小时间/资源范围，不能据本机5k边界查询宣称容量通过。

日志下一页使用绑定范围与固定end的签名游标；超过源5000条或同纳秒64边界ID不能完整翻页，明确truncated且停止nextCursor。导出当前过滤CSV，截断与掩码保留。IP密钥轮换改变精确HMAC过滤覆盖期，需向使用者说明旧期不可由新密钥匹配。

## 持续告警与人工处理

Collector接收200不证明日志/Trace已持久化。必须保留collector.yaml内8888的内部诊断reader及prometheus.yaml中的webapi-collector-diagnostics job；发送失败/拒绝/入队失败和诊断采集中断使对应信号Partial。共享Collector缺环境归属时保守影响所有查询环境，短暂重试失败也可能标为Partial；独立Collector可缩小影响。

源Unknown/缺样本中断Pending，下一次有效true重新开始forSeconds；Firing在Unknown保持原事件并标数据状态，不当Recovered。采集完整新鲜时RPS=0为Known，低流量规则可成立，高流量规则可恢复；比例/时延无样本仍Unknown。默认只评估当前槽，不补追历史槽。规则宽范围逐Active环境独立评估，未来环境也纳入，不放宽Scope。

Ack保留故障事件；Silence15m/1h/4h/24h必填原因，到期由独立DB时钟循环推进一次transition/审计。Silenced期间真实恢复仍Resolved。人工Resolve进入SuppressedUntilRecovery：持续true不重复创建，只有处理后的有效false解除抑制，之后新true达到forSeconds生成新occurrence。旧观测不能提前解除抑制。

修改名称/通知只增加revision，不清空逻辑评估。逻辑/Scope/目标/级别/持续或启停改变会推进logic_revision并结束旧逻辑活动事件；先看确认面板影响。提交需If-Match，412保留输入并显式采用新版本，不自动重试覆盖。事件revision用于生命周期命令并发；最新评估元数据由行锁/lease token保护。

Worker停机超过2个槽间隔显示Unknown，保留历史有效观测但当前值/条件为空。重启允许PG租约过期后接管，旧进程迟到不能改新token或已禁用规则。不要直接编辑lease/event表“修复告警”；用规则/事件管理命令保留审计。

外部Email/Webhook/EnterpriseIm仅意向，没有发送、回执、URL/密钥配置。站内告警中心已接入真实PG事件。

## 证据与交付边界

verification.json是最终门槛；fault-run.json保留独立完整故障项目和零清理结果。browser-qa.json记录真实六页1440px、正常错误0、故障会话分开及精确清理。UI协议测试证据与真实源证据分开命名。Domain49/API184/Gateway25/前端35是Task14结果，最终审查修正后的新增计数见final-review和verification。

本机短E2E设置：窗口60/持续4秒/槽2秒/延后5秒/租约30秒/导出1秒/采样1/队列64；原阈值错误>0.05/P95>1000/健康>0保留。300/600/120秒原持续语义用领域时钟验证，未实际等长运行。生产TLS/容量/SLA/HA/持久7天/AMD64/企业数据库备份恢复/企业端点/外部通知仍未验收。

独立观测交付使用scripts/package.sh observability <不可变commit>。ZIP包含源码/锁文件/增量迁移/配置/中文手册/实际证据/同一提交重建的静态页面；manifest记录源提交、静态产物哈希、ZIP SHA256及排除项。原首期核心ZIP/manifest与40页原型保留。解压不含账号密码/业务库/依赖缓存/.git；按本手册重新生成临时夹具，或为目标部署显式提供真实配置。
