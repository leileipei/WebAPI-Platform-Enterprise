# 通知与跨环境审批：原4192升级结果

状态：已完成本地提交、主分支快进合并及原4192实际安装；未push。

产品软件：`adbcbed56a7e465c483b7462e4f3de9ac1156669`；实际镜像：`sha256:e7b9c1324e0f880ea1813c69fa0b4ee8782ea8dcbcfd71a1212800bb1e7b902b`。维护工具/安装合并提交：`d2ce0cbdf2f25f2a3a594bdf52cfaab802432b1b`；后续证据与包装提交另计，不改变运行软件身份。

固定产品六套完整回归2042项通过、零失败/跳过。实际合并固定源码全部Node功能测试208项通过，其中179已计入上述2042，新增29项为D3门禁；不把208再次加总。唯一整分支审查四项Important已集中修复，未重复审查。

原基线52表、794文件、216旧工具及七项本地修改保留，迁移后56表，仅新增4张通知表。原双网关版本4、序列4保持；五应用镜像和3静态文件摘要一致，两网关/orders均200，Collector与Prometheus/Loki/Tempo全部Available，最终Ready。原4笔审批申请在真实1440px界面完整显示。

原管理员、两名本地审批账号及原Keycloak账号均正常。真实SSO回调303→固定SPA200、returnPath正确；新维护入口完整stop/status/start/up/restart后原本地及SSO Cookie仍200，CSRF与只读写入403，注销204后401；密码/绑定/DP原文件摘要保留。

原通知设置尚未保存，保留未配置状态、Email/Webhook自动开关关闭。原规则及旧事件默认外发关闭；原库0投递/0尝试，测试请求因未保存返回409，页面测试按钮禁用。没有向原实例写QA申请/告警或为演示改配置。完整真实SMTP250、签名HTTPS202及16通知场景在自有克隆验收，原实例仅核实声明收件fixture身份和生命周期。企业端点及生产验收未执行。

升级前平台8卷/Keycloak4卷，升级后平台11卷/Keycloak4卷，均冷备并逐文件SHA回读、finally恢复Ready。新增3卷明确为通知秘密、fixture秘密及fixture数据；原DP旧钥匙及IdP秘密卷前后摘要一致。绝不以旧数据库覆盖升级后的新增数据。

新增独立长期维护入口：`manage-notifications.sh`，支持status/start/stop/up/restart，从确切D3提交归档并检查字节/秘密；原manage.sh、manage-policies.sh、manage-idp.sh、manifest及216固定工具不改。缺少私有钥匙时拒绝启动，不重新生成覆盖；恢复应使用原私有冷备，兼容桥接只用于已验证恢复且通知/规则管理临时只读423。

697个执行账本/日志/失败观察文件已私有归档逐SHA回读，6套重要冷备继续保存。仅关闭本批58631/58831/58931三个UUID克隆和其独立Keycloak容器/网络，全部数据卷、备份、候选/桥接镜像和历史文件/工作区保留，未prune全局资源；57531旧撤权角色和reader未扩权。

公开实证见[安装状态](../evidence/notification-approval/d3-installed-adbcbed/installed-status.json)、[原数据保护](../evidence/notification-approval/d3-installed-adbcbed/original-protection.json)、[前后备份](../evidence/notification-approval/d3-installed-adbcbed/backup-summary.json)、[清理状态](../evidence/notification-approval/d3-installed-adbcbed/cleanup-status.json)。所有裁决与判断错误影响见[裁决记录](notification-approval-rulings.md)，克隆细项见[固定安装前结果](notification-approval-pre-install-result.md)。最终ZIP的固定源、CRC/逐文件摘要/私有canary检查和整包SHA另见交付目录package-status.json。

失败不隐去：辅助相对路径错误在导入阶段失败；修复后主体基线成功，辅助配置读取空JSON失败，随后只读核实未保存行。D2网络池、SMTP恢复漏stdin、QA Key过期和旧CUA闭包等完整失败材料保留，其修复及边界见裁决记录；仅当前实际成功证据计入结论。
