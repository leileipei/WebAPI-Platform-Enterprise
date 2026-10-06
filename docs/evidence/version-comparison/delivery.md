# 本批源码与验收交付说明

交付文件：`WebAPI_Enterprise_版本比较与风险评审_20261006.zip`，同目录提供`.sha256`。包内`source.tar`为Git archive固定源码，`evidence/`为最新中文说明、机器可读验收与截图，`tests/`为通过日志，`package-manifest.json`记录逐文件SHA。源归档中旧检查点文档反映当时阶段，最终状态以包内evidence/result.md为准。

应用来源：`943143fdf4022f55b599fd521cf2e4fbe74a502d`。
固定源归档SHA256：`2d75b32cb085471929fe5b9eb29a463ae84767cce9172b3d84b95530b802e2ba`。
实际运行镜像：`sha256:0f2b243d098d19eca5d1ea1d1acb853893cf0260495bcb230fd28386fc46962c`。
源码分支：`feature/gateway-restart-readiness`，保留工作树；本批未合并、推送或升级4192。

重现入口（在保留的Git工作树、已配置Docker与既有测试NuGet种子卷环境中运行）：

```sh
node scripts/comparisons/acceptance.mjs --revision 943143fdf4022f55b599fd521cf2e4fbe74a502d --review
```

执行器输出随机私有目录与临时loopback地址，凭据仅保存在0600私有文件；不得使用4192账号/数据替代验收。进入UI的路径为`/apis/{id}/versions/compare`，从真实API详情页导航。每次验收生成新数据和新端口，旧截图ID或旧临时URL不能作为运行入口。完成实际UI验收后，写入对应证据并执行：

```sh
node scripts/comparisons/acceptance.mjs --cleanup <本次执行器输出的私有目录>
```

清理前校验容器/卷/网络所有者；只有真实API、权限、发布、UI截图和四项残留0全部满足时，complete才为true。纯source.tar没有Git对象，不能直接解析此提交；需要保留的Git工作树或完整仓库。此包不含依赖、Docker镜像、业务数据库或凭据，不是无需环境的离线安装包。

验收合计909项自动化检查、真实PostgreSQL与双网关HTTP、12项UI动作、六张必需截图及一张长片段截图。此次是第11页功能交付，不等于40页全部真实集成、4192部署或生产验收。六项审查问题全部修复，无延期问题；四项实现裁定及代价见source-review-resolution.md。
