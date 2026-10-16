# 网关重启就绪修复：本机升级记录

2026-10-06。用户在已呈现9文件本地提交、冷备份/隔离恢复/4192升级方案后回复“继续”，按该具体授权执行；不push/merge、不删除原数据、不新增业务发布。

升级前原owner/账号/角色/Scope/SSO/IdP/业务表与历史发布事实、密码文件摘要、原主工作区状态及16份已封存ZIP已建立私有保护基线；原状态Degraded、v4/seq4、13服务和四诊断源运行，双网关/orders200。

固定提交前重新执行611项C#（196/363/52）、167项工具/console、20项真实PG查询，全部通过。9个源码/测试摘要与已审阅commit-scope完全一致，index空后只stage这9文件。一次本地提交63681d172cfff7fdd5a80efb7bdd7dfbb2a32c10，不push/merge，文档/秘密/依赖未提交。

使用Git archive新提交构建长期镜像；前端锁与现有SSO依赖字节核验后，仅在本工作树创建依赖符号链接，不纳入提交。构建期间原服务保持运行。后续步骤以下实际续记，不把准备当完成。

长期镜像构建通过：sourceRevision=63681d172cfff7fdd5a80efb7bdd7dfbb2a32c10，imageId=sha256:9d350a453b8f8441e68d786283bfe13374b0bf5660cce02e33e972af4d246f17。

平台7卷冷备份20个文件、独立IdP4卷冷备份12个文件，逐文件SHA256读回通过；包含原SSO配置、账号凭据与数据保护密钥。原服务已恢复运行，保护基线不变。备份存于私有.runtime目录，不进入补充交付包。

独立恢复演练通过：新owner空平台7卷及独立IdP4卷恢复，校验原账号/数据/Subject，恢复副本SSO保持disabled，未连接原IdP。原软件登录及流量、新软件双网关Ready、再切回原软件登录及流量均实测通过。业务v4/seq4及31张保护表/发布成功审计摘要不变，无新增迁移或业务发布。仅本批副本清理，剩余容器/卷/秘密文件均为0，原环境保护基线再次通过。

最终验收首轮在重启前停止：注销响应更新LocalClient Cookie，但脚本误用另一SsoBrowser的Cookie副本，得到200。源码SignOutAsync仅清除请求客户端Cookie；本批不新增会话撤销行为。已仅修正私有验收脚本，检查同一注销客户端/auth/me=401，需重跑最终验收。没有修改9文件提交或运行镜像。

原4192升级通过，新镜像和固定96文件维护工具安装完成；原管理员Cookie会话存活。实际正常平台与IdP restart 后，双网关Ready、runtimeConfirmed=true、publicationAcknowledged=false，双/orders200，四诊断源Available。原admin/两审核员/真实Keycloak Viewer登录，Viewer写入403，同一注销客户端401，均通过。

1440×1000实际原生浏览器QA通过：双网关Ready、当前实例runtime_applied事件、真实Keycloak浏览器回调和Viewer只读禁用按钮；恢复管理员并保留/nodes页面，临时viewport已reset。截图不含凭据。页面操作后31保护表/发布成功审计/秘密与SSO配置/原主工作区/16历史ZIP摘要再次一致；未新增业务发布或修改v4/seq4。
