# 4192 网络策略部署结果

2026-10-14。按用户“单独授权”，原 WebAPI 平台 4192 已升级到固定候选 `226dfe4` 并完成本机运行验收，维护入口为 Ready。原账号、权限、组织／项目／环境、IdP、API 路由和持久卷保持，第 2 包仍暂停。本轮无源码提交或推送。

## 运行入口和固定版本

| 项目 | 已安装结果 |
| --- | --- |
| Console | http://127.0.0.1:4192/ |
| Gateway A／B | http://127.0.0.1:4196/ ／ http://127.0.0.1:4197/ |
| 原 IdP | http://localhost:4194/ ；原 owner、issuer、回调及秘密保持 |
| 应用源码 | `226dfe450865cd322d81eb5747bec791a71f7519` |
| 应用镜像 | `sha256:4223703e79dfc88fe13631a32ac7bedf0c0a6da81370dd0610b908a7e6b9a8aa` |
| 平台 owner | `6d7e3fda-88ea-4315-a8ef-df9e7cd995c8` |
| 维护入口 | `bash "/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/local/manage-pipeline.sh" status` |
| 当前业务发布 | 配置版本 4、发布序列 4；两个当前 Gateway 实例 Ready，具备快照 2.3 能力 |

源码与已集成 GitHub main `a30ce5b` 的产品内容一致。固定镜像的 8 个应用容器、281 个应用文件和 3 个前端资源摘要匹配。[安装证据](../evidence/network-policies/4192-installed-226dfe4-20261014/upgrade-result.json)、[最终验收](../evidence/network-policies/4192-installed-226dfe4-20261014/acceptance-result.json)。

## 数据和维护清单

迁移 `20261012004548_ReleaseFrozenContracts` 严格回填 4 份历史冻结契约，0 不一致，原候选字节 Hash 保持。原 71 张表的 1011 条基线记录保留，只允许一条新迁移历史和 `release_frozen_contracts` 派生表；没有重新 bootstrap、恢复旧库、重置账号或增加权限。[迁移门禁](../evidence/network-policies/4192-installed-226dfe4-20261014/immediate-migration-result.json)。

应用 release、runtime 与维护清单已协调到 `226dfe4`。维护清单仅更新 sourceRevision，原固定工具目录、165 文件、runner、工具提交 `0ecdb46` 和 UTF-8 修复 `9cef7cfe` 保留；这两个标识是维护工具来源，不是当前应用版本。原四项观测配置的容器、镜像、挂载及 pin 保持，18 个原持久卷和 20 份私有文件摘要一致。原报告、Cookie 密钥、缓存 HMAC、通知秘密和通知夹具秘密五类物理卷与紧邻切换的冷备逐文件字节一致，文件数分别为 0、1、1、3、5；原 IdP 两个容器仍运行且镜像保持。main 既有本地文件和状态未变。[基础设施保护](../evidence/network-policies/4192-installed-226dfe4-20261014/infrastructure-result.json)、[物理卷和 IdP 保护](../evidence/network-policies/4192-installed-226dfe4-20261014/volume-preservation-result.json)。

## 运行验收

| 检查 | 实际结果 |
| --- | --- |
| 本地管理员 | 原 admin 账号和既有 Pipeline 权限可用，密码未更换 |
| SSO | 原 provider 和用户身份，真实 OIDC code 交换；callback 303、auth/me 200，未写角色或范围 |
| 双网关原 API | 两个 /orders 均 200，API Key 未转发上游 |
| 三类策略管理 | 原 4192 的 CORS、IP 和 Transform 校验通过；JSON Request／Response 预览分别通过，actualForwarding=false |
| 网关新能力 | 两个原当前实例均声明支持 2.0、2.1、2.2、2.3；当前原业务快照保持 2.0 |
| 三源观测 | 同一实测请求的日志、链路和指标均 Available；24 次原 API 调用成功，原采样参数 0.1 保持 |
| 站内告警 | 原告警与规则可查询，各 1 条；没有创建告警或触发外部通知 |
| 第 2 包 | 保持暂停，原性能 owner 的运行容器为 0 |

[SSO](../evidence/network-policies/4192-installed-226dfe4-20261014/sso-result.json)、[策略校验与双向预览](../evidence/network-policies/4192-installed-226dfe4-20261014/policy-availability-result.json)、[节点能力](../evidence/network-policies/4192-installed-226dfe4-20261014/node-capabilities.json)、[三源与告警](../evidence/network-policies/4192-installed-226dfe4-20261014/observation-result.json)。

本轮安装保留原业务，不向原环境新建或发布演练策略。三类策略和 JSON 双向映射的真实转发、独立审批、2.3 发布及历史回滚已在[旧数据升级演练](network-policies-4192-upgrade-rehearsal.md)验证；完整 TEST／PROD、四类历史快照、1440px QA 见[固定第 3 包交付](../evidence/network-policies/2026-10-14-226dfe4/delivery-index.md)。本轮 SSO 是实际 HTTP/OIDC 验证，没有新增浏览器 SameSite 验收；原报告卷为空，先前冷恢复按 0 文件核对。

## 切换前备份和回退

紧邻切换新建平台、IdP 冷备，51 个备份文件全部回读 Hash 一致，另保存 173 个原维护文件。备份完成后原 9d 与 IdP 先恢复 Ready，再停止平台服务进行已授权迁移。旧演练备份及工具目录没有覆盖。[本轮备份证据](../evidence/network-policies/4192-installed-226dfe4-20261014/backup-result.json)。

平台备份：`/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/backups/network-policies-cutover-292ae3c4-f95e-4345-8314-4cf0c5f27057`。IdP 备份：`/Users/leo.cui/Documents/WebAPI Platform Enterprise/enterprise/.runtime/backups/network-policies-cutover-292ae3c4-f95e-4345-8314-4cf0c5f27057-idp`。转储、秘密和完整操作日志仅保留在本机私有目录，不包含在公开证据中。

本轮未触发回退。目前只安装候选，没有新增 2.3 业务快照或网络策略工作记录；旧软件兼容性仍按实际 desired、两个 LKG、数据库工作记录和已验证条件判断。若后续发布新策略或产生新业务写入，先使用业务历史快照回滚或兼容修复前进；完整冷备回退需要明确处置切换后的新增数据，不能直接把旧库覆盖回原卷。

## 文件核验

[安装清单](../evidence/network-policies/4192-installed-226dfe4-20261014/manifest.json)包含本页和十份结构化证据的 SHA256。原 4192 软件切换已执行，本轮不恢复性能包、不修改产品源码、不生成新 Git 提交、不推送。之前的准备和隔离演练文档保留其当时状态，以本页和安装清单为当前原实例结果。
