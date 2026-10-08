const views=['PendingMine','HandledMine','AllVisible'];
const states=['Draft','WaitingApproval','Ready','Building','Publishing','Succeeded','Failed','Cancelled','Rejected','RolledBack'];
const guid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
export function parseApprovalQuery(search=''){
 const query=new URLSearchParams(search),value=key=>{const all=query.getAll(key);if(all.length>1||all.length===1&&!all[0].trim())throw Error('审批筛选参数无效。');return all[0]??null;};
 const id=key=>{const v=value(key);if(v!==null&&!guid.test(v))throw Error('审批范围参数无效。');return v;};
 const number=(key,fallback,max)=>{const v=value(key),n=v===null?fallback:Number(v);if(v!==null&&!/^\d+$/.test(v)||!Number.isSafeInteger(n)||n<1||n>max)throw Error('审批分页参数无效。');return n;};
 const view=value('view')??'PendingMine',status=value('status');if(!views.includes(view)||status!==null&&!states.includes(status))throw Error('审批筛选参数无效。');
 return {view,organizationId:id('organizationId'),projectId:id('projectId'),environmentId:id('environmentId'),status,page:number('page',1,2147483647),pageSize:number('pageSize',50,100)};
}
export function approvalQuery(filter){const query=new URLSearchParams();for(const key of ['view','organizationId','projectId','environmentId','status','page','pageSize'])if(filter[key]!==null&&filter[key]!==undefined)query.set(key,String(filter[key]));return '?'+query;}
export function approvalAuthority(user){if(!user)return 'anonymous';return JSON.stringify([user.id,[...user.permissions].sort(),user.scopes.map(g=>JSON.stringify(g)).sort()]);}
export function approvalScope(item){return {organizationId:item.organization.id,projectId:item.project.id,environmentId:item.environment.id};}
export function approvalMayAct(item){return item?.state==='WaitingApproval'&&item?.approvalEligibility?.canAct===true;}
export function safeApprovalReturnTo(value){
 if(typeof value!=='string'||!/^\/approvals(?:\?[^#\\]*)?$/.test(value))return null;
 try{return '/approvals'+approvalQuery(parseApprovalQuery(value.slice('/approvals'.length)));}catch{return null;}
}
export function switchApprovalView(filter,view){if(!views.includes(view))throw Error('审批标签无效。');return {...filter,view,status:null,page:1};}
export function createInboxState(authority,filter){return {authority,filter,epoch:0,requestId:0,rows:[],counts:null,page:null,dialog:null,error:'',loading:false,updatedAt:null};}
export function inboxReducer(state,event){
 if(event.type==='authority')return event.authority===state.authority?state:{...createInboxState(event.authority,state.filter),epoch:state.epoch+1};
 if(event.type==='filter')return {...createInboxState(state.authority,event.filter),epoch:state.epoch+1};
 if(event.type==='shell-scope')return state;
 if(['request','result','request-error'].includes(event.type)&&((event.authority!==state.authority)||(event.epoch!==state.epoch)||(event.requestId<state.requestId)))return state;
 switch(event.type){
  case 'request':return {...state,loading:true,error:'',requestId:event.requestId};
  case 'result':return {...state,loading:false,error:'',rows:event.payload.page.items,counts:event.payload.counts,page:event.payload.page,updatedAt:event.updatedAt??Date.now()};
  case 'request-error':return [401,403,404].includes(event.status)?{...state,rows:[],counts:null,page:null,dialog:null,loading:false,error:event.message}:{...state,loading:false,error:event.message};
  case 'open':return approvalMayAct(event.item)?{...state,dialog:{item:event.item,action:event.action,comment:'',key:event.key,error:'',conflict:false},error:''}:state;
  case 'close':return {...state,dialog:null};
  case 'comment':if(!state.dialog)return state;if(event.comment.length>10000)throw Error('审批意见不能超过 10000 字符。');return {...state,dialog:{...state.dialog,comment:event.comment,key:event.key??state.dialog.key,error:'',conflict:false}};
  case 'action-error':return state.dialog?{...state,dialog:{...state.dialog,error:event.message,conflict:[409,412].includes(event.status)}}:state;
  case 'lost-read':return {...state,rows:[],counts:null,page:null,dialog:null,error:event.message,loading:false,epoch:state.epoch+1};
  default:return state;
 }
}

export function approvalOperationMatches(opened,detail){return approvalMayAct(detail)&&Number.isInteger(opened?.stepOrder)&&opened.stepOrder===detail.approvalEligibility.currentStepOrder&&typeof opened.candidateHash==='string'&&/^[a-f0-9]{64}$/.test(opened.candidateHash)&&opened.candidateHash===detail.candidateHash;}
