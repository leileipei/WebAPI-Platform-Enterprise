# 核心工程实施决策与验收边界

原40页原型保留；本文件逐项保存全部实施裁定及判断错误时需要补充的工作。

## 全部实施裁定

1. Ruling: 新建enterprise独立Git仓库及feature/core-loop分支，保留prototype原目录 — 原目录无Git，批准的计划已明确独立工程 — 若错误隔离成本为调整仓库布局，不影响原型。

2. Ruling: 计划标题由中文“任务N”改为“Task N”以兼容技能任务提取脚本 — 任务内容和用户范围不变 — 若错误仅影响任务提取。

3. Task 1: Ruling: 尚无行为的Gateway/Worker/console项目延后至其拥有的任务建立，不放运行时占位服务 — 测试基础工程仅建Domain/Contracts/Infrastructure和Integration — 若错误需提前补项目配置，不影响接口。

4. Task 1: Ruling: EF映射从已批准源字段机械生成，约束后缀另入同迁移 — 避免手抄32表遗漏且保留逐实体类型 — 风险为未标注字段可空性，已在模型文档与测试中明确。

5. Task 3: in progress。RED真实HTTP6失败（治理端点404），随后实现。Ruling: 首期跨组织用户注册表 / 停用 / 整体角色和Scope分配由平台管理员管理，组织资源及组织自定义角色仍按Scope授权 — 全局用户可能跨多个组织，单一组织不能停用其他组织账号 — 若需要组织成员治理，应新增限定组织的成员关系命令，当前避免越权。Ruling: 关键写入使用统一PG事务授权锁，将实际授权检查和角色/Scope变更串行化 — 保证事务提交时授权不陈旧 — 首期控制面写吞吐受限，后续可按授权版本细化锁。

6. Task 4: in progress BASE b74f1f8。RED10真实HTTP测试失败（端点404）。Ruling: 简化首期路由语法为整段命名参数及末尾命名通配符，同形参数和大小写等价规范化 — 与ASP.NET匹配保持一致且避免未验证约束语法 — 若需内联约束/可选参数将另补验证；静态始终优先于参数和通配符，priority在各类型内生效。Ruling: 每条新路由持久化API Key认证绑定，显式匿名需额外policy.write — 满足基础认证绑定并默认安全 — 后续策略扩展须复用绑定，不重复生成认证事实。实际运行版本只在所有在线启用节点一致时返回公共版本，其他情况null，不把desired pointer伪称实际运行。

7. Task 5: in progress BASE 2d254ca。RED8真实HTTP测试失败（导入与凭证端点404）。Ruling: 每个导入Operation对应独立Version，已有Version仅可对应一条当前Operation；已有Route覆盖需显式ID及revision — 参数/Schema表没有operation_id，避免把不同Operation参数混在同一个版本 — 完整多Operation同版本建模另行扩展，不谎称支持。已有Route认证绑定保留；新Route默认ApiKey。幂等导入按任务5原计划在任务6公共执行器完成后接入并回归。

8. Task 6: in progress BASE54e56b4。RED7真实HTTP失败，审批端点404和导入幂等缺失。Ruling: Production flow固定两级顺序1/2，每级required_count允许1到5，任何人最多占一个审批席位 — 保留源字段并落实独立性 — 默认每级1人，较高人数需要更多独立账号。Ruling: CandidateBytes内部含凭证摘要，ReleaseDTO仅投影安全视图而不返回hash — 详情不暴露凭证hash约束同样适用于发布预览 — 后续Snapshot公共接口也须脱敏。

9. Task 7: in progress BASE46870f4。RED14领域测试NotImplemented；GREEN15（另加共享Cluster隔离）。Ruling: RuntimeCluster用native SourceId +实际配置内容派生运行ID — 选中API更新共享Cluster时，未选择API必须保留旧后端配置 — 协议含SourceId供治理关联；未来Policy扩展应同样避免共享配置附带影响。Ruling: 空路由的有效Snapshot允许用于撤除最后路由，仍需有效版本/协议 — 不能让禁用最后Route永远无法发布 — 无LKG首启仍不Ready，空有效版本返回404。

10. Task 8: Ruling: 授权线性化点为Worker锁内复核及PG事务提交，提交后的Outbox重试完成已授权事实 — Building撤权不得移动pointer，提交后不能偷偷撤销目标或破坏PG与Redis一致性 — 如需撤回已提交目标必须明确发起新的经授权协调记录。Ruling: 全部启用节点都必须在线且至少2个，构建冻结所有实例 — 缺节点不降低ACK目标 — 较大部署中需要先显式停用已退出节点才能发布。Ruling: Submit后Version内容revision改变则发布409或构建Failed，不覆盖草稿 — 首次发布版本需要与审批内容一致且不可变 — 必须重新提交审批。新增PublishRequestedBy/PublishTraceId迁移明确记录实际发布人；申请人与发布人分离。

11. Task 9: Ruling: 节点注册以显式Nodes:Enrollments环境/名称/SecretFile绑定，不提供通用注册秘密；摘要唯一且库内停用不能被配置重新注册绕过 — 无默认节点身份、不能用同密钥跨环境注册 — 部署前需配置每节点独立文件，密钥轮换需显式运维流程。Ruling: 进程重启生成新instanceId，旧冻结发布目标保持不变，不把新实例填入旧ACK席位 — 遵守冻结实例身份 — 发布中重启需新恢复协调记录。Ruling: Publish:AckTimeoutSeconds由Worker显式配置，DeadlineAt在发布PG事实中持久化；API入口自身检查截止时间 — 未引入源后续system_settings模块 — 配置变更只影响后续构建，不延长已有发布。心跳不充当ACK；负ACK或超时保留真实已应用状态，不自动恢复。

12. Task 10: Ruling: 新请求准入门只覆盖路由选择及取得匹配generation的lease，立即释放后才做响应/后端I/O；切换持门等待YARP实际ConfigurationApplied，老请求持旧lease继续 — 避免路由/auth异步指针混用且不等待老后端结束 — 切换瞬间新请求会短暂排队，未做生产吞吐承诺。每generation独立YARP Route/Cluster IDs、只读集合、旧lease结束后回收。Ruling: /health/live及/health/ready固定运维路径，其余/health前缀仍是普通业务 — 无LKG不应意外漏过NotReady — 这两个具体路径不能配置成业务代理。Ruling: Destination权重在RR/Random/LeastRequests/PowerOfTwoChoices实际执行，FirstAlphabetical按ID排序不使用权重 — 保留五种源策略 — 字母排序不是Destination显示名称。

13. Task 11: 初始3 RED回滚端点缺失；历史原字节及审批3 GREEN。补充真实Worker开发宿主RED缺RouteService，注册后GREEN；负ACK任意64位字符串泄漏摘要RED200，限定诊断代码后GREEN；节点相同configVersion不同sequence读模型RED缺desired字段，补环境权威pointer。Ruling: retry复用已审批相同bytes无需重复审批，但重新授权实际发布人和冻结当前实例，以RecoveryOf关联新记录 — 既不修改旧Failed也不复用旧ACK — 修改内容必须新建普通发布。Ruling: 历史回滚Review用通用历史路由/后端标签及当前API管理名称，原字节/hash为权威 — 原Snapshot未保存源显示名称 — 后续若需要精确历史名称应增加冻结管理元数据，不捏造。

14. Task 12: Shell/治理页面TypeScript+Vite构建通过，transport2通过，真实API前置15通过。CUA观察错误密码真实401消息、管理员登录与空组织页、1440×1024页面无水平溢出、error日志空，截图governance-shell.png。独立角色账号/412有数据表单浏览器验收因自动审批拒绝测试账号持久创建而待用户明确批准；已异步提问，不标Task12 complete。Ruling: 同时推进Task13不依赖该账号批准的页面源码及已有API回归 — 避免暂停已授权开发 — 必须补完Task12浏览器证据后才标完成，不混淆API与UI证明。

15. Task 15: taken BASE58aad47，brief已读。首个真实跨容器E2E测试RED明确缺专用Compose/context，不将Missing条件Skip或health200视为成功。提供单独随机webapi-enterprise-e2e-*项目、独立PG/Redis/CP/Worker/两Gateway/两Backend/ConsoleHost，两个独立LKG卷；测试流程A→B→审批回滚A、切换时已进入A请求仍完成A。Ruling: 持久本地验收账号仍待用户明确批准，不执行被拒绝的prepare-local。新E2E夹具改为进程生存期临时、只含合成测试数据，EXIT自动销毁本次随机项目的卷和凭证；须重新由自动审批核验低风险临时测试范围后才运行 — 避免持久访问扩张并补真实容器证明 — 意外强杀可留测试项目，运维文档必须明确只清理精确记录的项目，不自动扫描删除其他资源。console同源代理保留原Host，控制面Origin/CSRF保持检查，依据Microsoft YARP官方RequestHeaderOriginalHost文档。

16. Task12–14 Ruling: 三个相邻UI任务以一份可编译源码提交交付，task-done共享58aad47基线 — Shell/Main跨页面依赖，不引入不可编译的中间提交 — 错误成本为Git按页面再拆分；每个任务的独立API前置、浏览器RED/GREEN与证据仍逐项保留。

17. Final: Ruling: AMD64、企业TLS/网络、容量/SLA、HA/恢复和正式密钥管理维持目标环境验收事项 — 首期交付只证明本机Linux ARM64核心行为，不形成生产承诺 — 若错误需追加目标环境部署、安全及容量验收。

18. Final: Ruling: SSO、完整监控告警、复杂策略、URL/YAML与完整Breaking Change分析维持后续范围 — 已批准核心计划及40页能力映射明确延期 — 若错误需另补这些模块的开发与验收。

19. Final: Ruling: 网关逐节点应用、凭证经Snapshot生效，不承诺跨节点瞬时一致切换或即时撤销 — 与已冻结实例及真实ACK状态一致，失败保留部分节点事实 — 若错误需增加全局切换协议及独立即时撤销通道。

20. Final: Ruling: 保留全部40页原型；首期仅承诺能力映射中的真实核心后端 — 不将演示交互当成未实现业务的生产成功 — 若错误需补齐后续模块，不能仅改页面提示。

## 交付方式

仓库仅有 feature/core-loop，无远程与可合并基线；保留本地提交及源码包，不虚构PR或合并结果。全部Critical/Important进入同一修正阶段，未再派独立复审。没有延期Minor。原始审查、修正验证和实施记录均在 docs/evidence/core-loop。

持久验收账号授权与Secret全文读取被自动审批拒绝，未执行。另获准的随机可销毁项目只含合成数据；本轮只检查凭证控件状态而不读取Secret。
