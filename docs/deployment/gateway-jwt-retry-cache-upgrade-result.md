# JWT、重试与缓存：4192 升级验收

2026-10-07，本机原部署已升级并实际验收。入口 http://127.0.0.1:4192/policies 。运行软件提交 `c1c271555e3a125d22184697c17320c94a4db07c`；镜像 `sha256:bc07e5333e135ff4672fc2934e5e8076e619744185728174d205e3f8231b8b1b`。文档和升级工具提交与运行软件身份分别记录，未把后续文档 SHA 写成镜像 SHA。

五应用容器身份一致、双网关 Ready，原业务仍为配置 4 / 序列 4，双 `/orders` 返回 200，Collector、Prometheus、Loki、Tempo 均 Available。原 admin、两位独立审批账号登录成功；原 Keycloak viewer 实际登录、只读访问、合法写请求 403、退出后 401。三个静态构建文件由原 4192 实际回读核对。

原 7 项本地修改及历史未跟踪文件保留，792 个文件哈希复核一致；动态发现 52 张表，业务配置和账号逐行哈希一致。心跳/实例/运行元数据、自动告警计算状态和 Outbox 处理字段按稳定业务列保护；审计/事件可追加但既有业务行保留。SSO 五分钟临时登录尝试按既有过期清理处理。未创建原环境 QA 导入、策略、应用映射、审批或新业务发布，未回灌旧数据库。旧 2.0 快照没有新策略运行修订信息时，界面显示“未知/需核对”，未编造运行修订。

冷备包含升级前 7 个平台卷、4 个 Keycloak 卷，以及升级后含新增缓存 Secret 的 8 个平台卷，全部清单文件 SHA 回读通过。密码、SSO 凭据、旧固定工具均保留；新增 HMAC 私有文件与独立共享卷仅供双网关只读使用。新固定维护入口为 `enterprise/.runtime/local/manage-policies.sh status|start|stop|restart|up`，116 个工具文件从运行软件提交 Git archive 固定；原 manage.sh/tooling.json 未覆盖。

安装前同一固定版本验收：1629 项完整测试零失败/零跳过，11 项真实克隆业务场景，12 项 CUA 交互、1440/1280 双宽度、13 张截图。唯一独立整分支审查的五项 Important 均经同一修复批及完整回归解决，无未解决 Critical/Important、无延期 Minor。详见安装前报告和裁决记录。

JWT 支持冻结公开 JWKS、RS256/ES256、精确 Issuer/Audience/应用映射；网关离线校验。有限重试仅无体 GET/HEAD，最多三次，受总期限、单次期限、取消、熔断与不确定送出限制。Redis 缓存按身份/部署代际分区，GET 200 的有界 HTTP 子集，部署上限 64MiB/2000 条，默认操作预算 100ms，故障旁路；发布和回滚冷启动，不保证跨 TTL 的业务强一致性。生产吞吐、时延、容量 SLA 尚未验收。

第三批外部通知（SMTP/Webhook）和跨环境审批未实施，需另行设计。本批完成 JWT、重试、缓存，第一批 URL/YAML 导入、兼容性、Schema/示例验证已保留。

交付包包含固定源码、实际部署静态原型和中文材料；源码测试保留一个以 literal signature 结尾的已知无效 JWT 测试字面量，实际密码/token/HMAC 扫描无任何例外。实际 whole SHA、entry SHA 与 CRC 回读结果见 `docs/evidence/gateway-jwt-retry-cache/package-status.json`。本轮临时资源清理结果见 `cleanup-status.json`；原服务、冷备、新旧镜像、历史工作区保留。全部本轮提交仅本地合并，未 push。
