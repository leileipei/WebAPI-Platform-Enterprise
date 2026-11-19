# 冷恢复硬配额验证服务接入

运行验收入口可连接独立管理员部署的本地验证服务。仓库提供客户端、接线和隔离回归。具体文件系统的取证服务及现场资格由部署方提供。缺少服务、证据或容量时，入口在创建源夹具前阻断。恢复仍须分别通过预检、初始化前和解包前的实时核验。[验收入口](../../scripts/runtime/acceptance.mjs) · [验证客户端](../../scripts/runtime/restore-quota-provider.mjs)

## 部署条件

- 在已授权的独立 Linux 验收主机上使用专用、rootful Docker Engine 和可信 Docker CLI/Compose。源夹具和恢复副本均在该 daemon，严禁指向现有安装的 daemon 或共享既有 Docker 卷。服务管理员在独立配置中固定实际 daemon 及硬配额域，来客提供的 socket 和路径只用于匹配，不决定核验范围。Docker socket 等价于主机管理权限，仅向已信任的验收操作员授权。[Docker daemon 的权限边界](https://docs.docker.com/engine/security/#docker-daemon-attack-surface)
- 使用两个不同的本地 Unix socket。两者的所有路径组件均须真实且由 root 管理，父目录不得允许组或其他用户写入，socket 权限为 `0600` 或 `0660`。使用真实绝对路径，例如 `/run/webapi-recovery/docker.sock`，不要使用带符号链接的别名。入口固定 daemon socket 和路径身份，通过受保护通道读取 API，并以同一地址调用 Docker 和 Compose。验证服务必须核对 daemon 的实际进程和挂载命名空间。代理、转发及不能证明监听者身份的 socket 激活方式不受支持。[Docker CLI 的 daemon 选项](https://docs.docker.com/reference/cli/docker/#specify-daemon-host--h---host)
- 当前协议支持 XFS 或 ext4 的文件系统项目硬配额。专用 daemon 的实际具名卷根 `<DockerRootDir>/volumes` 由同一个已启用继承的项目配额约束，并位于同一后备挂载下。部署方须隔离其他写入者、保护挂载和项目配置、保留验证服务证据，并核对内核及文件系统支持所需的唯一挂载 ID 和稳定文件句柄。源夹具、恢复副本及同项目卷元数据计入同一额度。固定容量块设备、软上限和应用日志配额不能直接作为此协议的项目硬配额。[Linux 内核系统调用手册](https://www.kernel.org/pub/linux/docs/man-pages/book/man-pages-6.17.pdf)

仅在确认独立后端和批准本次持久验收后，使用批准的发布清单、路径及四项十进制正整数字节值。两个入口均进入原有的浏览器验收步骤。

```bash
./scripts/check-local-runtime.sh e2e \
  --release-file "$ISOLATED_RELEASE_MANIFEST" \
  --restore-quota-socket /run/webapi-recovery/quota.sock \
  --restore-docker-socket /run/webapi-recovery/docker.sock \
  --max-snapshot-bytes "$MAX_SNAPSHOT_BYTES" \
  --min-host-free-bytes "$MIN_HOST_FREE_BYTES" \
  --max-target-bytes "$MAX_TARGET_BYTES" \
  --min-target-free-bytes "$MIN_TARGET_FREE_BYTES"
```

调用方可使用 `browser` 替换 `e2e`。省略 `--release-file` 时使用当前工作树的 `.runtime/local-build/release.json`。预算和 socket 无默认值。验收保留现场和报告，现有浏览器、恢复连续性与收尾检查仍决定最终结果。[运行与恢复手册](local-runtime-runbook.md#验收入口的区别)

专用 daemon 未观测原 Docker 环境，报告将原环境保全标为 `Unknown`，因此隔离结果不足以完成包含原环境保全的签核。隔离恢复和浏览器证据仍会保存。模块调用方使用此适配器时，须在其 `withDocker` 作用域内执行整个恢复；脱离作用域的恢复在预检时阻断。

## 独立读取项

| 证据 | 验证服务须独立观察的事实 |
| --- | --- |
| Docker daemon | 核对经独立配置批准的 socket 的实际设备和 inode，从连接核验实际 `dockerd` 的 peer 凭据及 PID，读取本次内核 boot ID、进程启动 ticks 和挂载命名空间 inode。通过同一连接观察 Engine ID、版本、API 版本及数据根目录。 |
| 后备挂载及硬配额 | 从 daemon 的挂载命名空间无符号链接解析实际具名卷根。通过内核读取实际文件系统设备、文件系统 UUID、不可复用挂载 ID、卷根目录 inode 和文件句柄及配额项目 ID。通过文件系统的配额接口读取项目计量、强制执行、目录继承和非零硬字节上限，排除子挂载、绑定替代及绕过项目继承的路径。 |
| 后备可用容量 | 同一后备挂载内，在具名卷根外选取真实、无项目限额影响的目录。验证探针的设备、唯一挂载 ID 和项目 ID 0，再使用 `statfs` 的可用块数及块单位计算非特权可用字节。现有门槛要求该观测值至少等于本次实际硬上限加 `minTargetFreeBytes`，硬上限同时不得超过 `maxTargetBytes`。该值为采样时的容量，物理预留和上层宿主的空间须另行验收。输入快照所在文件系统仍单独检查 `maxSnapshotBytes` 和 `minHostFreeBytes`。 |
| 全部恢复写入卷 | 预检核验已配置卷根和项目继承，并核对请求的目标范围。后两阶段在同一 daemon 逐一读取实际 Docker Volume Inspect 对象和实际写入目录，从内核复核挂载、设备、项目 ID、继承、inode 和文件句柄。只接受对应 owner 与创建身份的普通 `local` 卷，排除驱动挂载选项及重复目录。路径取自实际 inspect，并跟实际文件系统交叉验证。 |

目录文件句柄证明应包含文件系统提供的 inode generation，例如把 `name_to_handle_at` 返回的句柄类型及原始字节按服务固定编码作 SHA-256。挂载 ID 采用内核支持的 `STATX_MNT_ID_UNIQUE`。设备号、挂载 ID、inode、ticks 和命名空间不得取自计划配置或请求，服务无法现场读取时返回非 200。[Linux 内核系统调用手册](https://www.kernel.org/pub/linux/docs/man-pages/book/man-pages-6.17.pdf)

验证服务须每次重新采集，拒绝超范围、权限不足、无法核验和读取期间的替换。客户端以新的随机 challenge 绑定请求，交叉核对 Engine API、配额与卷证明。它从稳定的 daemon、挂载、配额及硬限额事实计算配额身份，并要求从只读探测到解包前一致。初始化前与解包前的每个写入目录还须保持相同的文件系统身份。受控管理员、专用 daemon 和只读实时取证是信任前提。

## HTTP 合同 v1

服务在自己的 Unix socket 接收 `POST /v1/restore-storage/verify`，使用 `application/json`。客户端限制单次请求等待时间为 10 秒、响应正文为 256 KiB，只接收 HTTP 200 和以下 JSON 对象。服务应先验证对端和独立配置，再验证并观测请求。拒绝时返回非 200。

| 请求字段 | 值 |
| --- | --- |
| `schemaVersion`、`challenge` | 版本 1、本次请求的 64 个小写十六进制字符 |
| `phase` | `probe`、`preflight`、`before-initialization` 或 `before-extraction` |
| `budget`、`dockerSocket` | 四项预算、客户端看到的 socket `{path, device, inode}`，设备号和 inode 为十进制字符串 |
| `projectName`、`directory`、`volumeNames` | 恢复阶段的目标项目、规范绝对路径和全部写入卷逻辑名。只读 `probe` 无此三项 |
| `ownerId`、`volumeIdentities` | 初始化前及解包前提供，分别为新 owner UUID 和 `{卷逻辑名: SHA-256}`。服务须独立检查实际卷和摘要 |

| 成功响应字段 | 值 |
| --- | --- |
| `schemaVersion`、`challenge`、`requestDigest` | 版本 1、原 challenge、请求原始 UTF-8 正文字节的 SHA-256 |
| `daemon` | `{socket, bootId, pid, startTicks, mountNamespace, id, version, apiVersion, rootDirectory}`。socket 与请求同结构，信息从实际连接及内核观察 |
| `quota` | `{kind, filesystem, filesystemUuid, device, mountId, mountPoint, volumeRoot, volumeRootInode, volumeRootHandle, projectId, projectInheritance, accounting, enforcement, hardLimitBytes}`。类型为 `filesystem-project-hard-quota`，文件系统为 `xfs` 或 `ext4`，设备使用 `major:minor`，项目 ID 为正整数，三项开关均为 true |
| `capacity` | `{path, device, mountId, projectId, availableBytes}`，项目 ID 为 0，单位为字节 |
| `volumes` | 初始化前和解包前各返回每个写入卷的 `{name, identity, mountpoint, inode, handle, mountId, device, projectId, projectInheritance}`，其余阶段返回空数组 |

摘要和文件句柄摘要为 64 个小写十六进制字符。挂载 ID、进程启动 ticks、命名空间和 inode 用十进制字符串保留精度，进程 ID、项目 ID 和字节值用安全整数。卷 `identity` 使用仓库定义的 Volume Inspect 摘要，客户端同时核对恢复门禁绑定的摘要与自己读到的实际对象。服务以 challenge、请求摘要和响应正文摘要保存独立证据。恢复证据链持久化响应摘要和前一阶段摘要。[卷标识定义](../../scripts/runtime/docker.mjs#L11) · [恢复证据链](../../scripts/runtime/restore-storage-budget.mjs)

## 现场验收

后端须在已授权的独立夹具证明真实的合计写入上限、确切的挂载与所有动态卷覆盖、容量采样口径及所需的重启持久性，再执行真实冷恢复和浏览器验收。缺失挂载、错 daemon、预算不足、换卷或 provider 失联均须验证阻断并保留证据。仓库回归用自有 Unix socket 和合成的 daemon/文件系统观察验证协议和门禁，物理配额及现场结果待独立取证。[既有现场验证计划](../superpowers/plans/2026-11-05-independent-persistent-storage-verification.md)
