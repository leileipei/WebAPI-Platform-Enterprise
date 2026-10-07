# 网关JWT依赖与离线验证边界

第二批任务4固定使用Microsoft.IdentityModel.JsonWebTokens与Microsoft.IdentityModel.Tokens 8.19.2；现有控制台OIDC依赖版本保持8.19.2。Gateway直接引用两包，所有受影响锁文件必须用`--locked-mode`恢复。

|包|版本|NuGet锁文件contentHash (SHA512/base64)|
|---|---|---|
|Microsoft.IdentityModel.JsonWebTokens|8.19.2|`ui3fuBT4fs8kdKfBthI4NzLYIBIneEVS8UrL1JVBzAn80UiKmngBBi0BEByE7n/9c+EElcfFlCMFFTqpkBSLNA==`|
|Microsoft.IdentityModel.Tokens|8.19.2|`GtPC1S02uH1gOO4fQ+zRysIicKmEXaYFP8PIkdJYXqMyruYhopre4ozVHp0XiDSA0+GJvOZH9prxCPvgBMg4Ww==`|

锁文件刷新通过固定SDK镜像`mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`完成；解决方案与单独的RuntimeTool分别恢复。核对所有原依赖resolved及contentHash没有变化，新增中央声明使部分原Transitive项改为CentralTransitive，Gateway改为Direct。

许可为[8.19.2仓库MIT License](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/8.19.2/LICENSE.txt)。验证器依据该固定版本的[TokenValidationParameters](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/8.19.2/src/Microsoft.IdentityModel.Tokens/TokenValidationParameters.cs)及[ValidateTokenAsync](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/8.19.2/src/Microsoft.IdentityModel.JsonWebTokens/JsonWebTokenHandler.ValidateToken.cs)配置离线签名验证；不使用已弃用的同步ValidateToken入口。

执行前预检16KiB、三个非空规范base64url段、JSON最大深度8、头和载荷累计最多256个对象属性或数组元素，并拒绝所有层级重复属性（包括转义后重复名称）。发布配置已通过严格Domain校验，只以Ordinal kid选中一把公钥。允许RS256/ES256与JWT/at+jwt；禁止令牌自带密钥、网络地址、加密、压缩及关键扩展。Issuer/audience/type均精确校验，关闭audience尾斜杠宽容。

库负责签名、issuer、audience和type；固定调用时间负责NumericDate、最大生命周期及clockSkew检查。exp/iat/sub和单一字符串应用声明必需；NumericDate不得超出.NET合法日期范围。成功结果只公开Success/FailureCode；issuer/sub/应用声明及已验证Bearer没有公共getter或record ToString，限制为网关请求内存。未启用discovery、resolver、内省或远程刷新，错误统一invalid_jwt，不输出令牌或声明。

阶段状态：本文件描述验签组件。真实JWT路由授权、重试、缓存、固定版本验收及4192部署需后续任务完成，不能据此视作运行环境已启用JWT。
