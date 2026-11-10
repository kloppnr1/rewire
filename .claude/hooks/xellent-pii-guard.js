#!/usr/bin/env node
// PreToolUse hook on Bash|PowerShell: BLOCKS commands that would pull personal
// data out of the Xellent database (xel2012 / AXDB50). Exit 2 = block.
//
// Why: Xellent holds real customer data. Every row an agent reads goes through
// the model and into the session transcript. Agents may count, join and look
// up identifiers freely; they must not surface who a customer is. The agent
// files say so, but an instruction is advisory — this hook is not.
//
// What is blocked, once the command targets xel2012 or AXDB50:
//   - a personal column by name (NAME, ADDRESS, PHONE, EMAIL, ...)
//   - SELECT * (or alias.*) — the whole row always includes those columns
//   - sqlcmd -i <file> — the query is not in the command, so it can't be checked
// Independently: `dotnet run` in the ad-hoc sql-runner scratch project, which
// opens AXDB50 straight through SqlClient and dumps whole result sets.
//
// Deliberately no env-var override. If a name is really needed, the user runs
// the query herself outside Claude.

const TARGETS_XELLENT = /\bxel2012\b|\bAXDB50(_TEST)?\b/i;

// Column names as they exist in the Xellent entities (XellentDomain). NAME is
// on several non-person tables too (EMS_TIMESERIES, DOCUREF); that false
// positive is accepted — the message says to select an id instead.
const PERSONAL_COLUMNS = [
  'NAME', 'NAMEALIAS', 'EXU_ADDITIONALNAME', 'INSTBILLINGNAME',
  'ADDRESS', 'DELIVERYADDRESS', 'STREET', 'STREETNAME', 'ZIPCODE', 'CITY',
  'PHONE', 'CELLULARPHONE', 'TELEFAX', 'EMAIL', 'URL',
];
const PERSONAL_COLUMN_RE = new RegExp(`\\b(${PERSONAL_COLUMNS.join('|')})\\b`, 'i');
// Anything that smells like a national id, in case a custom column carries one.
const NATIONAL_ID_RE = /\b[A-Z_]*(CPR|CIVIC|SOCIALSEC)[A-Z_]*\b/i;

const SELECT_STAR_RE = /\bSELECT\b(\s+(TOP\s*\(?\s*\d+\s*\)?|DISTINCT))*\s+([A-Za-z_][A-Za-z0-9_]*\.)?\*/i;
const SQLCMD_INPUT_FILE_RE = /\bsqlcmd(\.exe)?["']?\b[^\n]*\s-i\s/i;
const SQL_RUNNER_RUN_RE = /sql-runner[^\n]*\bdotnet\s+run\b|\bdotnet\s+run\b[^\n]*sql-runner/i;

// A commit message is prose, not a query — it may well say "xel2012" and
// "NAME" while describing this very guard. When the command is a git commit,
// drop the message text (here-string, heredoc, -m "…") before checking, so an
// actual query chained onto the commit is still seen.
const GIT_COMMIT_RE = /\bgit\b[^\n|;&]*\bcommit\b/;
function withoutCommitMessage(cmd) {
  if (!GIT_COMMIT_RE.test(cmd)) return cmd;
  return cmd
    .replace(/@'[\s\S]*?\n'@/g, '')                                   // PowerShell literal here-string
    .replace(/@"[\s\S]*?\n"@/g, '')                                   // PowerShell expanding here-string
    .replace(/<<-?\s*'?"?(\w+)"?'?[^\n]*\n[\s\S]*?\n\1\b/g, '')        // bash heredoc
    .replace(/\s-m\s+("(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')/g, ' -m ""'); // -m "…"
}

function block(reason) {
  console.error(
    `[XELLENT PII GUARD] Blocked: ${reason}\n` +
    'Xellent holds real customer data and query results end up in the session transcript. ' +
    'Select identifiers (ACCOUNTNUM, CONTRACTNUM, METERINGPOINT, RECID), counts and dates — not who the customer is. ' +
    'Name the columns; no SELECT *, no -i file.'
  );
  return true;
}

// Returns true when the tool call must be blocked (caller exits 2). Hook errors
// are deliberately non-fatal: a broken guard must not block every command.
function run(input) {
  try {
    const cmd = withoutCommitMessage(input?.tool_input?.command || '');

    if (SQL_RUNNER_RUN_RE.test(cmd)) {
      return block('`dotnet run` in the sql-runner scratch project opens AXDB50 directly and dumps whole rows.');
    }

    if (!TARGETS_XELLENT.test(cmd)) return false;

    const col = cmd.match(PERSONAL_COLUMN_RE);
    if (col) return block(`personal column \`${col[1].toUpperCase()}\` against Xellent.`);

    const nat = cmd.match(NATIONAL_ID_RE);
    if (nat) return block(`national-id column \`${nat[0].toUpperCase()}\` against Xellent.`);

    if (SELECT_STAR_RE.test(cmd)) return block('`SELECT *` against Xellent returns name and address columns.');

    if (SQLCMD_INPUT_FILE_RE.test(cmd)) return block('`sqlcmd -i <file>` against Xellent — the query is not visible to the guard. Inline it with -Q.');
  } catch (e) {
    console.error(`[XELLENT-PII HOOK ERROR] ${e && e.message ? e.message : String(e)}`);
  }
  return false;
}

module.exports = { run };

// Standalone invocation. Normally this runs via pre-tool-guard.js, which bundles
// every PreToolUse check into a single process — every extra process is another
// chance for Git Bash to die on startup under the local antivirus.
if (require.main === module) {
  let data = '';
  process.stdin.on('data', chunk => (data += chunk));
  process.stdin.on('end', () => {
    let input;
    try {
      input = JSON.parse(data);
    } catch (e) {
      console.error(`[XELLENT-PII HOOK ERROR] ${e && e.message ? e.message : String(e)}`);
      return;
    }
    if (run(input)) process.exit(2);
  });
}
