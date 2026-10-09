# 环境访问地址交付记录

候选源码：`ab6337eefad1a6b34f15510965f47c3c1c5753a6`，镜像：`sha256:358a1ef99079f5784e5c36132e6c7633740b937b5a5aa106f49273b9718716f0`。本批只在隔离工作区提交，原目录修改未合并。

|验证|实际结果|
|---|---|
|Domain|735/735，零跳过|
|Integration|764/764，零跳过；运行入口重启修正后相关8/8|
|Gateway|142/142，零跳过，包含实际双节点重启和错误摘要拒绝|
|Console|221/221及生产构建；既有大包提示|
|证据拒绝检查|7/7|
|实际隔离闭环|8项运行检查及8项浏览器检查，7张截图逐张查看|
|代理与回滚|严格信任测试证书的TLS请求200，前缀剥离至实际上游；双节点真实ACK，独立审批回滚|
|原实例保全|43张业务表冷备前后不变，17个平台卷及4个IdP卷归档；旧软件恢复副本登录/SSO/双Gateway通过|

[运行闭环证据](ab6337eefad1a6b34f15510965f47c3c1c5753a6/verification.json)、[浏览器记录](ab6337eefad1a6b34f15510965f47c3c1c5753a6/ui/qa.json)、[回归摘要](ab6337eefad1a6b34f15510965f47c3c1c5753a6/regression.json)。早期16bbe62候选证据记录前一运行包，不作为当前安装身份。

状态：sourceVerified=true，isolatedAcceptance=true，localInstalled=false，productionAcceptance=false。独立审查、升级与回退演练及原4192安装仍在进行。生产DNS/TLS/LB、企业上游和容量验收未执行。
