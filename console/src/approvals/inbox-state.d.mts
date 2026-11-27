import type {ApprovalFilter,ApprovalItem} from './api';
export type ApprovalSubject=Pick<ApprovalItem,'id'|'releaseNo'|'requestedBy'|'state'>&{
 approvalEligibility?:ApprovalItem['approvalEligibility']|null;candidateHash?:string|null
}&Partial<Pick<ApprovalItem,'organization'|'project'|'environment'|'applicantDisplayName'|'risk'>>;
export type ApprovalAction='approve'|'reject';
type ApprovalOperation={releaseId:string;stepOrder:number|null;candidateHash:string|null};
export type ApprovalDialog={item:ApprovalSubject;action:ApprovalAction;comment:string;key:string;error:string;conflict:boolean};
export type ApprovalDialogEvent=
 | {type:'open';item:ApprovalSubject;action:ApprovalAction;key:string}
 | {type:'close'}
 | {type:'comment';comment:string;key:string}
 | {type:'action-error';status:number;message:string};
export function parseApprovalQuery(search?:string):ApprovalFilter;
export function approvalQuery(filter:ApprovalFilter):string;
export function approvalAuthority(user:{id:string;permissions:string[];scopes:unknown[]}|null):string;
export function approvalScope(item:ApprovalItem):{organizationId:string;projectId:string;environmentId:string};
export function approvalMayAct(item:ApprovalSubject|null|undefined):boolean;
export function approvalOperation(item:ApprovalSubject):ApprovalOperation;
export function approvalOperationMatches(opened:ApprovalOperation,detail:ApprovalSubject|null|undefined):boolean;
export function approvalDialogReducer(dialog:ApprovalDialog|null,event:ApprovalDialogEvent):ApprovalDialog|null;
export function safeApprovalReturnTo(value:unknown):string|null;
export function switchApprovalView(filter:ApprovalFilter,view:string):ApprovalFilter;
export function createInboxState(authority:string,filter:ApprovalFilter):any;
export function inboxReducer(state:any,event:any):any;

export function singleFlightInbox(load:()=>Promise<void>):(refresh?:boolean)=>Promise<void>;
