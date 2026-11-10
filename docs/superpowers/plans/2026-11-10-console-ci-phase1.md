# Console CI Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将现有前端测试与构建接入独立的 `console` GitHub 检查，准备可审阅的第一批提交。

**Architecture:** GitHub 托管 Ubuntu runner 安装固定 Node/pnpm，按锁文件安装后调用现有脚本。保留主目录所有已有工作，仅在独立工作目录实现。

**Tech Stack:** GitHub Actions YAML、Node 24.22.1、pnpm 11.25.0、既有 React/TypeScript/Vite。

**Spec:** `docs/superpowers/specs/2026-11-10-console-ci-phase1-design.md`

## Global Constraints

- 检查名 `console`；runner `ubuntu-24.04`；超时 15 分钟。
- PR 到 main、push 到 main、手动触发；无路径过滤、无 `pull_request_target`。
- 默认 `contents: read`；checkout 不保留凭据；Action 引用固定官方提交。
- 复用 `scripts/check-console.sh`；不改产品、依赖锁、测试、部署或历史证据。
- 不声明远程检查成功，不提前启用主干规则；推送范围在最终审查后确认。

## Review Focus

- PR 与 main 同名分支不能互相误取消：并发组包含 workflow 与 PR 号或 ref。
- 测试失败必须阻断构建/检查：使用原脚本的失败退出，不设置 continue-on-error。
- 不依赖本机绝对路径：runner 中 Node/pnpm 通过 PATH 解析。
- 公开日志不带产品秘密：不配置 secrets，不上传 .runtime 或其他原始目录。
- 本地已有 node_modules 不等于干净安装：分别报告锁定安装可复现性、本地测试和远程运行边界。

### Task 1: 前端工作流及验证说明

**Files:**
- Create: `.github/workflows/console-ci.yml`
- Create: `docs/deployment/console-ci-phase1.md`

**Interfaces:**
- Consumes: `scripts/check-console.sh`；`console/pnpm-lock.yaml`。
- Produces: 一个 GitHub job `console`，顺序执行锁定安装、原测试与构建；失败保留非零状态。

- [x] Step 1: 在工作流尚不存在时运行 YAML/约束检查，记录缺少工作流的基线；不为人类文档或配置字段添加重复的源码字符串测试。
- [x] Step 2: 新建工作流，固定 checkout/setup-node 提交、Node/pnpm，按规格触发并调用原脚本。
- [x] Step 3: 解析 YAML，检查权限、触发范围、并发隔离、固定引用、超时和无失败吞噬；分别核验上游 action 元数据。
- [x] Step 4: 独立目录执行锁定安装与完整 `check-console.sh`。若离线依赖不足则记录，不能冒充干净安装成功；使用隔离副本验证现有依赖时明确标注。
- [x] Step 5: 写验证说明，列版本、测试数、构建结果、原目录保护结果、远程运行/保护仍未执行。
- [x] Step 6: 对整个差异做独立审查，修复 Important/Critical 后复验；仅提交本批文件，保留已有主目录文档。
