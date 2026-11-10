# Contracts CI Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在本地准备三个后端 GitHub 检查，并验证它们实际消费的现有容器测试入口。

**Architecture:** 固定枚举矩阵生成独立 ARM64 runner，原 check-contracts.sh 创建随机临时数据库与测试项目，校验 owner 后精确清理。所有远程写入保持未授权状态。

**Tech Stack:** GitHub Actions、Node 24.22.1、Docker Compose、固定 .NET SDK 镜像、现有 xUnit 套件。

**Spec:** `docs/superpowers/specs/2026-11-10-contracts-ci-phase2-design.md`

## Global Constraints

- 固定输入 `536efe17ffa3db4229d152783096cdc58fe79747`；不得修改首批四文件、产品、测试、脚本、锁文件或镜像 digest。
- 三套 domain/integration/gateway；ubuntu-24.04-arm；45 分钟；fail-fast=false。
- 与首批一致的触发、只读权限和固定 Action；无部署、工件上传、故障注入或秘密接入。
- 保留本地提交，不推送、不合并、不创建 PR、不启用主干保护。

## Review Focus

- 固定镜像架构必须匹配 runner，不能在 x64 上默默依赖仿真。
- 不吞掉脚本或清理失败；某套失败不能掩盖其他矩阵结果。
- 不通过固定 Compose 项目连接原数据库或使用原密码。
- 三个新 job 与现有 console 的并发组相互独立。
- 测试结果必须分清固定镜像本机验证与尚未运行的 GitHub 检查。

### Task 1: 后端工作流与实际验证

**Files:**
- Create: `.github/workflows/contracts-ci.yml`
- Create: `docs/deployment/contracts-ci-phase2.md`

**Interfaces:**
- Consumes: `scripts/check-contracts.sh` 及 deploy/compose.test.yml、compose.contracts-test.yml。
- Produces: 三个 contracts-* 检查、逐套实际结果与清理边界记录。

- [x] Step 1: 记录工作流缺失基线及 Docker/镜像架构，不新增重复源码字符串测试。
- [x] Step 2: 写工作流，固定矩阵与环境，将枚举值经环境变量传入原脚本。
- [x] Step 3: 解析 YAML/Compose，检查权限、平台、超时、失败与引用约束。
- [x] Step 4: 各套至多一次实际固定镜像验证，记录通过/失败/跳过和清理结果；失败不改断言或重试。
- [x] Step 5: 更新验证说明，独立只读审查全部差异；只保存第二批四份文件为本地提交，核对原目录保全。
