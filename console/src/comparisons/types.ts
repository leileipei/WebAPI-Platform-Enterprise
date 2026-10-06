export type ComparisonCounts={added:number;changed:number;removed:number;compatible:number;breaking:number;unknown:number};
export type Finding={key:string;source:string;operation?:string;pointer:string;changeKind:string;risk:string;reason:string;before?:unknown;after?:unknown};
export type RiskReview={id:string;comparisonId:string;apiId:string;inputFingerprint:string;reportHash:string;engineVersion:string;decision:string;comment?:string;actorId:string;createdAt:string};
export type Report={engineVersion:string;inputFingerprint:string;coverage:string;counts:ComparisonCounts;findings:Finding[];coverageIssues:{code:string;source:string;pointer:string;reason:string}[]};
export type CompareVersion={id:string;version:string;status:string;changeType:string;revision:number};
export type ComparisonView={id:string;apiId:string;from:CompareVersion;to:CompareVersion;reportHash:string;report:Report;createdAt:string;freshness:string;latestReview?:RiskReview};
