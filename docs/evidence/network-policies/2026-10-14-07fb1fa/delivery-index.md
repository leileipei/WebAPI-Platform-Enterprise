# 第3包 CORS、IP 与 JSON Transform 交付索引

日期：2026-10-14。状态：07fb1fa固定独立验收通过；最终全分支审查已完成并发现3项Important，正在修复及复验，目前不可合并；尚未main合并、推送或升级4192。第2包保持用户要求的暂停。

固定源码：`07fb1fa82390f35a98e029d00b795dee2944271d`。固定镜像：`sha256:f9f08bc809d06d2ec3d3ab5d320e4e856ad86e38123f8cc813f7fffe7ebab266`。独立owner：`dc4f9e62-4472-44b2-8532-412be5369009`。数据库为本轮新建，没有恢复原4192数据。控制面、Worker、Console及TEST/PROD四个Gateway共7个应用容器均由严格门禁核对为同一镜像；实际Console静态资源逐文件Hash一致。

最终审查确认：允许的Content Header设置失效、异常多方法CORS预检可能500、前端误拦空DenyList。原07证据保持；后续修复必须取得新固定版本验收。

## 能力与结果

新增CORS、IP过滤、请求/响应Header、后端路径和Query改写，以及双向JSON字段add/replace/remove/copy/move。保留原API身份、方法、授权、独立审批及发布/回滚流程。JSON默认每方向256 KiB、上限1 MiB，原始大整数/小数与null保真，gzip/br有界解压、原子失败、缓存最终映射结果及安全重试均有对应固定测试或实测证据。

- 固定五组回归：3341通过、0失败、0跳过。Runtime234、Console295、Domain1065、Integration1409、Gateway338。
- 独立正式场景P3-01至P3-10均实际生成；15项1440px CUA及原环境保护形成P3-11/P3-12。
- [严格门禁结果](acceptance-result.json)：passed=true、stage=isolated-pre-install；[输入及Hash清单](acceptance-input.json)可核对全部公开材料。
- [正向SDK E2E](sdk-e2e-result.json)：1通过、0失败、0跳过；[原日志](sdk-e2e.log)记录固定来源。它读取真实严格结果、来源身份及材料Hash，不使用缺材料skip。
- [逐要求覆盖台账](coverage-ledger.json)按requirementId、sourceIdentity、testLog、evidencePaths、status、remainingGate关联实现与证据。
- [交付清单](delivery-manifest.json)记录公开文件Hash及已知夹具秘密零匹配；私有密码/API Key/JWT/HMAC列表不复制到这里。

## 页面与操作

[15项实际动作](public/P3-11.json)覆盖创建、编辑、校验、复制、9类绑定、请求/响应JSON预览、null/大数保真、只读、412输入保留、未保存导航、scope迟到隔离、冻结Review、晋级目标映射和日志/Trace。截图使用1440px原尺寸；JPEG转PNG保持解码后像素相同，见[像素核对记录](ui-extra/pixel-receipt-all.json)。

只读账号无配置写入入口，按`policy.read`及数据范围仍可执行不持久化的JSON预览，见[补充截图](ui-extra/readonly-preview-extra.png)。晋级明确选择目标timeout/rate_limit/CORS/IP修订；Transform全体规则冻结，见[映射](public/promotion-mapping.png)和[上半部](ui-extra/promotion-mapping-top-extra.png)。本轮晋级记录已Completed；截图以实际历史状态展示，不把旧ACK冒充当前节点验收。

[观测实测](public/P3-10.json)的较短完整覆盖窗口中Metrics/Logs/Trace均Available，根请求关联实际Server/Client span。页面1小时范围显示Partial，保留“数据覆盖不完整”，同时展示真实请求、Transform Applied与JSON操作数1，见[Trace截图](public/logs-trace.png)、[日志决策](ui-extra/logs-decisions-extra.png)。预览、映射实例及截图只使用本轮固定合成夹具。

管理员录入、审批、双节点ACK、跨环境映射、验证、回滚与错误处理见[操作说明](../../../deployment/network-policies-runbook.md)。

## 历史失败与保护

本轮之前的首次发布、预览方向、观测窗口、HTTPS入口、最小范围读取、目标参数与JSONB格式比较等诊断失败及其复验材料继续保留；本轮固定48f9aad构建的NU1902/NU1903依赖阻断也保留。Pkcs按独立设计与精确14文件授权更新到10.0.13，未关闭NuGet审计或警告门禁。新07fb1fa重新构建及完整固定验收通过，旧结果不代替新结果。

本轮第一个等待器在UI预检完成前超时，未开始业务场景；第二次同固定版本调用产生全部正式记录。所有私有原日志在本计划独立工作区保留，[原日志Hash索引](retained-log-index.json)同时包含历史成功/失败日志，不把全部索引条目算作失败。CUA选择器及待核对的Span选择消失观察见[操作记录](ui-extra/operator-notes.json)，不伪装为已确认产品根因。

[P3-12保护证据](public/P3-12.json)核对主工作区原文件内容与Git状态、4192运行身份及71表原行保留。原账号/数据/安装未改变；第2包暂停收据保留，暂停owner运行容器为0。

## 边界与待办

这份材料证明本包固定源码在独立本机Docker Linux环境中的验收。旧节点案例是能力声明门禁，未运行旧二进制；受控HTTPS入口保持证书验证，仅用于本轮夹具，未配置企业DNS/TLS/LB。AMD64、HA、容量/SLA、企业真实服务正文契约、正式生产上线没有本包验收结论。

最终独立全分支审查待完成；交付文档是以本固定源码及实际材料为依据的工作区补充，尚未另行提交。提交/合并/推送/4192部署仍以具体授权为准。

后续状态：原3项Important已在226dfe4修复并通过新固定验收，见[当前交付索引](../2026-10-14-226dfe4/delivery-index.md)。本目录保留07fb1fa的历史证据和当时审查状态。
