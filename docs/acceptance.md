# 核心闭环验收

验收对象是已确认首期真实核心，原 40 页高保真原型仍完整保留。各页真实接入及后续能力以 console-coverage.md 为准；后续模块不显示模拟成功。

## 当前证据

|验收层|证据|实际覆盖|
|---|---|---|
|管理 API / PostgreSQL|完整集成回归 90 项，0失败0跳过|权限∩Scope∩有效状态、Cookie/CSRF、事务审计、ETag/幂等、导入/凭证、冻结审批、Outbox、真实原字节回滚|
|浏览器，1440×1024|evidence/m4-console/verification.json 和对应截图|真实登录与重登、只读直达403、撤Scope清缓存、412保留输入/显式采用最新修订、非法导入保留原文、封存禁编辑、工作路由不改运行流量、一次性Secret关闭不回显|
|审批与节点浏览器|evidence/m4-console/release-flow.json|两个独立Cookie审批会话，两真实Gateway确认后成功，独立回滚重新审批；1/2确认保持Publishing，超时Failed保留真实部分状态|
|真实容器跨服务|evidence/core-loop/scenarios.json|A→B 两网关业务响应；历史回滚A且序列更高；已进入A的请求在切换时仍完成A，新请求B|
|容器故障注入|evidence/core-loop/faults.json|通知丢失走poll；ACK丢失重试；LKG不可写保持旧流量/独立恢复；实际Key到期与跨API403；坏hash/外环境拒绝；CP/Redis停机下真实进程重启LKG；并发发布/回滚仅一者成功|

`complete:true` 只表示该证据文件所列检查通过。源码构建、数据库迁移、单元/集成、浏览器及真实容器分层记录，彼此不替代。最终汇总见 evidence/core-loop/verification.json；若文件缺失或 complete=false，该项尚未完成。

当前全分支审查发现的3项Important已完成代码修正；9项前端自动化与完整后端回归通过。真实浏览器复验尚有待办（旧测试页确认框阻塞），见 evidence/core-loop/final-review.md；最终汇总仍为 complete=false。

## 交付与未验收范围

交付包含源码、32张核心源表与补充发布/身份/幂等表、EF迁移、集中版本和锁文件、固定镜像摘要、Compose、管理前端、协议与运维说明及脱敏证据。打包排除任何密码/API Key/Secret、数据库卷、.runtime、.secrets 与依赖缓存。

在本机 Docker Linux ARM64 验证；AMD64 编译与运行尚未验证，企业实网/TLS、真实数据库恢复、容量/SLA、HA、灾备、外部SSO与企业适配未验收。没有 5k RPS 或生产上线结论。

后续业务范围：完整企业指标工作台、监控/告警、系统设置、标签运营、完整 Breaking Change 分析、复杂策略编辑器、SSO/首次改密、URL/YAML OpenAPI 导入等，详见40页映射。原高保真交互演示的完整页面数量不等于全部业务后端已经上线。
