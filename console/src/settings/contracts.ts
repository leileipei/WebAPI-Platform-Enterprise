export type SecretReferenceMutation={operation:'Keep'|'Replace'|'Clear';reference?:string};
export type SettingsInputValues=Record<string,number|string|boolean|string[]|SecretReferenceMutation|null>;
export type SettingsGroupDto={group:string;revision:number;values:Record<string,any>;fields:{key:string;source:string;effect:string}[]};
export type SettingsEditorState={group:string;loaded:SettingsGroupDto|null;values:SettingsInputValues|null;etag:string|null;dirty:boolean;requestEpoch:number;conflict:boolean;conflictReviewed:boolean;idempotencyKey:string|null};
export type SettingsPreview={differences:{field:string;before:string;after:string}[];impacts:string[];confirmationToken:string;expiresAt:string};
