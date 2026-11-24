import fs from 'node:fs';

const [file, owner] = process.argv.slice(2);
let report;
try {
  if (fs.statSync(file).size <= 4096) report = JSON.parse(fs.readFileSync(file, 'utf8'));
} catch {
  // A missing or partial write cannot attest to a completed test run.
}
const count = value => Number.isSafeInteger(value) && value >= 0;
if (report?.owner === owner && report.completed === true && report.failed === 0 &&
    count(report.executed) && count(report.passed) && count(report.skipped) &&
    report.passed > 0 && report.executed === report.passed + report.skipped) {
  console.log(`Verified contract test run: ${report.passed} passed, ${report.skipped} skipped, ${report.executed} total.`);
} else {
  console.error('Missing or invalid completion for this contract run. Expected a completed VSTest run with at least one passing test and no failures.');
  process.exitCode = 3;
}
