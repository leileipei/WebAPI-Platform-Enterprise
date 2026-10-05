# 流量策略实施账本

日期：2026-10-05。已确认规格与计划：2026-10-05-traffic-policies-design.md / 2026-10-05-traffic-policies-implementation.md。方式：Native。

## 隔离与保留

- 工作树：`enterprise/.worktrees/traffic-policies`，分支`feature/traffic-policies`，基线`5f2871ac378c936a0e27bf15d6a47ece4ed97765`。
- 原目录交付改动和秘密不复制到工作树；仅复制已确认规格/计划，并链接相同锁文件下的已有console依赖供构建。
- 原生工作树工具未识别上层目录中的子仓库；使用仓库内工作树，主目录仅追加忽略规则。
- 不自动Git提交/推送/合并；故障测试使用专用项目和秘密，不操作4192服务。

## 前置核对

策略配置契约由Task 1唯一建立；Scope、绑定Revision、运行策略身份、schema能力、请求generation、遥测无关身份、Draft显式刷新、源hash证据均核对跨任务接口。

Console基线：35项通过；Domain基线49项通过。

## Task 1

新增四类配置严格校验和绑定组合规则。缺类型RED后全部领域72项通过；隔离runner缺入口RED后4项通过，实际随机Docker项目运行和自有资源清理验证成功。

全量Node暴露环境相关旧测试：默认沙箱不能绑定回环端口，获准运行后发现ownPortsAllowRepeatedUp使用未声明归属的固定4196/4197，与已存在演示冲突。测试改用三个真实占用的随机端口并声明同一测试owner，保留foreignPort拒绝测试。生产assertPorts和长期服务未修改；全量Node回归92项通过。

Ruling: 历史2.1快照可以包含同源策略的多个修订。FrozenPolicy增加可选SourcePolicyId供历史Review和删除保护使用；Id仍保留运行ID以对应历史绑定，普通发布候选的源ID与旧候选格式兼容，不读取当前策略重建历史。无此来源会漏判回滚草稿对源策略的保护。

## Task 2–10 已验证检查点

- Task 2：六项策略管理集成测试通过，验证 Scope、ETag、复制、审计与配置脱敏。
- Task 3：绑定、目录和导入等 34 项通过；认证开关仅替换本路由私有策略。
- Task 4：领域快照 81 项通过，2.0/2.1 兼容与稳定运行身份覆盖。
- Task 5：发布、审批、历史回滚、引用删除和幂等 45 项通过。历史回滚恢复原摘要。
- Task 6：当前实例能力与 ACK 35 项通过；无 2.1 能力的启用节点阻止高级发布。
- Task 7：Redis 七项通过；两真实客户端共享桶、TTL、超时单次派发和取消覆盖。
- Task 8：领域 91 项与 Registry 三项通过；epoch、防迟到完成与并发探测覆盖。
- Task 9：真实代理管线及相关回归 32 项通过；在途请求继续使用原 generation。
- Task 10：领域 92 项、遥测和代理 28 项、观测查询 52 项通过。新增策略决策计数不含应用或版本维度；API/应用/后端筛选显示不适用，缺源为空。日志/Trace安全策略决策与 CSV 可读；三源实际闭环尚待 Task 12。

Task 11：前端状态缺模块 RED 后 41 项 Node 测试通过。构建使用同锁文件下现有依赖。运行 pnpm 时默认依赖自检尝试 install，未执行安装；显式关闭 verify-deps-before-run 后走现有 TypeScript/Vite 构建，不修改共享依赖。浏览器 QA 尚待 Task 13。

## Task 12 真实闭环

实际独立镜像完成八项：当前实例能力门禁、两节点共享 3/17 额度、真实后端熔断恢复、共享策略部分发布保留旧修订、Redis Reject/Allow、在途请求继续原 generation、2.0/2.1 原字节回滚与 LKG 重启、三源策略决策与根 Trace。`disposable-verification.json` 保存临时项目资源清理为零的证据；独立页面评审另用新项目，不能混同为清理失败。

完整回归：领域 92、集成 240、Gateway 50、Console Node 41、运行脚本 Node 59，全部通过。实际源副本逐文件 hash，镜像记录 sourceManifestHash 与 imageId；baseCommit 不代表已提交本批代码。

验收脚本调试记录保留：随机端口在重建/重启后变更，LKG 卷需要非 root 归属初始化，测试控制端点返回空响应，以及回滚到当前 desired 的操作按既有规则被拒绝。均核对真实响应后修正脚本，不放宽产品规则、不删除失败事实。

## Task 13 UI 迭代

1440px 真实像素与浏览器检查发现并修正两项：发布 Review 引用未加载完时误允许加载；策略列表发送空 type 导致服务端正确拒绝查询。前者在真实 UI 中 RED 后禁用依赖入口，后者通过新请求构造测试 RED→GREEN；补充字段旁配置反馈、完整引用分页后才标识一致。浏览器只读 GET 错误注入用于失败状态截图，真实写操作、412 和权限均由真实 API 处理。

为缩短 UI 调试，临时静态预览连接专用真实 API；这些调试截图不作为最终固定镜像证据。全部修正会重新固定源码构建，再复验与逐像素审查。

### Final review fix pass

- 独立 reviewer gpt-6-astra read-only 全变更审查：2 Important，1 Minor，0 Critical；Minor 因默认保护行为影响纳入同一修正批次。
- RedisClockRollback RED：实际 Exceeded 预期却 Allowed；PathEditAndTimeoutUnbind RED：基础 30000 实际 1000；CircuitDefault RED：状态码集合不同。随后一次修正，有效时间不倒退、基础/有效超时分离、完整 500–599 默认。
- Ruling: 路由工作 DTO timeoutMs 保持基础配置语义，新增 effectiveTimeoutMs 表示策略覆盖结果；运行快照折叠值语义不变 — 避免普通编辑持久化隐藏覆盖值 — 调用方须用有效字段查看工作覆盖结果。
- Ruling: 不派第二审查员，以复现测试和必要全回归证明本次修正；最终源码/镜像/截图及ZIP由交付校验封存 — 遵循一次独立 review 与一次 fix pass — 未执行生产容量仍明确延期。

修正后全量回归：Domain92、Integration241、Gateway51、Console44、Runtime59全部通过；真实固定镜像9项场景通过，包括路径编辑/解除超时/正常审批发布后运行基础超时30000。Node生命周期测试默认沙箱拒绝loopback绑定并触发native assertion，获准端口后单文件及完整59项通过；未改产品逻辑。

Ruling: 用户已确认本批不commit/push/merge并保留评审入口，finishing-a-development-branch按保留分支执行，不再次要求选择 — 已有授权覆盖本次收尾 — 工作树和未提交更改保留。没有Git提交历史可恢复本批账本，因此保留本计划scratch，不执行删除。

### Task13 最终封存事实

最终固定源码清单778777cf72036826d7520e51a5f675a5b254a50c491213072df2c2868f53efcf，独立入口34245。完整回归487项，9实际业务场景+1 C# E2E通过；最终15张1440px截图来自同一镜像，逐图像素检查完成，交互检查全部通过。P01–P19已按真实测试/场景/截图证据映射。原10个ZIP摘要保留，交付校验将再次验证；不改4192。新ZIP封装以实际执行的CRC/hash/秘密扫描和外部manifest为最终结果，不覆盖任何旧包。
