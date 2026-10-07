import {useEffect,useState} from 'react';
import {ApiError} from '../api/client';
import {useSession} from '../auth/SessionProvider';
import type {SettingsEditorState,SecretReferenceMutation} from '../settings/contracts';
import {notificationApi,type NotificationChannel,type NotificationDeploymentPolicy} from './api';
import {mayTestNotification} from './settings-state.mjs';
export function NotificationSettingsPanel({state,onChange,onTest,disabled=false}:{state:SettingsEditorState;onChange:(key:string,value:any)=>void;onTest:(channel:NotificationChannel)=>void;disabled?:boolean}){
 const session=useSession(),[policy,setPolicy]=useState<NotificationDeploymentPolicy>(),[policyError,setPolicyError]=useState('');
 const authority=JSON.stringify([session.user?.id,session.user?.permissions,session.user?.scopes]),values=state.values;
 useEffect(()=>{const controller=new AbortController();setPolicy(undefined);setPolicyError('');if(session.can('system.manage'))void notificationApi.deploymentPolicy(controller.signal).then(value=>{if(!controller.signal.aborted)setPolicy(value);}).catch(error=>{if(!controller.signal.aborted){if(error instanceof ApiError&&[401,403,404].includes(error.status))setPolicy(undefined);setPolicyError((error as Error).message);}});return()=>controller.abort();},[authority]);
 if(!values)return null;
 const source=state.loaded?.revision?'已保存配置':'兼容默认';
 function reference(key:'smtpSecretRef'|'webhookSecretRef',title:string){const value=values![key] as SecretReferenceMutation;return <label className="settings-field"><span className="settings-label">{title}<span className="settings-source">{source}</span></span><select aria-label={title+' 操作'} value={value.operation} onChange={e=>onChange(key,{operation:e.target.value})}><option value="Keep">保持现有引用</option><option value="Replace">替换引用</option><option value="Clear">清除引用</option></select>{value.operation==='Replace'&&<input type="password" autoComplete="off" aria-label={title+' 替换值'} value={value.reference||''} placeholder="vault://provider/path" onChange={e=>onChange(key,{operation:'Replace',reference:e.target.value})}/>}<small>现有引用：{state.loaded?.values[key]?.hasConfiguredReference?`已配置 · ${state.loaded.values[key].provider}`:'未配置'}。Keep 保留原值；同值保存会重新固定该渠道秘密版本。</small></label>;}
 const mayTest=mayTestNotification({...state,busy:disabled,authorized:session.can('system.manage')});
 return <div className="notification-settings">
 <div className="notice">自动通知按渠道独立启用。保存与结构校验不会发送；真实测试使用已保存配置，自动开关关闭时也可明确发起。</div>
 <fieldset disabled={disabled||!!session.error||!session.can('system.manage')} className="notification-channel"><legend>Email / SMTP</legend><label className="settings-toggle"><input type="checkbox" aria-label="启用自动 Email 通知" checked={!!values.smtpEnabled} onChange={e=>onChange('smtpEnabled',e.target.checked)}/><span>自动 Email 通知 <span className={'badge '+(values.smtpEnabled?'green':'amber')}>{values.smtpEnabled?'已开启':'已关闭'}</span></span></label><div className="settings-fields">
 <label className="settings-field">SMTP Host<input value={String(values.smtpHost??'')} onChange={e=>onChange('smtpHost',e.target.value||null)}/><small>精确主机与端口须获部署白名单允许。</small></label>
 <label className="settings-field">SMTP Port<input type="number" min={1} max={65535} value={values.smtpPort===null?'':Number(values.smtpPort)} onChange={e=>onChange('smtpPort',e.target.value===''?null:Number(e.target.value))}/><small>范围 1–65535</small></label>
 <label className="settings-field">From Email<input type="email" value={String(values.fromEmail??'')} onChange={e=>onChange('fromEmail',e.target.value||null)}/><small>每项投递仅一个信封收件人，无 Cc / Bcc。</small></label>
 <label className="settings-field">SMTP TLS 模式<select value={String(values.smtpSecurity)} onChange={e=>onChange('smtpSecurity',e.target.value)}><option value="StartTlsRequired">强制 STARTTLS</option><option value="TlsOnConnect">连接即 TLS</option></select><small>证书与主机名验证必需，禁止明文回退。</small></label>
 {reference('smtpSecretRef','SMTP SecretRef')}
 </div><button type="button" className="btn" disabled={!mayTest} onClick={()=>onTest('Email')}>发送 Email 测试</button></fieldset>
 <fieldset disabled={disabled||!!session.error||!session.can('system.manage')} className="notification-channel"><legend>Webhook / HTTPS</legend><label className="settings-toggle"><input type="checkbox" aria-label="启用自动 Webhook 通知" checked={!!values.webhookEnabled} onChange={e=>onChange('webhookEnabled',e.target.checked)}/><span>自动 Webhook 通知 <span className={'badge '+(values.webhookEnabled?'green':'amber')}>{values.webhookEnabled?'已开启':'已关闭'}</span></span></label><div className="settings-fields"><label className="settings-field">Webhook URL<input type="url" value={String(values.webhookUrl??'')} onChange={e=>onChange('webhookUrl',e.target.value||null)}/><small>仅 HTTPS，精确 origin/path 白名单；禁止 query、fragment、重定向。</small></label>{reference('webhookSecretRef','Webhook SecretRef')}</div><button type="button" className="btn" disabled={!mayTest} onClick={()=>onTest('Webhook')}>发送 Webhook 测试</button></fieldset>
 {!mayTest&&<p className="notice">请先保存当前输入，并重新核对修订后再发送测试。</p>}
 <section className="notification-policy" aria-label="部署通知策略"><strong>收件与目标策略 · 部署配置</strong><p>规则最多 {policy?.maxRecipients??20} 个不同邮箱。名单由部署管理员维护；完整邮箱精确匹配，域名名单不自动包含子域名。</p>{policy&&<><p>允许邮箱：{policy.allowedRecipients.join('、')||'未配置'}</p><p>允许域名：{policy.allowedDomains.join('、')||'未配置'}</p></>}{policyError&&<p className="error" role="alert">无法读取部署名单：{policyError}</p>}<small>SMTP host:port、Webhook origin/path 及私网 CIDR 同样由部署端白名单控制；平台保存和发送均由服务端校验。</small></section>
 <div className="notice">禁用仅阻止未开始及后续自动投递。连接字段或秘密版本变化会终止旧档案任务；已开始的发送可能完成，旧消息不会改投到新地址。</div>
 </div>;
}
