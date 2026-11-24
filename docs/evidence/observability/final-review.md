# 三个专用验收入口凭证清理复核

被测提交 `1ac508063797940eb105df7dad51baad9daf6c02`，源码树 `35495625a43291ba7fde2bb8bb1c6a1391a8d865`。三个实现域互相复核了未参与编写的路径。现场登记和本机验证分别保留原始来源。

| 独立审查范围 | 复核记录 |
|---|---|
| 公共凭证清单、目录身份、HTTP 取消和 Python 回执归属 | [复核记录](../../../deliverables/specialized-cleanup-20261124-1ac5080/specialized/comparison/native-evidence/cross-review.md) |
| 版本比较的构建中断、正常及审查清理 | [复核记录](../../../deliverables/specialized-cleanup-20261124-1ac5080/specialized/settings/cross-review.md) |
| 系统设置五份 Compose 配置、旧项目兼容和实测余数 | [复核记录](../../../deliverables/specialized-cleanup-20261124-1ac5080/specialized/policies/cross-review.md) |

对三个正式入口分别运行保留审查的清理命令。版本比较 9 个、系统设置和流量策略各 14 个占位文件，在真实 Docker 资源下清到 0。系统设置和流量策略的普通业务运行也通过，版本比较在迁移阶段退出，4 份基础凭证清到 0。所有计数均在退出后独立读取。

共用后端的原生设置 domain 筛选通过 187 项，策略 domain 的无效参数实际退出 1。两次都从 1 个本地口令、2 个容器、2 个卷和 1 个网络清到 0，原始退出码保留。另以错属标签夹具复核拒绝拆除外来资源时仍清理本地凭证。

受控 CLI 用例覆盖正常返回、部分写入、运行报错、拆除失败、不可读目录、不可删除条目、归属冲突和进程中断。固定提交的 132 项专项检查均通过。

[各入口原生回执及复核索引](../../../deliverables/specialized-cleanup-20261124-1ac5080/review/native-and-residual.json)。
