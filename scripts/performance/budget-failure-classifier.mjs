const hash=/^[a-f0-9]{64}$/;
const blocked=reason=>({classification:'blocked',reason});
const same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const methodRegistered=(rule,method)=>rule.testMethods?.some(label=>label===method||rule.testCases?.some(c=>c.method===method&&label===method+'('+Object.entries(c.parameters??{}).map(([key,value])=>key+'='+value).join(',')+')'));
export function classifyBudgetFailure({failure,registry,functionalProof,costProof,sourceIdentity}={}){
 if(sourceIdentity?.role!=='baseline')return blocked('Only counterfactual baseline costs are eligible');
 if(registry?.schemaVersion!==1||registry.classificationOnlyNotSkipPolicy!==true||!Array.isArray(registry.rules)||sourceIdentity.testSourceCommit!==registry.sourceCommit)return blocked('Approved test source identity mismatch');
 if(failure?.attribution!=='unique'||!failure.caseKey||typeof failure.testFqn!=='string'||typeof failure.assertionFragment!=='string')return blocked('Failure assertion is not uniquely attributed');
 const method=failure.testFqn.split('.').at(-1),expectedClass=failure.path?.split('/').at(-1)?.replace(/\.cs$/,'');
 if(failure.testFqn!=='WebApi.Integration.Tests.'+expectedClass+'.'+method)return blocked('Fully-qualified test class does not match registered source file');
 const rules=registry.rules.filter(rule=>rule.sourceCommit===registry.sourceCommit&&rule.path===failure.path&&rule.line===failure.line&&hash.test(rule.fileSha256??'')&&rule.fileSha256===sourceIdentity.testFileSha256&&methodRegistered(rule,method)&&rule.allowedAssertionFragments?.includes(failure.assertionFragment)&&rule.testCases?.some(testCase=>testCase.method===method&&Object.entries(testCase.parameters??{}).every(([key,value])=>same(value,failure.parameters?.[key]))));
 if(rules.length!==1)return blocked('No unique approved file/line/assertion/test-case registration');
 const kind=failure.exceptionKind?.split('.').at(-1),fragment=failure.assertionFragment;
 const expected=fragment.startsWith('Assert.True(')?'TrueException':fragment.startsWith('Assert.Equal(')?'EqualException':fragment.startsWith('Assert.InRange(')?'InRangeException':fragment.startsWith('Assert.Empty(')?'EmptyException':fragment.startsWith('Assert.Single(')?'SingleException':fragment==='f.Capture.RouteColumns.Single(c => c.Length == 3)'?'InvalidOperationException':null;
 if(!expected||kind!==expected)return blocked('Functional or unsupported exception cannot be relabeled as cost');
 if(functionalProof?.complete!==true||costProof?.complete!==true||!hash.test(functionalProof.sha256??'')||!hash.test(costProof.sha256??'')||functionalProof.caseKey!==failure.caseKey||costProof.caseKey!==failure.caseKey||costProof.assertionFragment!==fragment)return blocked('Independent functional/cost proof is missing or belongs to another failure');
 return {classification:'expected-counterfactual-cost',reason:'Exact approved registration '+rules[0].id+'; original test failure remains retained'};
}
