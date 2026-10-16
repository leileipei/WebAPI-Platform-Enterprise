# 固定 main 独立验证执行清单

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans 执行本验证清单。

**Goal:** 独立验证 `9cef7cfe728ddbb98f19e7b645fda8d23eceb227`，区分完整回归、真实数据库升级与性能资格。

**Architecture:** 从固定提交 Git archive 导出零源码覆盖副本；既有测试使用随机 owner、独立数据库和 Redis；证据保存在当前性能 worktree 的独立目录。

**Tech Stack:** .NET、PostgreSQL、Redis、Docker、Node 24.19.0、pnpm 11.25.0。

**Spec:** 已批准性能计划 Task 12；用户选择“先完成固定 M，再单独验证当前 main”及本轮精确历史状态保护例外授权。

## Global Constraints

- 保留原 649 文件工作字节、所有现有工作区修改及账号数据；仅认可已记录 main 推进及四份文档 Git 状态变化的例外。
- 不授权新增产品提交、推送、合并、4192 部署或使用正式运行数据库。
- 固定 M 的性能资格只属于 M；Gateway 源树一致不等于整个当前 main 合格。
- 重负载验证顺序执行；每个独立 owner 的容器、卷、网络及私有秘密按身份清理。
- 失败日志保留。真实源缺陷先诊断，不在无授权情况下更改固定版本后继续沿用旧测量。

## Review Focus

- 新增迁移及模型快照与已有性能索引兼容；升级保留已有业务数据。
- 新增发布中心、审批和 Pipeline 不能绕过项目/环境权限及生产审批。
- 管理列表性能接口变化不破坏分页、权限、历史快照与回滚。
- RuntimeTool 构建、数据库秘密挂载、UTF-8 输出与命令退出码能够真实反映验证结果。
- 源/测试/运行依赖/日志必须绑定当前固定 SHA；禁止拿 M 或分支测试冒充当前 main 全绿。

### Task 1：固定源码和状态保护

- [ ] 导出固定 main；逐文件核对 Git blob 与 SHA256，保存归档和依赖版本指纹。
- [ ] 再次核对原 649 文件内容及唯一已授权状态例外。
- [ ] 记录相对 M 的源代码/迁移差异和待验证范围。

### Task 2：完整回归与前端构建

- [ ] 固定导出内执行 `scripts/check-contracts.sh domain`、`integration`、`gateway`，每项指定 TRX 目录、现有 Node、独立未占用 `10.251.x.0/24` 网段。
- [ ] 执行 `scripts/check-local-runtime.sh unit`，一次覆盖 Runtime/Console；另执行固定源 `node --test scripts/performance/*.test.mjs`。
- [ ] 仅使用已安装依赖，执行 `pnpm --dir console build`；不要重复 Console 测试。
- [ ] 收集实际 TRX/Node 汇总、零失败/跳过、退出码、日志 SHA 和 owner 清理结果；重新核全部固定源字节。

### Task 3：数据库与运行边界

- [ ] 检查本提交迁移测试的实际覆盖；在隔离数据库验证空库、固定 M 已有数据升级、失败阻断/回滚，不把仅新建库测试称为升级演练。
- [ ] 按当前固定源脚本执行 RuntimeTool 真实集成测试（独立 owner）及可验证的发布中心/跨环境审批闭环。
- [ ] 若某项暂未执行，显式列为 pending，不能用相同 Gateway 源树或已有分支报告替代。

### Task 4：审查与交付

- [ ] 汇总本固定版本证据和每项范围/限制；此前 M 数字仅列为 M 的来源，不继承。
- [ ] 本轮完成一次独立最终审查；重要发现诊断并完成受影响验证，保留首个失败。
- [ ] 完成当前 main 资格报告、原文件保护和所有本轮资源/秘密检查；不包含推送或部署。

## 执行裁定

本清单是用户已授权验证工作的细化，不添加产品需求或业务行为，不要求重复批准同一验证范围。仅在发现需要改变产品源码或扩大例外时重新明确范围。


Ruling: 原生 contract SDK 挂载源码为只读，TRX 写入 /workspace 在首轮执行前被拒绝。保留首轮失败，改用 console;verbosity=normal 的实际逐项结果和最终计数；解析器先失败再通过六项检查。保持产品和测试源码不变，当前版本测试资格以该详细原生日志及退出码为证，不把编译成功称为测试通过。

Ruling: 原生安全代理用例默认 security-build 清单路径不属于本轮新构建目录。使用用例已有的 WEBAPI_SECURITY_RELEASE_FILE 指向同一个固定 main 原生清单，保留首次 ENOENT 和无服务创建的清理证明，重跑完整运行集成组；无产品/测试源码覆盖。

Ruling: 原生诊断构建清单仅列顶层 DLL，运行清单递归列全应用 DLL。独立镜像核验按两个真实范围分别匹配，并另存十个诊断语言资源 DLL 的实际哈希，不误称旧清单覆盖全部递归文件。
