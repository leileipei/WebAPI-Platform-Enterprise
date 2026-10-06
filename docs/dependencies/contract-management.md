# 契约管理依赖记录

核对日期：2026-10-07。项目 SDK 固定 .NET 10.0.401；生产构建须使用固定 Git 提交和 locked restore。

## 来源与许可

- YamlDotNet 18.1.0：官方 NuGet 包，MIT，低层事件 API；[版本页](https://www.nuget.org/packages/YamlDotNet/18.1.0)、[固定版本源码](https://github.com/aaubry/YamlDotNet/tree/v18.1.0)。
- JsonSchema.Net 9.4.0 / JsonPointer.Net 7.0.2 / Json.More.Net 3.0.1：本仓库保留固定官方 MIT 源码，通过 ProjectReference 自行编译，**不使用三者的官方预编译 NuGet 包**。原 NuGet 包的 OSMFEULA 条款与源码 MIT 分别核对；[官方二进制协议](https://www.nuget.org/packages/JsonSchema.Net/9.4.0/License)明确保留源码及自行编译权利。
- Humanizer.Core 3.0.10：MIT，JsonPointer 的依赖，版本由中央包清单固定；[版本页](https://www.nuget.org/packages/Humanizer.Core/3.0.10)。此依赖使原 EF 工具依赖的 Humanizer 从 2.14.1 提升到 3.0.10，后续迁移和全量集成验证须覆盖。

## 不可变源码与内容哈希

第三方源码位于 `third_party/json-everything/`，上游 C#、resx、meta-schema JSON、公开签名文件、LICENSE 字节不改写；`source-manifest.json` 登记逐文件上游路径、提交、SHA-256。仅为 .NET 10 构建补充本项目的 csproj/Directory.Build.props；原上游 csproj 保留为 `.upstream.txt`。不执行上游构建后置复制/打包、不引入 SourceLink/PolySharp 的构建依赖；保留原 AssemblyName/版本/签名/资源及两份不启用的 keyword 排除项。上游业务代码的全部语义需由 Task 5 固定官方套件证明。

| 本项目编译库 | 版本 | 上游提交 |
| --- | --- | --- |
| Json.More | 3.0.1 | `8b8ab34027de5ad9f4ed50808b8e4889ca69cf4d` |
| JsonPointer | 7.0.2 | `399f198431f65cf6896fe6038f833ef6d0b27a39` |
| JsonSchema | 9.4.0 | `399f198431f65cf6896fe6038f833ef6d0b27a39` |

源码清单 SHA-256：`ab4215e13d7a4c197ca4572762c6a190c5cbee226277331206d210a1dbfcd1ec`。

实际下载的外部 NuGet 包 contentHash（SHA-512/Base64；来源 packages.lock.json）：

- YamlDotNet 18.1.0: `5K+9KFg2TdTl7VXv88Qzi/0lqK6JFoNP3lRuImPYGRV7K/QYklDyTrj4+A+KAki1JsQi6qKY+hDyY7d6WRqjrw==`
- Humanizer.Core 3.0.10: `yZIhtw8sYuvsONzQbZxWpR60tMWYHXoo0DL6nyOqSFiU5POjBTSEyWFpTQtJEZuy+oqiYTXKXY/Mjx7KnqIQFw==`

## 验证

- 独立 UUID 测试 runner 验证项目归属、失败退出码和自有资源清理，原 NuGet seed 只读。
- `tests/contracts/dependencies.test.mjs` 核对固定来源和全部源码哈希。
- 固定 SDK 实际自编译三库通过；解决方案 locked restore 通过；Domain 全量 271 项通过，其中新读取边界 22 项。
- 以上为基础解析与构建验收，尚不表示 Schema 全语义、URL 网络获取、UI 或部署验收已完成。

## Schema 执行与标准资源（2026-10-07）

- 官方 JSON Schema 测试集固定为 `f6fd52a0a95472e079cbfc6ef7f089702b80e045`。`tests/fixtures/json-schema-manifest.json` 保存普通根文件、离线 remotes、19 种登记 Strict 格式的 optional 文件及许可证 SHA。正式根 46 文件／1301 实例、Strict 842 实例全部遍历，无 skip。
- 底层引擎和产品适配器分别验收。正式根中的 5 个自定义词汇方言实例由底层离线引擎验证，产品返回明确 `Incomplete`，因为产品只登记 OAS 3.0、OAS 3.1／2020-12。该差异不能描述成产品接受任意自定义元 Schema。
- `ContractFormats` 使用每次执行独立的格式注册表，修正实际官方用例暴露的 UUID／换行、邮件、IP 字面量、IDNA 及 URI 模板边界；不改第三方源码或全局注册表。Annotation 为默认；Strict 对未登记格式返回 `Incomplete`。
- `third_party/unicode-idna/` 保存 Unicode 17.0.0 官方 Bidi／Script 数据、Unicode License V3、SHA 清单和生成器。`IdnaUnicodeData.cs` 是可重生成的有界标量查表，配合运行库 UTS 46 映射、允许字符、ContextO 和 Bidi 检查；不执行运行时下载。运行库对未知字符的支持仍与固定镜像有关，不承诺不同 Unicode／ICU 版本完全等价。
- `contract-evaluate` 只读一次有界 stdin 请求。协议输入 16 MiB、输出 8 MiB；Schema 原文／包仍受更小业务预算约束。固定包以原文 UTF-8 Base64、URI 和哈希传输，子进程重建并复核。父端每实例最多 2 个运行＋2 个等待，期限包括等待，Schema 5 秒／compare 10 秒；子进程不继承连接串与凭据，超时或取消终止进程树并回收。Schema 无网络获取，只有固定包及九个库内嵌 2020-12 元 Schema URI。
- 独立测试 runner 只读源码，并在容器内复制、locked restore、构建 RuntimeTool、传入实际新 DLL 路径；不会使用工作区旧 bin／obj。Task 5 Domain 完整 366 项通过。这是离线语义与进程验收，尚不表示只读 API、v2 比较、UI 或 4192 部署已完成；compare 实际分发在 v2 引擎任务接入，之前明确未完成。

只读 Schema 校验与版本来源一致性：
- 校验请求仅引用服务端查到的版本/定义，可临时验证草稿；无写权限也能使用，版本变化返回412。客户端来源包、URL和其他未登记字段拒绝，源码来源与相对引用由固定元数据决定。
- API返回 Valid／Invalid／Incomplete、实际评估Revision、安全诊断和模式；JSON null／false／0均作真实实例。非JSON媒体明确未完成，实例值不回显。结构与引用作为新Schema保存检查，示例不匹配仍可保存草稿。
- JSON／YAML根文档和原始来源须规范化后一致；根替换原子失效旧外部包。下载原始YAML使用application/yaml，representation=json返回规范化JSON。维护定义保留导入原文不改写，并独立更新定义位置/内容摘要；组件删除或改名返回受影响Pointer。
- 定义来源摘要采用规范化JSON内容，避免jsonb重新排版误报；既有业务SchemaHash及历史比较算法不改。来源元数据增加可选VersionDocumentHash和DocumentUri；旧未登记摘要来源保守未完成，不迁移回填旧行。
- 组件名称在维护图中不可按媒体类型互相覆盖；历史重复定义仍可读但语义明确未完成。Pointer在服务端转为URI时独立编码百分号，保留斜杠／波浪号转义和物理定位。
- 来源接口阶段完整Domain366、Integration488、Node5全部通过；这些为隔离功能回归，尚不代表UI与4192升级完成。
