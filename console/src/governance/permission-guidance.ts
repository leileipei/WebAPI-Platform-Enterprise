type Guidance={
  impact?:string;
  condition?:string;
  operation?:string;
  alsoRequires?:string[];
};

// Dependencies describe a named server operation. Grants remain independent across roles and scopes.
export const permissionGuidance:Partial<Record<string,Guidance>>={
  'api.version.read':{operation:'读取 API 的版本列表',alsoRequires:['api.read']},
  'api.schema.read':{operation:'下载环境 OpenAPI 文档',alsoRequires:['environment.read','api.read','api.version.read']},
  'api.approve':{impact:'可接受版本比较报告中的破坏性或未知变更。',
    operation:'评审版本比较报告',alsoRequires:['api.read','api.version.read','api.schema.read']},
  'credential.manage':{impact:'可创建凭证或修改凭证的有效期。',
    operation:'安排正式轮换',alsoRequires:['credential.revoke']},
  'credential.revoke':{impact:'可撤销调用方凭证。',
    operation:'安排正式轮换',alsoRequires:['credential.manage']},
  'credential.governance.recover':{impact:'可恢复凭证治理写入流程。',condition:'执行恢复须具备平台管理员身份。'},
  'app.permission.manage':{impact:'可改变应用获准调用的 API。',
    operation:'批量修改应用授权矩阵',alsoRequires:['app.read','api.read','environment.read']},
  'pipeline.manage':{impact:'可修改流水线阶段和交付门禁。',
    operation:'维护项目流水线定义',alsoRequires:['project.write']},
  'pipeline.run':{impact:'可启动交付流水线并推进阶段。',
    operation:'创建流水线运行',alsoRequires:['release.create']},
  'release.publish':{impact:'可将获准的配置下发至目标环境。',
    operation:'执行流水线阶段的晋级发布',alsoRequires:['pipeline.run']},
  'release.rollback':{impact:'可发起回滚，发布后环境将采用目标历史配置。',
    operation:'创建回滚草稿',alsoRequires:['gateway.config.read']},
  'release.test.accept':{impact:'可通过、拒绝或撤销测试验收。',condition:'独立验收人须与申请人不同。'},
  'release.verify':{impact:'可登记生产验证结果。',condition:'验证人须与晋级申请人、实际发布执行人不同。'},
  'approval.act':{impact:'可通过或拒绝发布申请。',condition:'须符合当前审批角色与独立审批条件。'},
  'gateway.operate':{impact:'可启用或停用网关节点，影响参与下发的节点。'},
  'user.manage':{impact:'可修改用户状态和身份信息。',operation:'绑定用户的外部身份',alsoRequires:['system.sso.manage']},
  'role.manage':{impact:'可修改自定义角色的授权，影响使用该角色的用户。',condition:'授予权限须在当前范围内持有该项权限。'},
  'scope.manage':{impact:'可改变用户可访问的组织、项目或环境范围。'},
  'system.manage':{impact:'可变更平台级系统设置。'},
  'system.sso.manage':{impact:'可变更平台登录使用的 SSO 身份源。',operation:'绑定用户的外部身份',alsoRequires:['user.manage']},
  'alert.operate':{impact:'可静默或解决告警。'},
  'alert.rule.manage':{impact:'可修改或停用告警规则，影响告警触发。'}
};
