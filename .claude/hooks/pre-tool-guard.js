#!/usr/bin/env node
// PreToolUse hook on Bash|PowerShell: runs the rewire checks in ONE process.
//
// Why bundled: Claude Code starts a fresh Git Bash for every hook command, and
// on this machine the antivirus injects into each process start. Every so often
// bash dies during its own startup before it runs anything. The hook then never
// executes and the tool call goes through, which silently opens the commit gate.
// Fewer process starts per tool call means fewer chances to hit it.
//
// Order: the cheap PII check first, so a blocked command never pays for build
// and tests. Exit 2 = block.

const { run: guardXellentPii } = require('./xellent-pii-guard.js');
const { run: verifyBeforeCommit } = require('./verify-before-commit.js');

let data = '';
process.stdin.on('data', chunk => (data += chunk));
process.stdin.on('end', () => {
  let input;
  try {
    input = JSON.parse(data);
  } catch (e) {
    // Unparseable payload: allow through rather than blocking every command.
    console.error(`[PRE-TOOL-GUARD HOOK ERROR] ${e && e.message ? e.message : String(e)}`);
    return;
  }

  for (const check of [guardXellentPii, verifyBeforeCommit]) {
    let blocked = false;
    try {
      blocked = check(input);
    } catch (e) {
      console.error(`[PRE-TOOL-GUARD HOOK ERROR] ${e && e.message ? e.message : String(e)}`);
    }
    if (blocked) process.exit(2);
  }
});
