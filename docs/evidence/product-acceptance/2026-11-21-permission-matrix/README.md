# API 授权矩阵隔离验收

产品提交 `9f45b97c73873088ebe58280025d10d39e121f0b`。开发起点为通过四项主线复跑的 PR #23 合并提交 `3b8a672`，冻结前纳入了仅更新 P12 台账的 PR #24。第 21 页的 11 个需求经 34 项真实 HTTP 定向回归及 14 个真实浏览器观察点验证。14 张 1440 或 1280 像素截图已逐张检查。观察与回归场景按需求共享。[HTTP 与构建清单](verification-manifest.json) · [浏览器原始结果](browser-results.json) · [截图复核](visual-review.json)

用真实 ControlPlaneApp、PostgreSQL、Redis 和构建后的 ConsoleHost，合成 55 个 API、项目应用、组织共享应用和测试/生产环境。验证了实际构建的 1,086 份后端输入，登录前比对实际服务的 3 份静态资源。浏览器中的生产标签来自合成夹具。工作授权通过现有审批发布才影响网关，浏览器操作未产生发布或改变现行目标版本与序列。

| 原需求 | 可复核的结果 |
| --- | --- |
| P21-C01 矩阵表 | 按 API 行显示授权，真正从后端分页。批量保存后读回对应单元。[矩阵](B02-matrix-and-window-states.png) · [提交回执](B02-batch-grant-readback.png) |
| P21-C02 环境列 | 仅展示可读环境。共享应用在项目 B 仅显示本项目 API 和环境，项目应用读其他项目返回 404。[共享项目](B02-shared-project-scope.png) · [无环境读权](B02-no-environment-read.png) |
| P21-C03 有效期 | 矩阵和预览展示时间到秒，未变字段的原精度保留。[窗口](B02-window-preview.png) |
| P21-C04 批量选择 | 从首页和末页选择两个单元，同一命令预览并提交；覆盖同项目跨环境批次。[跨页多选](B02-cross-page-selection.png) · [批量影响](B02-batch-grant-preview.png) |
| P21-C05 影响预览 | 预览逐项对照工作前后和当前运行依据。HTTP 检查确认发布、节点依据失效、旧路由修订及历史快照授权不明确时的结果。[批量预览](B02-batch-grant-preview.png) · [移动预览](B02-legacy-move-preview.png) |
| P21-A01 授权 | 两项旧版授权批量读回。路由授权明确选择 `POST` 及修订后提交。[批量读回](B02-batch-grant-readback.png) · [方法授权](B02-production-confirmation.png) |
| P21-A02 撤权 | 两个环境的旧版与路由授权一次撤销。HTTP 核验父授权、方法子记录、事务和审计。[撤权预览](B02-revoke-preview.png) · [撤权读回](B02-revoke-readback.png) |
| P21-A03 设置有效期 | 批量调整到同一失效时间，原生效时间、授权模式和路由方法保持。[有效期预览](B02-window-preview.png) |
| P21-S01 PROD 高危确认 | 控制台确认前提交禁用，HTTP 无确认拒绝。移动双侧均纳入生产风险核验。[生产确认](B02-production-confirmation.png) · [移出与移入](B02-legacy-move-preview.png) |
| P21-S02 Expired | 服务端判定的已过期和待生效均有独立标识。[窗口状态](B02-matrix-and-window-states.png) |
| P21-P01 `app.permission.manage` | 只读 Scope 禁用写操作。缺环境读取权时矩阵、详情和原授权列表均裁剪。已有会话被撤权后提交 403，清理草稿，重新读取后稳定只读。[只读 Scope](B02-read-only-scope.png) · [会话撤权](B02-revoked-current-session.png) |

独立源码审查提出的三项问题已修正并复核。应用详情、原授权列表和路由选项使用矩阵读取边界。预览标识和当前路由修订不符的授权。旧版授权可在矩阵中原子移到同项目另一单元，保持授权 ID 和移动前后的审计。生产确认约束矩阵命令，既有单条命令的请求契约继续兼容。[移动结果](B02-legacy-move-readback.png) · [后端定向用例](../../../../tests/WebApi.Integration.Tests/ApplicationPermissionMatrixTests.cs)

首轮浏览器 14 项通过，最后一帧拍到权限刷新中。第二轮额外要求等待只读表恢复，但遗漏了界面提供的重新读取操作，在失权场景停止，13 个已记录观察与失败回执保留。第三轮在新夹具中补上该操作，所有 14 项通过并逐张复核。产品提交贯穿三轮。开发过程的夹具外键错误、测试编译错误、预期 RED 和本机监听失败日志单独保留。四套浏览器准备/运行资源均已停止，同一工作树保留各自容器、卷、数据库和原始测试记录。原历史台账保持原字节。[完整清单](verification-manifest.json)

当前结果单列在 [454 项台账](../../../deployment/requirements-acceptance-current.json)。项目计数和矩阵共 14 项隔离验收通过，其余 440 项待复验。分支的本机全量与 GitHub 检查使用各自回执，合成环境结果只用于本批产品验收。

复现时使用已安装的标准 Node 和 Chromium，命令 `node scripts/acceptance/project-counts-fixture.mjs prepare <产品完整SHA> <本机空闲端口> <空闲10.251.x.0/24> permission-matrix`。在该 owner 目录启动夹具后，用 `node scripts/acceptance/permission-matrix-browser.mjs <环回URL> <owner目录> <新的结果目录>` 执行。运行后按相同 owner 停止夹具。前端和后端从 Git 固定提交导出，测试主机文件另外记录摘要。
