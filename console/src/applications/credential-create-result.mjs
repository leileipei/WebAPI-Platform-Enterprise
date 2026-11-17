export function credentialCreateResult(response){
 const credentialId=response?.credential?.id;
 if(typeof credentialId!=='string'||!credentialId)throw new Error('凭证创建结果不完整，请保留当前输入并重试。');
 if(response.secretUnavailable===true)return {credentialId,apiKey:null,secretUnavailable:true};
 if(typeof response.apiKey!=='string'||!response.apiKey)throw new Error('凭证创建结果缺少秘密，请保留当前输入并重试。');
 return {credentialId,apiKey:response.apiKey,secretUnavailable:false};
}
