# 独立本机运行环境操作手册

本阶段独立项目为 `webapi-enterprise-local`，使用新库；旧 core-test 数据与原 4180/4181/5090 入口保留。需要 Docker Compose、Node 22+ 和现有锁文件对应的前端依赖。实际验收结果以 `docs/evidence/local-runtime/verification.json` 为准。

```bash
export WEBAPI_NODE=/你的/Node22以上版本/bin/node
./scripts/local-runtime.sh build --revision <明确提交>
./scripts/local-runtime.sh init --admin <显式管理员名>
./scripts/local-runtime.sh status --json
```

首次 init 生成独立随机密钥与一次性密码，密码位于工程 `.runtime/local/secrets/bootstrap-password`（0600）。自行读取并妥善保存，勿复制到共享日志或截图。也可用 `init --admin <名称> --password-file <0600密码文件>` 提供16–1024字符密码。再次 init 不重置密码、不自动创建其他管理员，缺失长期秘密必须恢复而非重新生成。已保存凭据后使用 `complete-bootstrap --credentials-saved` 删除一次性宿主引导文件；运行服务不挂载它。

控制台默认 `http://127.0.0.1:4190`；未配置环境时网关明确为未配置。初始化不会自动创建业务组织、环境、API、规则或审核账号。后续通过正常页面建立环境，再显式绑定两网关与上游白名单。默认端口若冲突，首次 init 可用 `--console-port`、`--gateway-a-port`、`--gateway-b-port` 指定三个不同端口；不会终止端口占用者。

```bash
./scripts/local-runtime.sh stop
./scripts/local-runtime.sh start
./scripts/local-runtime.sh restart
```

正常启停保留数据库、两份 LKG、会话密钥和三源数据，不执行 down -v。容器采用 unless-stopped；Docker 未运行时服务不可用，手动 stop 后要显式 start/up。新 build 不自动替换正在运行的版本；版本与部署文件按不可变提交保存。升级前须显式备份；init 使用指定新 release-file 时先迁移成功再保存新版本，迁移失败不把镜像切回冒充数据库回滚。

本机入口使用 HTTP/Development cookie 策略；保留 CSRF/Origin 检查，内部服务仅 Docker 网络可达。三源故障和采集缺口仍会显示 Unknown/Partial，Collector 重启不会补造丢失遥测。7 天保留配置不等于已经完成 7 天实际运行验收。生产 TLS/HA、企业上游、容量与外部通知不在本阶段证明范围。

## 绑定业务环境与状态

登录4190，在组织/项目/环境页面创建 Active 环境，按既有治理规则配置权限。将上游允许列表保存为JSON数组，例如 `["https://你的企业上游.example"]`，无路径、查询或凭据。

```bash
./scripts/local-runtime.sh configure --environment <环境GUID> --origins-file <上游origin数组.json> --username <管理员名> --password-file <0600登录密码文件>
./scripts/local-runtime.sh status --json
```

需要示例后端时显式添加 `--demo`，允许列表必须显式包含 `http://backend-a:8080`、`http://backend-b:8080`；不会自动创建API或规则。两个示例后端仅内网可达，无控制接口。已有绑定不能直接换成另一环境；相同绑定重试不轮换密钥。

状态区分未配置、已注册、Ready、Degraded及Stopped。只有当前实例、期望版本/序列与真实ACK一致才是Ready；源异常、缺节点、停用环境或过期心跳不会显示完整正常。Gateway可从LKG恢复实际代理，但历史发布ACK冻结于先前实例；新实例不会改写旧ACK。恢复后的新发布仍走正常独立审批，再取得当前实例ACK。`acknowledged` 与节点运行版本应分别查看。

新控制面使用独立Session/CSRF cookie名称，避免相同127.0.0.1主机不同端口间覆盖旧控制台登录。
