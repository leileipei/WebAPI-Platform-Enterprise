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
