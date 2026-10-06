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
