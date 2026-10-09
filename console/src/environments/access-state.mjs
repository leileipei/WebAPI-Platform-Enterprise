export function createAccessState(identity,authorized,epoch=0){return {identity,authorized,epoch,loading:false,conflict:false};}
export function accessReducer(state,event){
 if(event.type==='context')return state.identity===event.identity&&state.authorized===event.authorized?state:createAccessState(event.identity,event.authorized,state.epoch+1);
 if(event.type==='invalidate')return createAccessState(state.identity,false,state.epoch+1);
 if(event.type==='loaded'){if(!state.authorized||event.identity!==state.identity||event.epoch!==state.epoch)return state;return {...state,detail:event.detail,draft:{...event.detail},internalUrl:event.detail.gatewayInternalUrl,loading:false,error:undefined};}
 if(event.type==='edit')return state.authorized&&state.draft?{...state,draft:{...state.draft,[event.key]:event.value}}:state;
 if(event.type==='failed'){if([401,403,404].includes(event.status))return {...createAccessState(state.identity,false,state.epoch+1),error:'资源或权限已失效，请重新打开。'};return {...state,loading:false,error:event.message,conflict:event.status===412};}
 return state;
}
export function mayCopyAddress(result,address){return !!result?.configured&&!result.error&&typeof address==='string'&&address.length>0;}
export function releaseEntrySummary(release){const context=release.accessContext;return {recorded:context?context.publicOrigin?context.publicOrigin+(context.basePath==='/'?'':context.basePath):'当时未配置':'历史未记录',current:release.currentPublicOrigin?release.currentPublicOrigin+(!release.currentBasePath||release.currentBasePath==='/'?'':release.currentBasePath):'尚未配置',changed:!!context&&release.accessAddressChanged===true};}
