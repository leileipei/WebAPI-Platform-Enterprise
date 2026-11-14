# 本地快照诊断模块

这组 Python 标准库模块为调用方提供阶段快照和事件说明。模块不连接 Docker、不启动进程、不提供验证执行器，也不自动生成运行授权。实际环境的授权、引擎身份、时间预算和资源清理由调用方负责。

## 离线检查

在仓库根目录执行（Linux/macOS，Python 3.9+）：

```sh
python3 -B -m unittest discover -s tests/local_diagnostics -p 'test_*.py' -v
```

检查只使用合成资源、事件及临时文件，不需要下载依赖、数据库或容器。检查通过只说明这些模块的离线行为，不代表网关缓存、完整后端套件或生产验收通过。

## 模块接口

- `private_files.write_private_text_exclusive`、`write_private_json_exclusive`：独占新建 UTF-8 文件，权限为 `0600`；拒绝覆盖已有文件及符号链接。
- `t0.safe_metadata`：将调用方已读取的资源记录投影为身份白名单。容器命令只保留哈希，环境和命令原文不写入快照。
- `t0.capture_once(state, inventory, metadata, events, healthy, diagnostics=None)`：按 A 清单、A 身份、B 清单、B 身份、C 清单及事件窗口依次核对。回调由调用方提供；函数自身没有引擎适配器。
- `t0.verify_protected(before, before_meta, current, current_meta)`：`current` 是含新增项的当前完整清单；`current_meta` 只包含 `before` 起始保护集的当前身份元数据，键集合必须与 `before` 完全一致。调用方应针对起始 ID 读取身份，或先将完整元数据投影到起始 ID；新增项由完整清单的差集报告，不授予清理权限。
- `t0_diagnostics.T0Diagnostics(directory, stream_path=None, check_budget=None)`：为到达的阶段写入私有证据，并可对调用方已留存的本地事件流做一次有界匹配。`check_budget` 应由调用方传入，在写入和读取过程中检查现有绝对截止时间。

`stream_path` 必须是调用方已留存的自有普通文件，不能传入管道、FIFO 或设备文件。读取大小有界，`check_budget` 在读写前后检查预算；阻塞 I/O 的硬中断由调用方的绝对截止机制负责。

调用方从 `scripts` 导入 `local_diagnostics` 包；测试文件包含完整的合成回调示例。

## 准入与证据边界

原保守判定保持：清单或身份变化、窗口不完整或饱和会拒绝；窗口中任意 `container`、`volume`、`network` 事件也会拒绝，包括 `exec_*` 及未知动作。事件分类只描述身份相关信号、执行活动或未知信息，`grantsAdmission` 始终为 `false`。执行活动不能据此认定为健康检查或无写入。

同一个 `state` 仅允许一次采集。失败不重新读取清单、不循环等待到稳定；调用方不得复用已消耗的运行入口。诊断写入失败也不能使采集放行。

阶段文件包含已实际到达的清单、身份白名单或事件说明及哈希。读取失败时保留前序证据和安全错误码；写入未确认时保留 `unconfirmedStageFile`。任意异常的原始消息和执行命令参数不会写入诊断收据。

诊断 `manifest.json` 永远为 `sealed=false`。`capture_once` 返回的 `sealed=true` 仅表示本次函数条件通过，实际运行入口仍须由调用方封存；两者都不是跨资源原子锁、完整事件历史、数据不变或资源清理证明。未保存的历史真实快照无法补造，旧失败及 Unknown 不能由一次新检查改标。

生成的证据仍包含调用方的资源标识和本机路径，应留在私有证据目录。仓库内只提交通用代码和合成测试，不提交实际运行授权、资源清单、事件流、口令或原始运行日志。
