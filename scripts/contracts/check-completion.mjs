import {verifyCompletion} from './test-completion.mjs';

if (!verifyCompletion(...process.argv.slice(2))) process.exitCode = 3;
