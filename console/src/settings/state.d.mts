import type {SettingsEditorState,SettingsGroupDto,SettingsInputValues} from './contracts';
export function createSettingsState():SettingsEditorState;
export function toSettingsInput(response:SettingsGroupDto):SettingsInputValues;
export function applySettingsResponse(state:SettingsEditorState,response:SettingsGroupDto,requestEpoch:number):SettingsEditorState;
export function invalidateSettingsState(state:SettingsEditorState):SettingsEditorState;
export function applySettingsFailure(state:SettingsEditorState,status:number):SettingsEditorState;
export function settingsSemanticKey(group:string,values:SettingsInputValues,etag:string):string;
export function settingsTestMessage(result:{testKind:string}):string;
export function routeDefaultInput(input:{timeoutMs:number;touched:boolean},defaults:{timeoutMs:number}):number;
export function settingsCommandValues(group:string,values:SettingsInputValues):SettingsInputValues;
export function settingsShellAuthorityKey(user:{id:string;permissions:string[];scopes:unknown[]},path:string):string;

export function settingsGroupFromSearch(search:string):string;
