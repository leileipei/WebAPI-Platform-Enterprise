import {until} from '../runtime/acceptance-scenario.mjs';
export async function waitPipelineReadyForEvidence(readStage){
 return until(readStage,stage=>stage.actualReleaseStatus==='Succeeded'&&stage.runStatus==='Active'&&stage.status==='AwaitingVerification'&&
  (stage.profile?.isProduction===true||stage.eligibility?.canMaterialize===true),90);
}
export async function waitPipelineStageOrder(readStage,order,seconds=120){
 let lastStage;
 try{return await until(async()=>{lastStage=await readStage();return lastStage;},stage=>stage.stageOrder===order,seconds);}
 catch(error){error.result={...(error.result??{}),lastStage};throw error;}
}
