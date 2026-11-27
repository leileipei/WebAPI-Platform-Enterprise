import {parseArgs} from 'node:util';
import {configureRestoreStorage} from './restore-quota-provider.mjs';
import {validateRestoreStorageBudget} from './restore-storage-budget.mjs';

const budgetFlags={
 maxSnapshotBytes:'max-snapshot-bytes',
 minHostFreeBytes:'min-host-free-bytes',
 maxTargetBytes:'max-target-bytes',
 minTargetFreeBytes:'min-target-free-bytes'
};
const storageOptions=Object.fromEntries(
 [...Object.values(budgetFlags),'restore-quota-socket','restore-docker-socket'].map(name=>[name,{type:'string'}])
);

export async function withRestoreCliStorage(args,run,{options={},configureStorage=configureRestoreStorage}={}){
 const {values,tokens}=parseArgs({args,tokens:true,options:{...storageOptions,...options}});
 const names=tokens.filter(token=>token.kind==='option').map(token=>token.name);
 if(new Set(names).size!==names.length)throw Error('Duplicate acceptance option.');
 const restoreBudget=validateRestoreStorageBudget(Object.fromEntries(Object.entries(budgetFlags).map(([key,flag])=>{
  const value=values[flag];
  return [key,/^[1-9][0-9]*$/.test(value??'')?Number(value):NaN];
 })));
 const quotaSocket=values['restore-quota-socket'],dockerSocket=values['restore-docker-socket'];
 if(!quotaSocket||!dockerSocket)throw Error('Restore target hard quota integration needs explicit verifier and Docker sockets.');
 const integration=await configureStorage({restoreBudget,quotaSocket,dockerSocket});
 return integration.withDocker(()=>run({restoreBudget:integration.restoreBudget,storageVerifier:integration.storageVerifier},values));
}
