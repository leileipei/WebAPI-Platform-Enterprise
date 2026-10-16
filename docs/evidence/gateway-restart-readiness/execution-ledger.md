# 网关重启就绪修复执行记录

2026-10-06：用户确认当前实例独立运行确认方案，授权开发/隔离测试/一轮审查；4192升级仍单独审阅，不自动提交。工作树feature/gateway-restart-readiness从acb10a5隔离建立。

根因现场：Succeeded发布目标2、成功历史ACK2、当前实例ACK0；节点v4/seq4心跳Ready，原平台aggregate Degraded。原数据只读，当前运行未改变。

基线166工具/console和24 ACK测试通过。新增真实成功发布后重启API测试RED，实际409而期望200；修复后39 ACK测试通过。新增真实PG维护SQL测试RED，实际Degraded期望Ready；修复后14检查通过。

运行确认使用现有节点Metadata与runtime_applied事件，仅新当前实例对Succeeded/Published/Active且当前desired的精确快照成功应用可记录；旧冻结Publishing规则不变，历史ACK/目标/发布/成功audit字节序列化比较保持。重新注册清除receipt，重复poll去重。

全量工具回归暴露既有lockSerializesWriters竞态，持有者释放后safeFile ENOENT。确定性真实文件测试通过EEXIST后释放复现，修复仅重试缺失锁。首轮夹具未realpath，产生60秒超时，已纠正；该超时不计为合法RED。基线旧state在独立临时副本中RED证据为red-lock-proof.log；最终167工具/console通过。

全量C#回归通过：Domain196、Integration362、Gateway52，总610，0失败0跳过。新增双网关LKG重启/再次应用/真实流量成功，并仅产生2运行事件和原2发布ACK；没有新增发布。14真实PG SQL检查通过，临时库清理。外部Compose EndToEnd整批未执行，不把本次进程内真实HTTP/PG网关测试称为生产或原4192升级验收。

追加锁竞态修复属于本批回归发现的维护安全性缺陷，已公开说明范围，不触碰原运行目录。一次独立只读审查进行中，源码未提交，持续运行仍原0b42/v4/seq4。

用户确认后的一次独立审查完成：Critical0、Important0、Minor1。作者对唯一JSON类型Minor写真实PG红测试（错误类型仍Ready），改为JSONB严格值比较，20检查GREEN。补充3并发真实API用例验证1事件/2重复和额外Metadata保留；这是原环境锁行为覆盖，未声称该测试本身RED。

最终全量C#611（Domain196/Integration363/Gateway52）、167工具/console、20真实PG查询全部通过；各运行均已读回日志及TRX计数。没有第二轮独立复审。新维护查询对原4192真实schema只读通过，仍原0b42/v4seq4 Degraded，无receipt、无切换。追加升级审阅文档，待最终一次本地提交与冷备份切换授权。

审查未判断事项处理：升级/生产明确未执行；receipt使用当前身份/desired/hash与fresh心跳证明持续运行，不额外加入定期无故失效；其它既有锁替换竞态未扩大，不能声称全部解决；独立审查只读取日志，实际写测试由主执行者授权执行。唯一Minor已解决，无暂缓Minor。

封装独立修复补充包：9个已审阅源码/测试按commit-scope摘要逐文件固定，附升级前结果/运维/升级审阅及脱敏证据。15份历史ZIP逐项SHA不变，不含私有.runtime、数据库/密码/会话/依赖/binaries。扫描当前原平台与IdP已知秘密值，ZIP CRC和manifest文件SHA读回，最终包外seal记录摘要。未进行本地提交或4192升级。
