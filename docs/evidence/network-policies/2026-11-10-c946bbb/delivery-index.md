# 第3包 CORS、IP 与 JSON Transform 交付索引（c946bbb 门禁解锁轮）

日期：2026-11-10。状态：固定代码及本机独立验收完成，本机 macOS/OrbStack 以 Docker-in-Docker 完成全量推进至全绿。

固定代码：`c946bbb708bc9de8438cd96561ba085c08a103ae`；镜像：`sha256:13eb95c36c071e7bdb29f84691b04407b9ab334dd47b521f18f685e836851290`；独立 owner：`c8935ba7-683c-48d3-8796-c95911222bdc`。本轮新空库，未恢复原 4192 数据，未升级 4192，第2包保持暂停。

## 本轮定位

c946bbb 本身仅重写一处确定死锁的锁竞争测试（`tests/runtime/lock-race.test.mjs`，9+/7-），使其对齐 fail-closed 的 `withRuntimeLock` 契约；这正是此前 macOS 上 runtime 套件挂起、阻断 network-policies 门禁的根因。其间并入的凭据治理（G01-G13）为独立包，未触及本能力。经核对，**network-policies 生产代码（`src/WebApi.Gateway/Policies`、`Configuration`、`console/src/policies`）与 226dfe4 交付逐字节一致**，故 226dfe4 的独立评审沿用，本轮不再执行第二次独立评审（secondIndependentReview:false）。详见[交付评审处置](final-review.json)与[上一固定版本](../2026-10-14-226dfe4/delivery-index.md)。

## 能力与验收

CORS、IP过滤、请求/响应Header、后端路径、Query及双向JSON add/replace/remove/copy/move均完成。原API身份、方法、授权、审批与回滚模型保留。

- 固定五组：4540通过、0失败、0跳过（runtime 298 / console 321 / domain 1251 / integration 2115 / gateway 555）。
- [严格门禁](acceptance-result.json)：P3-01至P3-12全部通过；实际7应用同镜像、3个Console静态文件Hash一致、proofSha256 `8658dac8fcc5700418add11c038a055fba99665f9ddfde62874a04d634e91dc6`、隐私排除均核对。
- [真实SDK E2E](sdk-e2e-result.json)：1通过、0失败、0跳过，[原日志](sdk-e2e.log)及[来源/Hash清单](acceptance-input.json)可复核；未使用缺材料skip。
- [逐项覆盖](coverage-ledger.json)与[交付清单](delivery-manifest.json)关联要求、代码版本、日志和实际材料。

## 页面与运维

[15项实际CUA](public/P3-11.json)全部在本轮1440px固定版本执行：

- [JSON请求预览](public/json-request-preview.png)、[JSON响应预览](public/json-response-preview.png)保留大整数、精确小数与null；[并发修订412](public/revision-412.png)保留输入；[脏导航](public/dirty-navigation.png)与[范围迟到](public/scope-late.png)。
- [九类绑定](public/nine-bindings.png)、[冻结Review](public/release-review.png)、[跨环境映射](public/promotion-mapping.png)、[只读配置](public/readonly.png)、[服务端校验](public/validate.png)、[复制](public/copy.png)、[创建](public/create.png)、[编辑](public/edit.png)、[空null大整数](public/exact-null-number.png)、[实际Trace](public/logs-trace.png)。

[P3-12](public/P3-12.json)核对主工作区原内容/Git状态、4192运行身份及原行保留；第2包暂停 owner 运行容器为0。未执行 main 合并、推送或 4192 安装。

管理员步骤、限制与错误处理见[操作说明](../../../deployment/network-policies-runbook.md)。
