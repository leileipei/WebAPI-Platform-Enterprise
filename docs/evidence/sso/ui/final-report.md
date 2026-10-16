# SSO/OIDC 最终浏览器与设计 QA

最终源清单 d9f22240c5890740138ed1d2d8895441611b7f34c4d3079f8d5a73a70649afd5：13 项补验、11 张逐图检查的 1440×1050 截图。初审阶段 18 项检查与 10 张截图保留在 `initial-review-phase/`，绑定原 ba1d9eb4 源码；不将旧截图换标为新源码。最终未再次操作的开户修正、默认/启停/dirty确认/未知Subject/返回取消有初审原生记录；认证修改路径同时经最终完整后端回归和真实IdP场景复验。

- [最终列表](final-02-provider-list.jpg)、[配置抽屉](final-03-provider-drawer.jpg)：沿用深色侧栏、蓝色主操作、灰白工作区及密集表格；SSO导航可见，状态、修订和连接测试的不同范围清晰。
- [合法长字段](final-09-long-fields.jpg)：真实提交128字符名称、带点SecretRef和长Issuer成功，组织源保留Disabled；整个文档宽度1440，无横向溢出。未使用该无对应部署文件的配置宣称连接成功。
- [忙碌释放](final-busy-unlocked.jpg)：合成代理暂停真实PUT响应，原工具栏刷新导致一次403并走现有权限复查；释放响应后Close/Save可用，Escape关闭。后台红色提示来自一次性故障注入，属于竞争场景证据。
- [连接测试](final-05-test-stages.jpg)、[修订冲突](final-06-conflict.jpg)：当前测试r9六阶段；并发修改使旧etag保存412，输入保持。读取/核对r10后明确采用并保存r11。
- [用户绑定](final-04-sso-user.jpg)：只包含本次合成账号的绑定标识，无密码输入；Active绑定只读，Tab焦点留在对话框，Escape关闭。UUID Subject为合成公开测试标识，既不是授权码也不是Token。
- [真实Keycloak](final-07-keycloak.jpg)、[通用失败](final-08-generic-failure.jpg)：错误客户端秘密经明确轮换后实际失败，页面只显示通用提示与trace，保留本地登录。恢复合成秘密并推进认证修订后真实成功；complete恢复身份、新CSRF写入成功r14。
- [原生Cookie布尔证据](cookie-evidence.json)：表单发起的重新验证流程，callback跨站有Correlation/Nonce、没有平台Session/CSRF；跨站complete没有Session；同源me带平台Session恢复身份。证据也保留已有IdP会话自动302链中Cookie存在情况不同的真实观察，不能仅根据Sec-Fetch-Site推断所有链的Cookie行为。
- [权限失效](final-10-permission-revoked.jpg)：保持功能码，撤真实系统PlatformAdmin；刷新后清空详情。释放已授权的迟到响应后仍dialog0、引用输入0。

后端196+347+51、前端70及构建、运行工具17、独立E2E1全通过，共682项；真实Keycloak另有10个场景检查。没有第二次独立审查，也不代替企业IdP、生产HTTPS/PKI、生产部署和灾备验收；没有完整WCAG或全站屏幕阅读器认证。fixture观察代理只存在于合成测试中，不进入正式控制台运行。
