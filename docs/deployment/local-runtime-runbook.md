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

## 离线完整备份与恢复演练

备份包含真实业务数据、会话密钥和运行秘密。保存在本机私有目录或加密介质；源码交付包不包含这些文件。使用与备份相同的架构、PostgreSQL大版本、应用提交与依赖镜像。先保留原项目，恢复到全新的随机项目中核验，工具拒绝覆盖长期项目或已有目录。

在工程根目录执行以下完整冷备份。它持有运行目录锁，校验owner/project；先停应用、网关、采集器、三源和示例后端，保留PG运行以生成逻辑dump，然后停PG/Redis并归档七个持久卷。成功后环境保持停止状态，可显式start。备份目标必须不存在，权限0700；归档/配置0600，清单逐文件SHA256。

```bash
"$WEBAPI_NODE" --input-type=module <<'JS'
import fs from 'node:fs/promises';
import path from 'node:path';
import {loadState} from './scripts/runtime/state.mjs';
import {loadRelease} from './scripts/runtime/lifecycle.mjs';
import {prepareContext} from './scripts/runtime/context.mjs';
import {createBackup} from './scripts/runtime/acceptance-backup.mjs';
const directory=path.resolve('.runtime/local');
const state=await loadState(directory);
const release=await loadRelease(path.join(directory,'release.json'));
const parent=path.resolve('.runtime/backups');
await fs.mkdir(parent,{recursive:true,mode:0o700});
const target=path.join(parent,new Date().toISOString().replaceAll(':','-'));
await createBackup(await prepareContext(directory,state,release),target);
console.log('备份目录：'+target);
JS
./scripts/local-runtime.sh start
```

备份内容：`postgres.dump`（逻辑恢复备用）、`pg.tar`（已停止PG的完整物理数据）、`lkg-a.tar`、`lkg-b.tar`、`dp-keys.tar`、三源数据tar、`runtime.json`、`release.json`、`secrets/`及`backup-manifest.json`。物理PG与逻辑dump是两种替代恢复方式，不能叠加导入。当前演练实际使用同版本物理卷恢复；逻辑跨版本迁移未验收。Redis缓存不备份，不影响持久业务真值。

将下面`RUNTIME_BACKUP`设置为刚才的目录。需保留release中记录的Git提交和本机镜像；恢复过程先核验所有文件哈希，再创建新owner、目录、卷，将七份归档恢复到空卷。所有秘密继承备份；控制台cookie命名空间使用新owner，恢复后重新登录。演练端口自动选择空闲loopback端口，并输出实际入口。

```bash
export RUNTIME_BACKUP=/完整路径/备份目录
"$WEBAPI_NODE" --input-type=module <<'JS'
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {restoreColdBackup} from './scripts/runtime/acceptance-backup.mjs';
import {freePorts} from './scripts/runtime/acceptance.mjs';
import {prepareContext} from './scripts/runtime/context.mjs';
import {operateRuntime} from './scripts/runtime/lifecycle.mjs';
const projectName='webapi-enterprise-local-test-'+randomUUID();
const directory=path.resolve('.runtime/tests/'+projectName);
const restored=await restoreColdBackup({target:path.resolve(process.env.RUNTIME_BACKUP),
 projectName,directory,ports:await freePorts(),contextFactory:prepareContext});
await operateRuntime('start',restored.state,{directory});
console.log(JSON.stringify({directory,projectName,consoleUrl:
 'http://127.0.0.1:'+restored.state.ports.console,ports:restored.state.ports},null,2));
JS
```

在新入口用备份中原管理员账号登录，核对组织/环境/API、告警规则与事件、原时间范围内日志/Trace，并用两网关检查原LKG代理。确认完整后再制定正式切换步骤；当前工具提供隔离恢复验证，不自动替换原运行卷。演练项目的删除使用`cleanupFixture(明确演练目录)`，它再次核验owner/project与已知卷名单，只删除该随机项目；普通启停不删除卷。

## 验收命令与证据

`check-local-runtime.sh unit`运行脚本和控制台测试；`integration`串行运行真实容器测试；`e2e`（`browser`同义）运行真实发布、独立审批、两网关请求、三源收录、告警、源/Worker故障、重启、控制面重建和冷恢复。最后停在页面QA等待点，需要实际浏览器操作并写入UI证据，随后才清理随机项目和出具complete证据；无人值守运行不能冒充UI已通过。

临时覆盖配置只属于验收项目；长期环境保留原采样、导出与告警间隔。内存证据为每5秒Docker stats采样的观察峰值，不能作为压力测试容量或内核精确最高值。旧环境保全证据比较容器ID、镜像、卷挂载、旧入口状态和旧ZIP哈希，不宣称逐行检查了全部旧业务数据。
