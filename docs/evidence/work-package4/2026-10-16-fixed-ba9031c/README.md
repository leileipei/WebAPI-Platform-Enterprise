# 第4包固定版本公共验收材料

产品来源ba9031c，2026-10-16，隔离owner9eb85269-863c-4615-b5c5-27e30ad9a362。此目录为脱敏交付材料，未部署4192、未推送或合并。

- acceptance/：原严格验收的53份公共材料、结果与脱敏索引；源原件及Hash不改写。
- requirements/：33项需求关闭的具名测试、实际运行观察、五页1440px材料及补充截图；其余需求保留原逐项理由。
- acceptance/public-index.json仅省略私有运行state与秘密库存，保留原proof SHA；不能单独冒充完整strict输入。严格CLI复验仍需要本机私有owner、原state和秘密库存，并重新检查真实容器。

在仓库根目录复核需求追溯：

    node scripts/account-credential/requirements.mjs docs/evidence/work-package4/requirements-closeout.json docs/evidence/work-package4

公共材料Hash由manifest.json登记。该清单是完整性清单，不是构建签名；GitHub CI、签名和迁移门禁属于第5包。SDK10项含协议fixture及真实前置原件读取，不代表10个独立实时场景；种子、TimeProvider和短时兼容队列的边界见最终报告。

具体功能与故障恢复见../../../deployment/account-credential-operations.md；当前结论见../final-summary.md。
