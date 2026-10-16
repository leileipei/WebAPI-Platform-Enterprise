# 版本比较与风险评审：4192升级结果

日期：2026-10-06。用户明确选择A后，本地主目录快进合并、完整冷备份、原数据随机恢复、迁移与软件回退演练、4192切换、原账号/SSO/页面验收和副本清理均完成。没有推送、删除原数据或创建新业务发布。

当前来源分开记录：主目录`feature/core-loop`为`17cad2315900939a1fdc2bca6f876a4649bcc06f`；实际应用`943143fdf4022f55b599fd521cf2e4fbe74a502d`；镜像`sha256:0f2b243d098d19eca5d1ea1d1acb853893cf0260495bcb230fd28386fc46962c`。17cad23相对943143f仅文档证据变化，应用输入相同。固定维护工具仍为63681d1，96文件SHA核验通过，其runtime/SSO代码与应用候选一致。

入口：http://127.0.0.1:4192/apis。原API详情已显示“版本比较与评审”。原API仅有一个Published版本，因此比较页真实显示“至少需要两个版本才能比较”，生成按钮禁用；原库两张新增表为空，没有为展示写入虚构版本、报告或评审。

原三名本地账号使用原密码登录通过；真实Keycloak Viewer登录、读权限及写入403、同客户端退出401通过。浏览器中原admin会话仍可读。31张受保护表、历史发布成功审计、原候选/快照、秘密和SSO摘要保持；原owner、绑定、端口4192/4196/4197与IdP4194保留。两网关Ready/runtimeConfirmed、业务v4/seq4不变，/orders均200，Collector与三源Available。静态三文件SHA匹配不可变镜像。

迁移仅新增api_version_comparisons、api_version_risk_reviews两张表及约束/索引，EF记录20261006090000_ApiVersionComparisons。升级前备份平台7卷与IdP4卷，20+12文件逐一读回SHA；私有备份保留，不包含在公开包。

原数据在随机空卷恢复后，先核对旧软件、31表与原代理，再完成943升级→802旧软件回退→943再升级，保留新空表/迁移记录，不执行Down删表。IdP四卷也恢复到独立owner、新端口，Subject/realm/client保持，原Provider未重绑。副本11项真实UI操作及四张1440×1024截图通过；比较、评审、草稿和提交写入只在副本，提交后WaitingApproval，两级审批仍独立。原4192两张1440×1024截图只做读取和导航。

本次主目录新跑控制台99项、运行工具104项，共203项通过，TypeScript/Vite通过。首轮控制台因沙箱禁止测试监听端口失败，源码不变、允许临时本机端口复跑通过。先前该应用源码909项自动化与独立HTTP/双ACK验收记录保持封存；本次没有把旧结果当作新主目录检查。检查日志与SHA见merge.json及包内tests目录。

清理：仅副本的容器、卷、网络、临时凭据和IdP资源全部为0；原7卷、4个IdP卷及私有冷备份保留。71份原修改/未跟踪文件与22份历史ZIP摘要保持不变。原六项既有编辑及旧安装状态文件不覆盖；本批实际安装记录以本目录final.json、deployed-ui.json、cleanup.json为准。

范围仍为第11页版本比较与风险评审；仅OpenAPI3.0 JSON与受限Schema语法，未支持结构显示Limited/Unknown。没有生产容量、企业联调、完整WCAG或40页全部真实集成验收结论。此补充包只含脱敏说明、机器证据、页面截图与检查日志；源码在此前版本比较交付包中。

页面证据：[原API详情](deployed-api-detail.png)、[单版本真实空态](deployed-comparison-empty.png)、[副本比較与留档](clone-comparison.png)、[强确认](clone-confirmation.png)、[向导交接](clone-wizard.png)、[独立审批](clone-release.png)。原始浏览器JPEG无缩放转换PNG后记录SHA并逐张查看。临时副本URL现已失效。
