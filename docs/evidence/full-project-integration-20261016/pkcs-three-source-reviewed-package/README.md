# 第2包三源 Pkcs 最小依赖修复：精确提交审阅包

本包仅用于审阅未提交的依赖修复。本次已按授权完成实现和工作树验证，未创建提交、推送、合并 main 或部署4192。**本包不是固定新三源的正式性能验收。**

## 精确来源与范围

| 来源 | 原父提交 | 文件数 |
|---|---|---:|
| 对照 B | f31887ee2ee551a466bf399fda2b26cdee67902e | 14 |
| 候选 C | 760493da5a5bc0f0674e7e6398260c8a5ac4a6c0 | 14 |
| 共享夹具 H | 883060f04ab04d95fad5ae9d559d7744ff1c2120 | 14 |

三源均在独立对象库、分离HEAD的隔离副本中准备，原固定仓库保留。每源仅将 `System.Security.Cryptography.Pkcs` 固定为10.0.13、在 Infrastructure 增加显式引用，并通过真实NuGet恢复生成12份受影响锁文件。其他依赖、业务、测试断言、审计及warning-as-error设置未改。`manifest.json` 列明全部42文件的原始／修复Hash、路径、父SHA及三个补丁Hash。

三源共用的14个相对路径如下（每源各修改一次）：

- `Directory.Packages.props`
- `src/WebApi.Infrastructure/WebApi.Infrastructure.csproj`
- `src/WebApi.ControlPlane/packages.lock.json`
- `src/WebApi.Gateway/packages.lock.json`
- `src/WebApi.Infrastructure/packages.lock.json`
- `src/WebApi.Migrator/packages.lock.json`
- `src/WebApi.RuntimeTool/packages.lock.json`
- `src/WebApi.Worker/packages.lock.json`
- `tests/WebApi.Domain.Tests/packages.lock.json`
- `tests/WebApi.Gateway.Tests/packages.lock.json`
- `tests/WebApi.Integration.Tests/packages.lock.json`
- `tests/WebApi.NotificationFixtureHost/packages.lock.json`
- `tests/WebApi.TestSupport/packages.lock.json`
- `tools/WebApi.PerformanceHost/packages.lock.json`

三个补丁字节完全相同，SHA256：`93325b7818349a2f3f4cbe03ae25b075fd0b5a1a0bb5ada5b75ff36c443e4776`。三侧解析依赖图一致，C/H产品源码一致，B/C原14处反事实产品差异保持。

## 实际验证结果

- 三源force-evaluate与locked-mode恢复通过；9份漏洞审计报告未报告已知漏洞；9组Release构建均零warning/error。
- 候选执行 2899 项，2899通过、0失败、0跳过；对照执行 2985 项，2963通过、原始失败22项、0跳过。
- 对照的22项原始失败全部精确匹配原登记成本差异。新的独立Counterfactual证明82项通过；严格逐例分类22项expected、0blocked，原失败TRX完整保留。**对照不是原始全绿。**
- 双侧前端构建通过，完整输入文件在测试后Hash不变；独立重新生成的完整输入也与实际执行输入逐文件Hash相同。历史父SHA编译元数据已核验。该元数据只表示父版本，实际未提交内容由补丁和完整输入manifest识别。
- 补丁逐一正向应用、目标Hash比对、反向恢复通过；三侧HEAD与stage0内容保持，未暂存。两侧index stat-cache字节刷新已记录，非暂存变化。
- 主工作区664文件状态及Hash、16运行控制文件、原固定源码／旧54夹具保护通过；原118容器身份、镜像、端口、标签和健康状态保持，29个未暂停服务启动时间不变；本轮测试owner均清理。保留一个本轮独立NuGet缓存卷，供后续验证复用。

## 保留的失败和验收边界

原133对／266窗口、原NU1902/NU1903构建失败以及私有输入准备失败均保留。首次私有副本遗漏原截图与root配置/vendor，后续从固定父树补齐并进行新完整回归；没有修改产品或测试断言来规避失败。原266窗口仍属未完成结果，本轮没有新增正式性能窗口，也未与旧结果拼接。

`immutable=false`、`formalQualified=false`。新的不可变B/C/H提交、固定构建、SQL／EXPLAIN、S/M/L门禁、全144对／288窗和G5/G6仍未执行。历史登录Unavailable延期问题也未在此次依赖修复中关闭。临时暂停的六套独立环境已恢复；4192／4194保留。

## 审阅内容与下一步

优先查看 `manifest.json`、`baseline.patch`／`candidate.patch`／`harness.patch`、`regression-verification.json`、`budget-classification.json`、`independent-review-final.json`。原始日志／TRX位于私有证据根，清单记录实际路径和Hash；本包不包含密码、令牌、私钥、数据库、账号数据、node_modules或原私有失败输入。

本次授权明确排除新增提交。本包准备完毕后仍须另行取得精确三源各14文件本地提交授权，再使用新固定身份重新执行相关门禁；不得把原父SHA的旧验收结果直接转记为新版本通过。
