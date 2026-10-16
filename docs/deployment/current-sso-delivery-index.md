# SSO升级后的当前本机交付索引

当前提交0b42f9e；[SSO身份源](http://127.0.0.1:4192/settings/sso)、[用户](http://127.0.0.1:4192/users)、[节点](http://127.0.0.1:4192/nodes)。继续使用原admin及原密码。

- [升级结果](sso-oidc-upgrade-result.md)：不可变构建、冷备份、隔离恢复与回退、增量迁移、等价双审批和当前ACK。
- [最终检查](../evidence/sso-upgrade/final-check.json)、[当前发布](../evidence/sso-upgrade/publication.json)、[浏览器复核](../evidence/sso-upgrade/ui/report.md)。
- 原SSO源码及验收：deliverables/WebAPI_Enterprise_SSO_OIDC_20261005.zip，保留不覆盖。
- 本机升级补充：deliverables/WebAPI_Enterprise_SSO_OIDC_本机升级补充_20261006.zip，仅含公开说明、脱敏证据和截图，不含源码、镜像、数据库、备份或秘密。

原各批文档反映各自历史状态。当前已部署SSO能力，但没有开通企业身份源；Provider、SSO用户保持空表，平台设置保持原默认值。正式企业SSO需要HTTPS入口及受控身份/秘密部署后另行验收。
