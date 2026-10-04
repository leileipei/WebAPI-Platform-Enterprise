# 本机隔离观测栈

此配置用于已批准的真实监控与告警阶段。本步骤证明 OTLP 协议与三种真实数据源可互通；网关请求采集、权限查询、六页管理界面和告警闭环分别在后续任务验收。

## 固定版本

| 组件 | 版本 | 验证范围 |
| --- | --- | --- |
| .NET SDK / Runtime | 10.0.401 / 10.0.12 | 当前固定 SDK 容器 |
| OTel SDK / Extensions.Hosting / OTLP Exporter | 1.19.1 | NuGet 锁定还原、.NET 10 编译 |
| ASP.NET Core / Http Instrumentation | 1.19.0 | NuGet 稳定版，与 SDK 1.19.1 编译兼容 |
| Collector Contrib | 0.162.0-arm64 | 官方 per-arch 镜像；本机 ARM64 |
| Prometheus | 3.15.0 | 官方镜像、本机 ARM64 |
| Loki | 3.7.8 | 原生 OTLP / structured metadata |
| Tempo | 3.1.0 | `target=all` 单进程；无需 Kafka |

完整不可变镜像摘要在 [images.lock.json](../../deploy/images.lock.json)，全部传递依赖和内容哈希在各项目 `packages.lock.json`。未安装图表库。Collector 的通用 `0.162.0` 镜像标签在本机查询不可用，因此固定实际成功拉取的 ARM64 摘要；本配置不证明 AMD64 可用。

## 运行验证

使用 Node 24 或设置 `WEBAPI_NODE` 为实际 Node 可执行文件，运行 `./scripts/check-observability.sh smoke`。脚本创建随机 `webapi-obs-smoke-<uuid>` 项目、0600 临时 IP HMAC 测试秘密、独立网络及卷，发送合成 OTLP 指标、日志、Trace，然后分别查询：

- Prometheus 请求 counter 至少为 1；
- Loki 找到本次独有日志标记；
- Tempo 按本次 Trace ID 找到对应 server span。

任一失败或部分拒收都会失败，健康检查不能替代三源查询。`EXIT` 只清理精确本项目的容器、卷、网络和秘密文件，并核验零残留。默认安全证据写入本计划 `.superpowers/sdd/2026-10-04-observability-alerting-implementation/task-2-smoke.json`；可用 `WEBAPI_OBS_EVIDENCE` 指定证据文件。该 JSON 不含秘密或账号。

一个固定 SDK 的临时辅助容器只初始化本次项目的 Loki/Tempo 空卷权限和核验容量；四个常驻服务均以镜像默认非 root 用户运行。没有删除已有 core-test、本机管理系统或其它项目资源。

## 网络、容量与保留

所有诊断端口均绑定 `127.0.0.1`，由 Docker 随机分配；服务之间使用本项目自己的 bridge。此 Docker 运行时的 `internal` 网络没有生成主机端口映射，故本机诊断使用普通 bridge；它不提供出口网络封禁，生产部署需要单独落实网络策略、TLS、身份认证与容量保障。

Prometheus 仅抓取 Collector，采集周期 2 秒供本机短测试使用，配置 7 天 / 2GB 双保留限制。Loki 与 Tempo 均配置 168 小时保留目标，其独立卷为每卷 1 GiB 的有界 tmpfs，脚本实际检查文件系统容量。Collector 配置 128 MiB 内存限制、每批最多 512 条、导出队列 1024 批请求、最长 30 秒重试；四种服务也配置容器内存上限。

Loki/Tempo 的临时卷在 Docker 虚拟机重启后丢失；用于可销毁协议和闭环验证，不能作为生产持久存储。达到容量上限时，7 天目标可能无法达到。生产必须配置有配额的持久卷、备份和实际覆盖检查，不可把“168h 配置”当作已收录完整 7 天数据。

Loki 只把 `service.name` 和可信 `webapi.environment.id` 设为索引标签。Collector 只把 service、实例与环境三个指定资源属性转换为指标标签，版本、发布序号、Trace ID、IP 不进入指标资源标签。

## 后续配置契约

应用配置键：`Observability:Enabled`、`CollectorEndpoint`、`PrometheusUrl`、`LokiUrl`、`TempoUrl`、`CredentialSecretFile`、`IpHmacSecretFile`、`TraceSampleRatio`。查询地址只由服务端配置提供，客户端不能指定数据源 URL。秘密以文件提供，不进 URL、日志或截图。

OTel 1.19.1 自观测通过 `otel.sdk.experimental` meter 可记录处理器队列满丢弃，但不覆盖导出失败。后续接入必须提供平台稳定的队列满与导出失败计数，并通过队列饱和、Collector 不可用测试证明请求转发不受阻。

## 官方核验依据

- [OTel .NET 1.19.1 发布](https://github.com/open-telemetry/opentelemetry-dotnet/releases/tag/core-1.19.1) 与 [SDK 自观测说明](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.19.1/src/OpenTelemetry/README.md)。
- [Collector 0.162.0 发布](https://github.com/open-telemetry/opentelemetry-collector-releases/releases/tag/v0.162.0) 与 [Prometheus exporter 的资源标签配置](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/v0.162.0/exporter/prometheusexporter/README.md)。
- [Tempo 3.1.0 单进程示例](https://github.com/grafana/tempo/blob/v3.1.0/example/docker-compose/single-binary/tempo.yaml) 与 [部署模式](https://grafana.com/docs/tempo/latest/configuration/#deployment-modes)。
