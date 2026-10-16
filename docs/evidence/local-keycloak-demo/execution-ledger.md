# 本机 Keycloak 演示执行记录

2026-10-06：用户确认独立持久 Keycloak 演示方案，授权一次已审阅工具本地提交、备份和4192接入；不push/merge。应用协议和镜像保留0b42f9e。

独立工作树 feature/local-keycloak-demo；原生工作树工具无法识别外层非Git目录，已检查 .worktrees ignored 后用 git worktree add 创建。

基线 Node24 在沙箱中监听本机端口发生内部断言退出；正常本机环境生命周期11测试通过。新增SSO持久配置测试首次出现缺少设置/owner校验的预期失败，再实现并通过。新增IdP测试在接口骨架上先验证预期失败，随后实现。

完整工具与console单元回归：162项通过。当前尚未进行持久接入或真实登录，结果以下续记。

独立一轮代码审查：Critical0、Important2、Minor3；作者一轮修复。新增阶段续跑、显式恢复绑定、有限HTTP期限、安全stop。后续真实清理发现外部secret卷仍挂载测试控制面，修正为先停用连接、移除仅测试控制面再清理IdP；不进行第二轮独立复审。

实际隔离流程8项通过：授权码+S256、Viewer读取/写403、未知Subject401且不开户、IdP和平台重启保持原cookie、停源票据撤销、停用户票据撤销、退出、本地管理员恢复。

7卷平台与4卷IdP备份，通过新owner空卷恢复。实际在第二卷解包前注入中断：服务未启动，首卷done标记保持；start续跑后Subject保留。显式API停旧源和Viewer→重绑→恢复状态；真实登录成功，写403。两组本批隔离副本资源清理完成，私有备份保留。

实测原0b42f9e已有发布ACK冻结旧InstanceId的限制：重启新实例心跳Ready、v4/seq4相同，但currentInstance ACK不接受，aggregate Degraded。本批不改应用、不伪造ACK、不增加发布；单独记录后续修复项。

166项最终单元回归通过，git diff --check通过。一次本地工具提交acb10a5f06a1734aec40a5d49ec1355d9fa191c8，共12文件；不push/merge。Git archive固定96个scripts/deploy文件到原运行目录tooling，安装manage.sh / manage-idp.sh。

4193已有Node监听，IdP检查在创建目录/卷前退出，不停该服务。改用4194，精确Origin由实际state生成；应用入口仍4192。

持久4192/4194部署完成，唯一Viewer/read Scope和显式Subject启用，不设默认。固定维护入口正常重启、8真实HTTP流程全部通过。客户端秘密实际10001:10001:600、readonly；IdP仅127.0.0.1:4194，独立数据库不发布端口。

原生浏览器企业登录→Keycloak→返回4192，Viewer管理权限拒绝并清空旧详情，环境读取/编辑禁用；admin本地登录恢复编辑。窄面板初轮截图弃用，7张实际1440x1000 JPEG视觉复核，视口重置，原用户页签保留。

当前持久平台7卷/20文件与IdP4卷/12文件冷备份完成，逐文件SHA读回；服务已重启。此前隔离新owner恢复不冒充当前最终备份已恢复。备份后再次真实OIDC登录读取/退出通过，原native admin会话刷新仍有效。

最终核验脚本首次误用UserDto.roles字段，按实际RoleIds及角色字典修正；未修改产品。原账号、保护秘密与业务表摘要一致，双网关/orders实际200，固定96工具文件SHA一致，14历史ZIP不变。13原服务运行、诊断源Available、v4/seq4保持；aggregate Degraded限制保留并明确披露。

原告警规则的整表摘要与原SSO升级前基线直接比对一致。最终补充交付采用固定提交的96工具/部署文件及3个新增测试，执行文档与7张桌面截图单独标识为提交后证据；排除全部运行私有目录和冷备份，进行现有秘密值逐文件扫描、ZIP CRC和读回摘要验证。包外delivery-seal.json保留最终摘要，未再次提交或覆盖历史交付。
