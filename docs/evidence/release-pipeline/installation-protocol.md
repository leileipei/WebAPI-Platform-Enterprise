# P14 受控执行边界（供唯一最终审查者同时评估）

状态：准备，未对原实例安装。产品候选 b1323c3a41dfdeb2008e0c9fdc811e4004067ac8 / sha256:f801fb31f83253d372bc7b7cfbaa3097324971df1929d31bbe1dddfeea622b42。若最终审查需要产品修复，必须改为重新全量验证和真实验收的新固定候选。

- 原 owner 6d7e3fda-88ea-4315-a8ef-df9e7cd995c8，项目 webapi-enterprise-local，目录主仓库 .runtime/local；4192/4196/4197。原已装源码 a445534f82f11ae9a45c3b2904bd07108bd165a9，原主仓库 HEAD 40759aac50f1a6281223b0ec0d45d58e6fb44cb5 及 dirty 内容不合并、不提交、不推送。
- P13 prove/verify、全量测试以及 Critical/Important 最终审查处置闭环才可进入维护。原实例不得自动激活、新建运行或产生业务发布。
- 先按 captureOriginalRuntime + capturePipelineTables 取得 fresh baseline，存于隔离 worktree 的私密 .runtime 安装记录。旧表使用实际有序主键、原列规范化哈希；静态旧行不得改变/删除。Gateway 实际版本与序列仍受保护，只有现有显式心跳/实例/评估租约列排除。原报告卷所有文件摘要（即便没有已登记报告）保全。额外保存 IdP owner 资源身份和 4193/4180 的监听进程/入口观察。
- 旧私密文件全部保全；允许变化只限 release.json、runtime.json 中已验证的 releaseId、runtime-config.json 的固定应用镜像及真实 Console proxy IP、console-proxy-trust.json 的已观察 Console 容器绑定。Secret、IdP、SSO、通知、缓存、凭证和旧维护工具不可借此放行。原卷/网络和非应用容器身份保持；仅固定应用镜像服务可替换实例，保留实际配置/卷绑定/端口。
- 检查 Building/Publishing=0、未处理发布 outbox=0、无 pendingConfiguration、原状态 Ready 后，经已有 owner 验证及维护锁调用 createBackup；备份在本机原 .runtime/backups 新私密子目录，报告/数据库/SSO/cache/login/通知支持按实际上下文包含。单独 backupIdp，逐文件回读 backup-manifest 摘要，备份结束再次 Ready 和旧事实保全。
- 用已验证 initializeRuntime / operateRuntime 机制及固定 release manifest 维护；不从 dirty 主目录构建。Migrator 应用两个增量迁移，再显式 dotnet /app/migrator/WebApi.Migrator.dll --seed-catalog。新 Pipeline 六表必须为空、旧新增 link/default 列符合 pipelineAddedDefaults；迁移历史仅增加本期两项。目录仅新增三项 pipeline 权限及 PlatformAdmin 的三条默认授权，原显式授权完整。
- 每个维护/登录/SSO/备份操作有独立命令记录、真实开始结束时刻、owner、固定候选及观测来源。新增行只允许本期迁移/目录和真实操作带来的精确 ID/哈希/命令关联回执；不可把 after 全部新增行直接作为无条件允许集。新业务行、任意幂等或未知审计都拒绝。节点新增事件须绑定原 nodeId、真实新实例、原配置/序列和对应重启操作；登录回执须绑定实际账号/trace 或实际 SSO attempt。必要的持续观测变化先单独采证和裁决，不能泛化整表放行。
- 实测五个主应用容器 image + 全部 DLL 文件和 HTTP 静态文件摘要；演示后端/通知 fixture 若同固定包会换实例，也必须明确核对 owner、服务、绑定和固定镜像。原 Ready、原 v/seq=4、原业务凭证两个 Gateway 调用结果保持。
- 原 4192 仅实际登录/SSO及只读定义/运行导航检查，Viewer 权限受限；逐张真实截图检查。不冒用 P13 独立业务事实为原业务闭环。
- 安装后再 cold backup，新 owner/新端口克隆 restoreColdBackup，核对历史、配置、所有已登记报告字节及两节点原凭证调用，保留 backup 摘要、按精确 owner 清理 0。原 IdP 不挂到克隆；克隆的 SSO 按原恢复机制隔离/关闭，新通知端口显式重绑定。
- 新工具包以审查时固定 tooling SHA 的 scripts/deploy Git archive 创建于原私密新目录，通过 createPipelineMaintenance 仅新增 manage-pipeline.sh 和本期 checksum 清单，旧入口与旧包逐文件不变。wrapper status 和 owner-aware backup 可用，不能绕过已验证备份机制。
- 若维护失败，先保留私密失败后事实和已验证备份，恢复原固定旧包/原已备份库，保留 Secret/报告/业务事实。恢复时必须原 owner/原卷、应用和数据服务停止、备份 SHA 回读；不得使用 clone restore 接口借用/收养 persistent owner，也不得删主目录或原卷。失败恢复不得冒称安装成功。
- 最终 verifyPipelineInstallation 必须通过：旧行/报告/私密文件/旧工具/身份/原 vseq/显式授权保全，固定新源包/镜像/静态文件/Ready、精确新增回执、仅 Admin 三项新权限。企业 DNS/TLS/LB 和真实企业业务验收仍未执行。

实现/检查接口：scripts/delivery/pipeline-installation.mjs、pipeline-inventory.mjs、pipeline-maintenance.mjs、pipeline-preservation.mjs；沿用 scripts/runtime/lifecycle.mjs、acceptance-backup.mjs、keycloak-demo.mjs 和 gateway-policies/delivery.mjs 的原 owner/锁机制。实际执行记录必须据实封存；此协议不充当安装证据。
