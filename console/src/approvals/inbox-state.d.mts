import type {ApprovalFilter,ApprovalItem} from './api';
export function parseApprovalQuery(search?:string):ApprovalFilter;
export function approvalQuery(filter:ApprovalFilter):string;
export function approvalAuthority(user:{id:string;permissions:string[];scopes:unknown[]}|null):string;
export function approvalScope(item:ApprovalItem):{organizationId:string;projectId:string;environmentId:string};
export function approvalMayAct(item:any):boolean;
export function safeApprovalReturnTo(value:unknown):string|null;
export function switchApprovalView(filter:ApprovalFilter,view:string):ApprovalFilter;
export function createInboxState(authority:string,filter:ApprovalFilter):any;
export function inboxReducer(state:any,event:any):any;

export function approvalOperationMatches(opened:{stepOrder:number|null;candidateHash:string|null},detail:any):boolean;
