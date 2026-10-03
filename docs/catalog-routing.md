# API、版本与路由工作区

API、分组、版本、参数、Schema、Route、Cluster和Destination均持久化。API Version不含environment归属；Route绑定环境，并在服务端校验Version项目和Cluster环境。已发布或sealed版本不可覆盖修改，参数/Schema修改通过版本ETag防并发。工作区编辑不更改历史Snapshot原字节。

同形路由按环境、HTTP方法、大小写一致的规范化路径冲突；数据库route_methods唯一索引承担并发保护，启用路由同事务维护。支持整段命名参数和末尾命名通配符，首期不支持内联约束/可选参数。静态、参数、通配符依次匹配，各类内部priority值越大越优先，matchOrder越小越先匹配。禁用路由释放工作区占用，运行流量仍受已发布Snapshot控制。

基础认证通过实际Policy及RoutePolicyBinding保存，默认API Key；显式匿名需policy.write。基础超时1到300000毫秒；后端必须HTTP(S)、无userinfo、查询或片段，按Upstream:AllowedOrigins的精确origin允许列表校验，默认仅http://test-backend:8080。允许列表由部署管理员配置，业务用户不能任意探测内网。

API详情中workingRevision为工作区版本；runningConfigVersion只在启用节点近期心跳且共同版本一致时提供，否则未知（null）。pendingReleaseId展示当前环境待处理发布。desired pointer不冒充节点真实运行版本。

最后启用Destination不能删除或禁用；被引用Cluster和版本拒绝删除。字段结构问题返回422，授权不足403，业务冲突409，旧ETag412。请求限制8MiB，单文本4MiB，结构深度16，子项最多5000。

已通过真实PostgreSQL/HTTP验证同形路径、方法区分、并发单赢家、静态优先、版本不可变、跨环境Cluster、Destination限制及快照隔离；尚未据此声称网关运行闭环已经完成。
