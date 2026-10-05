export function ssoUserPayload(value){return {username:value.username,displayName:value.displayName,email:value.email||null,providerId:value.providerId,subject:value.subject};}
export function ssoBindingPayload(value){return {providerId:value.providerId,subject:value.subject,enabled:!!value.enabled};}
export function mayEditSsoBinding(user,permissions){return user?.authSource==='sso'&&user.status==='Disabled'&&permissions.includes('user.manage')&&permissions.includes('system.sso.manage');}
