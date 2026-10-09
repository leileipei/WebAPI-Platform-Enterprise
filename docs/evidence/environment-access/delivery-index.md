# 环境访问地址交付记录

已安装源码：`8908281db8129860cfdf4104a02904a25d6c9b1b`，镜像：`sha256:9c9525d7ca63fa8fc4eb1173666640524f9760761e03a5189d3bb1d44ac71fed`。本批在隔离工作区提交；原源码目录的本地修改核对未变。

|验证|实际结果|
|---|---|
|Domain / Integration / Gateway / Console|736 / 767 / 142 / 223 全通过，共1,868项，零跳过；控制台构建通过|
|证据拒绝检查|7/7|
|实际隔离闭环|8项运行及8项浏览器检查；7张截图逐张查看；TLS前缀代理、双节点ACK和审批回滚通过|
|原数据演练|17个平台卷、4个IdP卷冷备；原43张表恢复后升级、旧软件回退、再升级及真实登录调用均通过|
|原4192安装|固定源码/实际镜像与二进制匹配；43张表、既有秘密、端口和绑定保留；原管理员、Keycloak Viewer及双网关调用通过；4个观测源Available|
|独立审查|3项Important集中修复并RED→GREEN；Critical0，剩余Important0，Minor0|

[运行闭环](8908281db8129860cfdf4104a02904a25d6c9b1b/verification.json)、[浏览器记录](8908281db8129860cfdf4104a02904a25d6c9b1b/ui/qa.json)、[完整回归](8908281db8129860cfdf4104a02904a25d6c9b1b/regression.json)、[原数据演练](8908281db8129860cfdf4104a02904a25d6c9b1b/original-data-rehearsal.json)、[原4192安装](8908281db8129860cfdf4104a02904a25d6c9b1b/original-4192/installation.json)、[审查记录](final-review.md)、[执行裁定](decisions.md)。早期候选证据仅为对应旧包的历史记录。

状态：sourceVerified=true，isolatedAcceptance=true，localInstalled=true，productionAcceptance=false。环境地址保存为元数据，DNS/TLS/LB仍需另行配置；完整跨环境发布晋级按B计划继续。
