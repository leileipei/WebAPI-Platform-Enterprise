# 后端自动检查第二批设计

承接用户已选 A（前端先、三个后端后）及 2026-11-10“继续”指令。本批在本地准备并保存；用户选择“先保留本地提交”继续有效，不推送、不创建 PR、不启用远程规则。

## 实现范围

- 独立工作流 `contracts-ci.yml`，三个稳定检查名：`contracts-domain`、`contracts-integration`、`contracts-gateway`。
- 三个独立 GitHub 托管 `ubuntu-24.04-arm` job，由固定枚举矩阵生成；`fail-fast: false`，每套 45 分钟。
- 复用未修改的 `scripts/check-contracts.sh` 与两份 Compose 文件，继续使用固定 SDK/PG/Redis 镜像、随机 owner/数据库/密码、只读源码挂载与归属核对清理。
- 选择 ARM64 是实施决定：三份现有固定镜像本机读回均为 linux/arm64。x64 是未来独立验证，不通过更换 digest 或浮动 tag 冒充兼容。
- checkout/setup-node 使用首批已核验的官方提交，Node 固定 24.22.1；本批不需要 pnpm。
- 触发与首批一致：main PR、main push、手动；无路径过滤，只读 contents 权限、不保留 checkout 凭据。同一 PR 取消旧轮次，各工作流独立分组。
- 不上传数据库、秘密、.runtime 或原始测试工件；不连接4192、不执行 G14 故障、性能实验或部署。

## 验证边界

解析工作流并核对矩阵、权限、引用、触发与失败语义；验证 Compose 合并配置。本机如使用已缓存固定镜像，三套依次在独立随机项目验证，保留首次失败且不自动重试；只清理脚本拥有的资源。

本机宿主 SDK 为 10.0.403，不用于替代固定 10.0.401 SDK 镜像。Docker ARM64 容器验证不等于 GitHub Ubuntu ARM64 runner 已成功运行。超时或取消的清理由一次性 runner 生命周期隔离，不宣称 shell trap 一定执行完成。

## 对比与交付

采用现有 Compose 验证入口；不采用宿主 dotnet 直接运行，因为后者绕过现有数据库、运行工具与迁移器契约。只新增工作流、设计/计划与验证说明，保留首批提交、主目录文档、历史证据和暂停状态。第二批独立本地提交，实际测试数与限制以本批记录为准。
