# 本机演示联调使用说明

用户选择 A 后，已在独立长期项目 `webapi-enterprise-local` 建立本机演示组织、项目、环境、订单查询 API、调用应用、双独立审批模板及站内错误率告警。原业务环境和已有交付包保留。本次使用已有应用镜像，没有修改产品代码、字段、权限规则或发布流程。

## 入口与账号

控制台：[http://127.0.0.1:4192](http://127.0.0.1:4192)。选择“本机演示组织 / 本机 API 联调项目 / 本机演示环境”，可查看 API、路由、后端集群、发布、节点、访问日志、Trace 与告警。

| 账号 | 用途 | 本机密码文件（工程根目录下） |
|---|---|---|
| `admin` | 平台管理与发布申请 | `.runtime/local/secrets/bootstrap-password` |
| `local-demo-api-approver` | 第一阶段 API 审批 | `.runtime/local/demo/local-demo-api-approver.password` |
| `local-demo-security-reviewer` | 第二阶段安全审核 | `.runtime/local/demo/local-demo-security-reviewer.password` |

两名审核员的密码独立随机生成，文件权限0600，范围仅限演示组织；对用户管理接口的访问均为403。密码和API Key不出现在文档、截图、Git或源码ZIP中。管理员引导密码文件暂保留，用户自行保存后再按运行手册执行`complete-bootstrap --credentials-saved`。

本机演示环境设置`isProduction=true`，用于验证现有“申请人不能自批、不同账号完成两级审批”的规则；这只是演示数据与治理流程验证，不代表企业生产环境已接入或验收。

## 调用订单 API

两个网关入口分别为`http://127.0.0.1:4196/orders`、`http://127.0.0.1:4197/orders`，需要`X-API-Key`。调用应用的私有Key文件为`.runtime/local/demo/consumer-api-key`，凭据有效期30天，具体失效时间见应用凭据页面或验收证据。两个后端只在Docker内网提供服务，使用等权RoundRobin。

在`enterprise`工程根目录执行以下命令，凭据只从私有文件读取，不显示其内容：

```bash
export WEBAPI_NODE=/你的/Node22以上版本/bin/node
"$WEBAPI_NODE" --input-type=module <<'JS'
import fs from 'node:fs/promises';
const apiKey=(await fs.readFile('.runtime/local/demo/consumer-api-key','utf8')).trim();
for(const port of [4196,4197]) {
  const response=await fetch(`http://127.0.0.1:${port}/orders`,{
    headers:{'X-API-Key':apiKey},signal:AbortSignal.timeout(10000)
  });
  console.log(port,response.status,await response.text());
}
JS
```

首次正常发布版本`1.0.0`已通过两名独立审核员审批，发布状态Succeeded，目标配置v1 / seq1，两个当前实例均ACK。实际两个入口HTTP200已验证；发布详情保留审批人与冻结目标实例记录。

## 可观测与告警结果

默认根Trace采样率10%、指标导出15秒、Worker评估15秒、评估延后30秒均保持原长期配置。短窗口真实请求已被Prometheus、Loki和Tempo收录；取得覆盖完整的指标和包含`gateway.request`、`gateway.proxy`两个span的Trace。Trace详情可关联日志。

本次最终联调批次执行874次真实请求，包含故障阶段的5xx。仅停止内网示例后端A制造故障，规则`error_5xx_ratio > 0.05`按60秒窗口、持续30秒触发；随后确认告警并恢复后端，系统自动将事件由Open → Ack → Resolved，解决原因Recovered。触发、确认、解除均有审计记录。两个示例后端目前运行，故障已结束。规则继续启用，仅站内通知，无外部通知渠道。

首次探测携带了不存在对应父span的外部Trace父标识，系统正确返回Partial；探测脚本错误地要求Available，已改用真正的根请求再验证。没有修改产品逻辑或隐藏这些原记录。

新建部署尚未覆盖默认“近1小时”窗口时，监控UI会显示覆盖不完整并保持指标空值；日志与实际可见span仍可查询。完整覆盖需要足够的运行历史，短窗口API验收结果不能等同于整小时UI覆盖。后端主动健康检查未启用，其健康显示Unknown/Disabled，HTTP200不被用于伪造Healthy。

## 日常运行与证据

```bash
./scripts/local-runtime.sh status --json
./scripts/local-runtime.sh stop
./scripts/local-runtime.sh start
```

普通启停保留数据和秘密。Docker需保持运行；镜像使用`unless-stopped`。重启网关后可用LKG恢复代理，但旧发布ACK仍冻结于旧实例，状态可能Degraded；新实例需按正常独立审批发布并取得新ACK，详见[独立运行环境手册](local-runtime-runbook.md)。

本次长期环境证据为[demo-activation.json](../evidence/local-runtime/demo-activation.json)，安装状态为[installation-status.json](../evidence/local-runtime/installation-status.json)。历史随机项目完整冷恢复验收、V1/V1.1交付封存证据仍保留；本次没有重新运行全套产品测试或覆盖源码ZIP。保全核对包括旧容器身份、镜像、卷挂载、4180/4181/5090入口及旧ZIP/manifest哈希。

本机演示已完成真实联调；企业实际上游、生产TLS/HA、容量、长期稳定性与外部通知接入另行验收。
