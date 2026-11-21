# 项目计数首批浏览器验收计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 P04-C03、P04-C04、P04-C05 补齐固定版本真实接口与浏览器证据，保留 454 项历史资格。

**Architecture:** 复用 ApiFixture 的真实 ControlPlaneApp、Kestrel、PostgreSQL 与 ConsoleHostApp，构建当前提交的生产前端静态资源；新增只用于验收的具名夹具和浏览器驱动。业务数据均为隔离合成数据。

**Tech Stack:** .NET 10.0.401、PostgreSQL、React、TypeScript、现有 Playwright/Chromium。

**Spec:** docs/superpowers/specs/2026-11-11-project-resource-counts-design.md

## Global Constraints

- 固定产品提交 1ead23c6f8f2778f1547264533622a759962b729；测试工具另记文件摘要与提交。
- 保留历史 requirements-closeout.json 原字节及 36/40/18/360 状态；新验收结果另建逐 ID 台账。
- 有权空集合显示 0；无读取范围返回 null、页面显示 —；共享应用不计入项目应用数。
- 只使用本轮独立工作区、带 owner 标记的新容器及新卷；保留旧环境和所有失败日志。
- 浏览器截图 1440px；不记录密码、Cookie、令牌或完整秘密。测试环境通过不代表生产验收。
- 合并前本机全量与 GitHub console、contracts-domain、contracts-gateway、contracts-integration 四项全部通过；跟进 main 复跑。

## Review Focus

- 0 和无权限的 null 不得混淆。
- 撤销权限须对已有会话生效，不依赖重登录。
- 仅环境 Scope 不得泄露全项目 API / 应用数或其他项目。
- 多版本、重叠 Scope 不重复计数；停用资源按已确认口径计入。
- 截图必须来自已核验静态文件与真实后端；夹具和失败信息不能升级为产品或生产验收。

### Task 1: 固定基线与隔离

**Files:** .runtime/product-acceptance-20261121；docs/evidence/product-acceptance/2026-11-21-project-counts。

- [x] 核对远端 main 并在隔离分支固定 1ead23c。
- [x] 保存原工作区 4552 个跟踪文件摘要及容器 / 卷 / 网络身份。
- [x] 运行现有 ProjectResourceCountsTests 与控制台完整测试和构建；保留每次尝试。

### Task 2: 真实页面与权限场景

**Files:** tools/WebApi.ProjectCounts.BrowserHost/Program.cs、tools/WebApi.ProjectCounts.BrowserHost/WebApi.ProjectCounts.BrowserHost.csproj、scripts/acceptance/project-counts-browser.mjs。

**Interfaces:** 夹具输出私有 browser-private.json（只用于测试登录）与不含秘密的 context.json；浏览器驱动读取私有输入并输出逐场景结果、接口投影和截图摘要。

- [x] 新增夹具：多资源/空项目/停用资源/多版本/共享应用/重叠 Scope、仅项目与仅环境范围、三种缺读取权限、已有会话撤权与 project.read 拒绝。
- [x] 构建并启动固定产品源的真实 HTTP 控制面和控制台；所有资源为本轮独有。
- [x] 执行 1440px 浏览器场景，核对三列文本、接口字段和读取权限；逐张检查截图。
- [x] 停止本轮服务，保留测试数据库与所有日志，核对原工作区与原资源身份。

### Task 3: 逐项留证与交付

**Files:** docs/deployment/requirements-acceptance-current.json、docs/deployment/requirements-current-review.md、docs/deployment/current-project-status.md、docs/evidence/product-acceptance/2026-11-21-project-counts。

- [x] 复制 454 个原 ID 建立当前验收层；只更新本轮真正验证的项目，保留历史原文、状态及证据。
- [x] 记录每项产品提交、工具提交/摘要、场景、结果、限制及缺证。
- [ ] 完成独立审查、本机全量、PR 四项检查，再合并并核对 main 复跑。

## 后续顺序

本批完成后依次推进 API 授权矩阵（环境范围、批量操作、撤权、影响预览及生产风险确认）、配置快照差异、节点 LKG 与运维操作、角色治理、API 目录、后端集群与实例。每组先核对现有接口与原需求，再独立实现与验收。强制环境凭证切换、MonitorOnly 日/月监控配额及专用安全告警保持待确认。

## 执行记录

- 本轮按用户明确要求在当前会话执行，首批沿用已确认设计，无新增产品语义。
- 首轮控制台使用 ChatGPT 应用内 Node，Rollup 原生模块被 macOS Team ID 校验拒绝。保留日志；已用现有标准 Node 验证 Vite 可以加载，另起具名复跑。

- Ruling: 缺 project.read 但有 environment.read 时，项目可通过环境可见性继承出现；单独记录该既有规则，并新增两种治理读取权均缺失时的空集合/404 场景。
- Task 1: complete — 固定基线、10项HTTP回归及339项控制台测试/构建通过；首轮运行器失败另存。
- Task 2: 浏览器13个观察通过，13张截图已逐张检查；工具复现与最终资源保全检查进行中。

- Task 2: complete — 首轮13观察/13张视觉检查通过；最终固定输入复现13观察通过，1080份复制输入摘要一致；全部原资源身份与4552份原跟踪文件保留。
- Final review: 独立只读审查发现4项Important工具问题，均已修正；5项防护回归先失败后通过，真实Chromium填写密码超时的公开日志无秘密；原四类本机门槛5204通过。两项非阻断细节记录于 review-fixes.json，未进行第二次独立复审。
- Task 3: 逐项台账与本机证据完成，PR四项检查及合并后main复跑待执行。
