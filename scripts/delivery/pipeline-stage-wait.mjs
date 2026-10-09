import {until} from '../runtime/acceptance-scenario.mjs';
export async function waitPipelineReadyForEvidence(readStage){
 return until(readStage,stage=>stage.actualReleaseStatus==='Succeeded'&&stage.runStatus==='Active'&&stage.status==='AwaitingVerification'&&
  (stage.profile?.isProduction===true||stage.eligibility?.canMaterialize===true),90);
}
