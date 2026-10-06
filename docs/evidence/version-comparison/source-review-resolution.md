# 版本比较与风险评审：源码审查处理记录

日期：2026-10-06。此记录证明源码审查和自动化回归阶段，不代表固定提交验收、浏览器设计QA、部署或生产验收完成。

整批独立审查由新上下文审查代理完成，覆盖批准规格、实现计划、文件SHA清单及工作区差异。发现六项问题，全部接受并在一次修复阶段处理；每项均先观察复现测试失败，再运行修复后的检查。无无法判断事项，无延期小问题。

| 问题及最终等级 | 修复及验证 |
| --- | --- |
| P1：响应headers/links、媒体encoding、根或路径servers等未支持约束漏判，使结果错误显示Complete | 各层未支持字段给出Limited及Unknown证据；UnsupportedOpenApiConstraintCannotClaimComplete先失败后通过，领域全量249项通过，另补测相同与变化方向7项通过 |
| P2：属性显式null与非字符串$ref被当作可支持输入 | 验证Schema字段类型、空值和引用；NullPropertyAndInvalidReferenceCannotClaimComplete先失败后通过，纳入领域249项回归 |
| P2：截图不存在被误当作可选UI证据不存在 | 限定可选证据文件的ENOENT处理，逐张验证实际PNG、SHA与尺寸；MissingScreenshotCannotBeSwallowedAsMissingOptionalUiProof先失败后通过 |
| P2：清理前未核对网络所有者 | 容器、卷及网络均验证项目与owner，完成条件要求网络残留为0；OnlyForeignOwnerNetworkRejectsCleanup先失败后通过；运行工具全量104项通过 |
| P2：比较GET及发布交接读取未应用15秒期限 | 统一读取适配器并处理忽略AbortSignal的晚到响应；期限复现测试先失败后通过；控制台全量99项、TypeScript及Vite通过 |
| P2：AcceptedRisk沿用comparison.reviewed，风险接受事件筛选会漏记录 | 审计动作改为comparison.risk_accepted，保留原幂等命令标识；ReviewAppendsWithoutApprovalTaskAndReplaysHistoricalAfterEdit先失败后通过；集成全量405项通过 |

网关52项通过记录来自本批发布引用实现阶段；最后六项修复未改变网关协议或实现，因此未重复相同网关套件。

实现过程中作出的四项裁定及其代价：

1. 契约来源深度仍限制64，指纹与响应包装层使用128，避免有效来源被DTO包裹后拒绝；若判断有误，代价是深层报告序列化失败，已有边界与真实HTTP重放检查。
2. 公共幂等执行器新增可选响应序列化参数，仅比较服务启用；其他命令默认值和请求哈希保留。若判断有误，代价是比较保存或重放深度错误，已有真实HTTP检查。
3. 修改发布状态的回执省略风险备注，具备当前契约读取权限的GET与预览显示备注；代价是查看备注需要后续读取，以免权限撤销后从幂等回执恢复受保护文本。
4. 审查包以基线提交到限定工作区文件的差异及逐文件SHA固定范围，包含新增未跟踪源码。批准计划约定不自动提交，本批不能借审查提前提交；代价是审查依赖SHA清单而非提交范围，提交仍待明确授权。

范围仅为本批版本比较相关源码、测试、迁移、执行器与说明。主目录六项既有修改、历史部署证据及依赖链接保留；没有提交、推送或切换4192。
