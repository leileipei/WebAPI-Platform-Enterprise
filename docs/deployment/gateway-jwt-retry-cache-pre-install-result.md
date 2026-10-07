# JWT、重试与缓存：固定版本安装前验收

本批软件提交 `c1c271555e3a125d22184697c17320c94a4db07c`，实际镜像 `sha256:bc07e5333e135ff4672fc2934e5e8076e619744185728174d205e3f8231b8b1b`。这是安装前验收，原 4192 尚未升级。

五组完整测试：Domain 642、Integration 541、Gateway 138、Console 174、Runtime 134，共 1629 项，零失败、零跳过；Console 类型检查及 Vite 构建通过。全部日志来自该提交 Git archive，逐项日志、SHA 与镜像内部标签经门禁核对。

原部署完成冷备并恢复服务。UUID 恢复克隆完成 11 项真实场景：两级审批与双 ACK、JWT 401/403、身份分区和双节点共享 Hit、三次物理重试、不确定送出不重试、限流只扣一次、半开一次、Redis 故障旁路、Scope 隔离、desired 和两节点 LKG 降级门禁、配置回滚。软件另在专用恢复克隆完成候选→旧软件→候选，新增工作数据保留，未回灌旧数据库；该专用克隆已按 owner 清理。

CUA 完成 1440/1280 两个实际宽度的 12 项操作，13 张原始截图：新建/编辑、文件选择器公钥导入、应用映射、六类绑定、无效 JSON 脏草稿、Escape 焦点、Enter 显式提交、连续 Scope 切换、真实 412、只读账号、完整发布 Review、Loki Hit 的 0 次尝试及 Tempo 的 3 个 Client span。Scope 检查采用真实连续切换，没有声称人为强制网络延迟。发布 Review 未再创建发布。

唯一整分支独立审查发现的五项 Important 已在同一修复批处理，并由行为 RED→GREEN 和上述全回归验证：maxAttempts=1 单次超时、YARP timeout 分类、Redis 等数量异成员索引、无效 JWKS 草稿、源只读/目标可写复制入口。无未解决 Critical/Important、无延期 Minor。额外混合权限账户实操暂未执行，复制权限规则由回归和服务端校验覆盖。

边界：JWT 为冻结公钥 RS256/ES256 离线验证，不远程取钥；重试仅无请求体 GET/HEAD，最多三次；缓存为有界 HTTP 子集，默认 100ms Redis 预算，故障旁路；生产持续吞吐/时延 SLA 未验收。第三批外部通知和跨环境审批尚未实施。

公开材料位于 `docs/evidence/gateway-jwt-retry-cache/`，不含密码、token、HMAC、转储。安装与包交付由下一任务另记实际结果。
