import path from 'node:path';
// This validates the shape and scope of a recorded retention result. It is not
// an independent attestation of the host or its storage.
export function validRetainedEvidence(cleanup,projectName){
 if(!/^webapi-enterprise-local-test-[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(projectName??'')||
    cleanup?.retainedForEvidence!==true||cleanup.projectName!==projectName||cleanup.directoryRetained!==true||cleanup.identityVerified!==true||
    cleanup.containersRemaining!==0||!Array.isArray(cleanup.retainedVolumeNames)||
    new Set(cleanup.retainedVolumeNames).size!==cleanup.retainedVolumeNames.length||
    cleanup.volumesRemaining!==cleanup.retainedVolumeNames.length||
    !cleanup.retainedVolumeNames.includes(projectName+'_migration-evidence')||
    cleanup.retainedVolumeNames.some(name=>typeof name!=='string'||!name.startsWith(projectName+'_')||!/^[a-z0-9-]+$/.test(name.slice(projectName.length+1))))return false;
 return true;
}

export function validCloneCleanup(backup,{requiresEvidence=false}={}){
 const name=backup?.cloneProjectName,directory=backup?.cloneDirectory,cleanup=backup?.cloneCleanup;
 const guid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
 if(!guid.test(backup?.ownerId??'')||!guid.test(backup?.sourceOwnerId??'')||backup.ownerId===backup.sourceOwnerId||cleanup?.ownerId!==backup.ownerId||
    !/^webapi-enterprise-local-test-[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(name??'')||typeof directory!=='string'||!path.isAbsolute(directory)||path.basename(directory)!==name||cleanup?.directory!==directory||cleanup.projectName!==name||cleanup.ownerVerified!==true)return false;
 if(cleanup.retainedForEvidence===true)return requiresEvidence&&validRetainedEvidence(cleanup,name)&&cleanup.remainingOwnedResources===cleanup.volumesRemaining;
 return !requiresEvidence&&cleanup.directoryRetained===false&&cleanup.remainingOwnedResources===0&&cleanup.containersRemaining===0&&cleanup.volumesRemaining===0;
}
