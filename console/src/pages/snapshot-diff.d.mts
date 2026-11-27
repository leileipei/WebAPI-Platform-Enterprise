export type SnapshotView={environmentId:string;configVersion:number;schemaVersion:string;routes:{id:string}[];clusters:{id:string}[];policies:{id:string}[];applications:{id:string}[]};
export function compareSnapshots(before:SnapshotView,after:SnapshotView):{kind:string;id:string;change:string;before:unknown;after:unknown}[];
