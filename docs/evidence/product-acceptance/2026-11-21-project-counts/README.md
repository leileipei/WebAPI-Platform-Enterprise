# 项目列表计数首批验收

产品提交固定为 `1ead23c6f8f2778f1547264533622a759962b729`（PR #22）。本批完成 P04-C03 环境数、P04-C04 API 数、P04-C05 项目应用数在**隔离环境**中的验收。原 36 已关闭、40 部分、18 缺失、360 待复验的历史台账保持原字节；当前结果单列在 [454 项当前验收台账](../../../deployment/requirements-acceptance-current.json)。

使用真实 ControlPlaneApp、Kestrel、PostgreSQL、Redis 和 ConsoleHostApp，加载该提交构建的生产前端。数据和账号全部为本轮合成夹具，没有替换 HTTP 响应。核对了容器内 1,077 份后端输入、运行程序集摘要及实际返回的 3 份静态资源。[构建身份](build-identity.json)

## 结果

- 现有 ProjectResourceCountsTests：10 通过、0 失败、0 跳过。
- 浏览器：10 类账号/范围组合，13 个观察点通过；全部截图已逐张检查。观察点共享于三个需求，不能换算成 13 个需求关闭。[逐场景结果](browser-results.json) · [独立视觉检查](visual-review.json)
- Console：339 测试通过及 TypeScript/Vite 构建通过；Python 诊断 94 通过；CI 运行恢复门禁 30 通过。
- 本机四类必需检查合计 **5,204 通过、0 失败、0 跳过**：Domain 1,426、Gateway 639、Integration 2,671、Console 468（Node 339、Python 94、运行门禁及工具回归 35）。[本机完整结果](local-required-checks.json)。本分支 GitHub 与合并后 main 复跑另行核对。

| 观察点 | 验证结果 |
| --- | --- |
| PC-all | 多资源项目 2/2/2；空项目 0/0/0；停用项目 1/—/—。多版本与重叠 Scope 不重复，停用资源计入，组织共享应用排除；HTTP 分页边界正确。 |
| PC-no-environment / no-api / no-app | 对应读取权限缺失时，该列接口为 null、页面为 —；其余有权列保留真实计数。 |
| PC-no-count-reads | 三种资源读取权均缺失，项目仍可见，三列均为 —。 |
| PC-project-only | 仅显示授权项目，2/2/2；其他项目详情返回 404。 |
| PC-environment-only | 仅显示授权项目，1/—/—；无全项目 API / 应用数泄露。 |
| PC-no-project-read | environment.read 的可见性继承保留有可读环境的项目；空项目不可见。这是现行范围规则。 |
| PC-no-governance-read | project.read 与 environment.read 均缺失时，列表为空，项目详情返回 404。 |
| PC-revoke 与三次撤权 | 同一会话依次撤 environment.read、api.read、app.read，刷新后各列变为 —；没有重登录。 |

所有账号的新建及编辑按钮均禁用，每轮退出后 `/auth/me` 返回 401；浏览器未出现未处理脚本错误。截图只含合成名称、计数与状态，没有秘密字段。

## 历史与失败保留

首轮控制台使用 ChatGPT 应用内 Node，受 macOS Team ID 校验阻断加载 Rollup；另用已安装标准 Node 复跑通过。测试夹具先出现 CA1416 编译错误，随后出现合成应用编码违反组织内唯一约束。两次失败及失败数据库保留；修正的是测试工具和数据，产品源码未改。详情及原始日志摘要见 [证据清单](evidence-manifest.json)。

原始日志和私有凭证留在忽略目录 `.runtime/product-acceptance-20261121`。私有输入、会话与密码不随公开证据提交。原 4192/4194 安装未参与本轮。这些结果不包含客户数据、生产容量、SSO 现场或真实企业部署验收。

## 复现

1. 使用本批工具所在的隔离 checkout 和已安装依赖；工具提交可以晚于产品提交。以本机能加载原生模块的标准 Node 执行，明确传入待验收产品完整 SHA。准备器从该 Git 对象导出源码并构建前端，不使用工作区现成 dist。
2. 使用本机已有 Docker 镜像和 `webapi-enterprise-core-test_nuget-test` 只读种子卷。`node scripts/acceptance/project-counts-fixture.mjs prepare <完整SHA> <空闲本机端口> <空闲10.251.x.0/24>` 创建新 owner，输出独立目录。
3. `node scripts/acceptance/project-counts-fixture.mjs start <目录>`。等待该目录的 `context.json`，记录容器日志。启动不会复用旧夹具。
4. 设置本机已有的 `WEBAPI_PLAYWRIGHT_PACKAGE` 和 `WEBAPI_CHROMIUM_EXECUTABLE`；运行 `node scripts/acceptance/project-counts-browser.mjs <本机URL> <目录> <新证据目录>`。不得覆盖旧结果，随后逐张检查截图。
5. `node scripts/acceptance/project-counts-fixture.mjs stop <目录>` 校验 owner 后停止本轮服务，保留容器、数据库、卷与证据。另行核对原资源身份和文件摘要。

## 独立审查与修正

独立只读审查核对了历史台账、13 张截图及后端输入身份，未发现 Critical 问题。四项 Important 均针对复现工具：冻结输入、目标身份绑定、错误日志中的密码和证据目录覆盖。已修正并通过 5 项负向回归，还用真实 Chromium 的密码填写超时验证公开结果不含合成密码。完整四类本机门槛已通过。没有将后续作者回归称为第二次独立复审。[修正与保留细节](review-fixes.json)

两项非阻断细节保留：环境可见性继承场景可进一步加强全体项目 ID 的自动断言；早期后端清单摘要采用 JSON 规范化后的计算口径（已由独立审查复算），不是原始文件字节摘要。

## 最终复现与资源保全

审查修正后的新 owner 使用 Git 固定产品源码、登记文件复制与容器内逐文件校验，1,080 份输入（1,077 份产品/既有测试输入与 3 份测试主机输入）一致，登录前核对服务身份和静态资源后，13 个浏览器观察再次通过；重复运行不新增需求完成数。[最终复现摘要](reproduction-summary.json)

开始时原工作区的 4,552 份跟踪文件、HEAD 和工作区状态均未变，525 个既有容器、1,151 个卷、133 个网络身份全部保留。四轮本批夹具均已停止，容器、卷、失败数据库及证据保留。[保全核对](preservation.json)
