import type {SaveAlertRuleRequest,AlertRuleDto,RuleTestDto,RuleScopePreviewDto} from '../api/alerts';
export type RuleEditorState={definition:SaveAlertRuleRequest;dirty:boolean;revision?:number;logicRevision?:number;conflict:boolean;latestRevision?:number;latestLogicRevision?:number;latest?:AlertRuleDto;testResult?:RuleTestDto;preview?:RuleScopePreviewDto};
export function newRuleState(definition:SaveAlertRuleRequest,rule?:AlertRuleDto):RuleEditorState;
export function ruleEditorReducer(state:RuleEditorState,event:{type:string;patch?:Partial<SaveAlertRuleRequest>;rule?:AlertRuleDto;result?:RuleTestDto|RuleScopePreviewDto}):RuleEditorState;
export function validateRule(definition:SaveAlertRuleRequest):string;
export function notificationLabel(channel:string):string;
export function ruleTestLabel(result?:RuleTestDto):string;
export type AlertSearch={page:number;all:boolean;status?:string;severity?:string};
export function parseAlertSearch(search:string):AlertSearch;
export function serializeAlertSearch(state:AlertSearch):string;

export function observationFreshLabel(state:string,value:string|null):string;
