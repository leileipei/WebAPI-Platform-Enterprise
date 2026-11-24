import fs from 'node:fs';
import path from 'node:path';

export function verifyCompletion(file, owner) {
  let report;
  try {
    const stat = typeof file === 'number' ? fs.fstatSync(file) : fs.statSync(file);
    if (stat.isFile() && stat.size <= 4096) report = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    // A missing or partial write cannot attest to a completed test run.
  }
  const count = value => Number.isSafeInteger(value) && value >= 0;
  if (typeof owner === 'string' && owner.length > 0 && report?.owner === owner && report.completed === true && report.failed === 0 &&
      count(report.executed) && count(report.passed) && count(report.skipped) &&
      report.passed > 0 && report.executed === report.passed + report.skipped) {
    console.log(`Verified contract test run: ${report.passed} passed, ${report.skipped} skipped, ${report.executed} total.`);
    return true;
  }
  console.error('Missing or invalid completion for this contract run. Expected a completed VSTest run with at least one passing test and no failures.');
  return false;
}

export async function withTestCompletion(directory, owner, runSdk) {
  const file = path.join(directory, 'completion.json');
  const receipt = fs.openSync(file, 'wx+', 0o600);
  let primaryError;
  try {
    await runSdk(['-e', 'WEBAPI_CONTRACT_TEST_OWNER=' + owner, '-v', file + ':/contract-completion.json']);
    if (!verifyCompletion(receipt, owner)) throw Error('Invalid VSTest completion');
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    try {
      const owned = fs.fstatSync(receipt, {bigint:true});
      const current = fs.lstatSync(file, {bigint:true});
      if (owned.dev !== current.dev || owned.ino !== current.ino) throw Error('Replaced VSTest receipt');
      fs.unlinkSync(file);
    } catch (error) {
      console.error('VSTest completion cleanup could not be verified.');
      if (!primaryError) throw error;
    } finally {
      fs.closeSync(receipt);
    }
  }
}
