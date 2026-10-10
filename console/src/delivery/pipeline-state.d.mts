export type EnvironmentChoice={id:string;projectId:string;name:string;active:boolean;isProduction:boolean};
export type PipelineStageDefinition={order:number;environmentId:string;requiredTestTypes:string[];evidenceValidityMinutes:number;waitTimeoutMinutes:number;approvalFlowId:string|null};
export type PipelineDefinition={name:string;description:string;stages:PipelineStageDefinition[]};
export function validatePipelineDefinition(definition:PipelineDefinition,environments:EnvironmentChoice[]):string[];
export function pipelineAuthorityKey(actor:unknown,resourceId:string,attemptId?:string|null,profileHash?:string|null,epoch?:number):string;
export function reconcilePipelineDraft(draft:unknown,latest:unknown,status:number):any;
export function pipelineCanManage(actor:unknown,scope:unknown):boolean;
export function newPipelineStage(environmentId:string,order:number,environments:EnvironmentChoice[]):PipelineStageDefinition;

export type PipelineAction='prepare-promotion'|'mapping'|'precheck'|'submit'|'approve'|'publish'|'materialize-artifact'|'record-verification'|'request-acceptance'|'verify-production'|'pause-run'|'resume-run'|'cancel-run'|'reopen-stage';
export function pipelineStageActions(stage:any,actor:any):PipelineAction[];
export function acceptPipelineResponse(expectedKey:string,currentKey:string,response:any):boolean;
export function pipelineStatusLabel(status:string):string;
export function pipelineCountLabel(counts:any,key:string):number|string;
export function pipelineReturnTo(search:string):string|undefined;
export function pipelineTraceUrl(kind:string,id:string,returnTo?:string):string;
export function pipelineStageKey(stage:any,actor:unknown,epoch?:number):string;

export function pipelineVerificationCommand(owner:{stageId?:string;promotionId?:string;artifactId:string;contextHash?:string},evidence:any):{path:string;body:any};

export function pipelineSummaryKey(stage:any):string;
