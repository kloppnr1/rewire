#!/usr/bin/env node
// PreToolUse hook on Bash|PowerShell: BLOCKS git commit unless the rewire
// solution builds and the impacted tests pass.
//
// Copied from Orchestration/.claude/hooks/verify-before-commit.js
// and adapted to this one monorepo:
//   - only runs in a repo whose root holds rewire.slnx
//   - builds rewire.slnx (300s timeout)
//   - a Directory.Build.props/.targets inside a folder marks every project
//     under that folder as changed; a root-level build file, a solution file
//     or nuget.config runs everything
//   - a test project that is not in rewire.slnx (the Xellent integration
//     tests) is built by its own `dotnet test` instead of `--no-build`
//
// Impact selection: parses the <ProjectReference> graph, which now crosses
// folders, and only runs test projects whose transitive references include a
// changed project. Falls back to running everything when impact cannot be
// determined.
//
// Streams stdio so the user sees MSBuild + xUnit progress live.
// Exit 2 = block the tool call.

const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const SOLUTION = 'rewire.slnx';

const FRONTEND_SCRIPTS = ['lint', 'test:run', 'build'];
const FRONTEND_FILE_RE = /\.(ts|tsx|js|jsx|mjs|cjs|json|css|scss|html)$/i;

// `.claude` is skipped for one reason that matters: agent worktrees live in `.claude/worktrees`,
// and each one is a full checkout of the repo. Without this, the gate discovers every worktree's
// test projects and frontend packages as if they belonged to the commit, then fails on them — a
// fresh worktree has no `node_modules`, so `npm run lint` dies on a missing eslint and blocks a
// commit that has nothing to do with it. That made parallel agents and committing mutually
// exclusive. Nothing under `.claude` is code this gate should be verifying anyway.
const SKIP_DIRS = new Set([
  'node_modules', 'bin', 'obj', '.git', '.vs', '.idea', 'dist', 'out', '.claude',
]);

function normalizeToWindowsPath(p) {
  return p.replace(/^\/([a-zA-Z])\//, (_, l) => `${l.toUpperCase()}:/`);
}

function findRepoRoot(cmd, cwd) {
  const gitCMatch = cmd.match(/git\s+-C\s+["']?([^"'\s]+)["']?\s+.*commit/);
  if (gitCMatch) {
    const dir = normalizeToWindowsPath(gitCMatch[1].replace(/\\/g, '/'));
    try {
      return execSync('git rev-parse --show-toplevel', {
        cwd: dir, stdio: 'pipe', timeout: 5000,
      }).toString().trim().replace(/\\/g, '/');
    } catch { /* fall through */ }
  }

  // input.cwd is the session's directory. Without it a plain git commit in another
  // repo resolves to the session repo and verifies the wrong thing.
  try {
    return execSync('git rev-parse --show-toplevel', {
      cwd: cwd || process.cwd(), stdio: 'pipe', timeout: 5000,
    }).toString().trim().replace(/\\/g, '/');
  } catch {
    return null;
  }
}

function identifyRepo(repoRoot) {
  if (!repoRoot) return null;
  if (!fs.existsSync(path.join(repoRoot, SOLUTION))) return null;
  return 'rewire';
}

// Absolute, forward-slash, lowercased paths of the projects listed in rewire.slnx.
function solutionProjects(repoRoot) {
  const set = new Set();
  try {
    const xml = fs.readFileSync(path.join(repoRoot, SOLUTION), 'utf8');
    const re = /<Project\s+[^>]*Path\s*=\s*"([^"]+\.csproj)"/gi;
    let m;
    while ((m = re.exec(xml)) !== null) {
      set.add(path.resolve(repoRoot, m[1]).replace(/\\/g, '/').toLowerCase());
    }
  } catch { /* empty set: every test project builds itself */ }
  return set;
}

function discoverProjects(repoRoot) {
  const all = [];
  const tests = { unit: [], integration: [] };
  const frontends = []; // [{ dir, scripts: Set<string> }]

  function walk(dir) {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) {
        if (SKIP_DIRS.has(e.name)) continue;
        walk(full);
      } else if (e.isFile() && e.name.endsWith('.csproj')) {
        const norm = full.replace(/\\/g, '/');
        all.push(norm);
        if (e.name.endsWith('IntegrationTests.csproj')) {
          tests.integration.push(norm);
        } else if (e.name.endsWith('Tests.csproj')) {
          tests.unit.push(norm);
        }
      } else if (e.isFile() && e.name === 'package.json') {
        try {
          const pkg = JSON.parse(fs.readFileSync(full, 'utf8'));
          const scripts = new Set(Object.keys(pkg.scripts || {}));
          frontends.push({
            dir: path.dirname(full).replace(/\\/g, '/'),
            scripts,
          });
        } catch { /* ignore malformed package.json */ }
      }
    }
  }

  walk(repoRoot);
  return { all, tests, frontends };
}

function parseProjectReferences(csprojPath) {
  let xml;
  try {
    xml = fs.readFileSync(csprojPath, 'utf8');
  } catch {
    return [];
  }
  const refs = [];
  const re = /<ProjectReference\s+[^>]*Include\s*=\s*"([^"]+\.csproj)"/gi;
  let m;
  while ((m = re.exec(xml)) !== null) {
    const includeRaw = m[1].replace(/\\/g, '/');
    const resolved = path.resolve(path.dirname(csprojPath), includeRaw)
      .replace(/\\/g, '/');
    refs.push(resolved);
  }
  return refs;
}

// transitiveRefs(proj) = set of all .csproj this project depends on (incl. self).
function buildTransitiveRefs(allProjects) {
  const direct = new Map();
  for (const p of allProjects) direct.set(p, parseProjectReferences(p));

  const cache = new Map();
  function walk(proj, seen) {
    if (cache.has(proj)) return cache.get(proj);
    if (seen.has(proj)) return new Set();
    seen.add(proj);
    const result = new Set([proj]);
    for (const ref of direct.get(proj) || []) {
      result.add(ref);
      for (const t of walk(ref, seen)) result.add(t);
    }
    cache.set(proj, result);
    return result;
  }
  for (const p of allProjects) walk(p, new Set());
  return cache;
}

// Walk up from a file path until a directory containing a .csproj is found.
function findOwningProject(filePath, allProjects) {
  const norm = filePath.replace(/\\/g, '/');
  // direct match if the changed path itself is a csproj
  for (const proj of allProjects) {
    if (proj.toLowerCase().endsWith('/' + norm.toLowerCase())
        || proj.toLowerCase() === norm.toLowerCase()) {
      return proj;
    }
  }
  // longest-prefix match: project whose directory is an ancestor of the file
  let best = null;
  let bestLen = -1;
  for (const proj of allProjects) {
    const dir = path.dirname(proj).toLowerCase() + '/';
    const candidate = norm.toLowerCase();
    if (candidate.startsWith(dir) && dir.length > bestLen) {
      best = proj;
      bestLen = dir.length;
    }
  }
  return best;
}

function selectAffectedTests(repoRoot, touchedFiles, projects, transitive) {
  // Map each touched file to its owning project (absolute path, lowercased compare).
  const changedProjects = new Set();
  let unmappedCodeFile = false;

  for (const rawFile of touchedFiles.split('\n')) {
    const f = rawFile.trim();
    if (!f) continue;
    if (/\.(sln|slnx)$/i.test(f) || /(^|\/)nuget\.config$/i.test(f)) {
      unmappedCodeFile = true;
      continue;
    }
    if (/\.(props|targets)$/i.test(f)) {
      // A folder's build file applies to every project below it; a root one to all.
      const dir = path.dirname(path.resolve(repoRoot, f)).replace(/\\/g, '/').toLowerCase() + '/';
      const rootDir = path.resolve(repoRoot).replace(/\\/g, '/').toLowerCase() + '/';
      if (dir === rootDir) { unmappedCodeFile = true; continue; }
      const under = projects.all.filter(p => p.toLowerCase().startsWith(dir));
      if (under.length === 0) { unmappedCodeFile = true; continue; }
      for (const p of under) changedProjects.add(p);
      continue;
    }
    if (!/\.(cs|csproj)$/i.test(f)) continue;
    const abs = path.resolve(repoRoot, f).replace(/\\/g, '/');
    const owner = findOwningProject(abs, projects.all);
    if (owner) {
      changedProjects.add(owner);
    } else {
      unmappedCodeFile = true;
    }
  }

  // Fallback: if any code file couldn't be mapped, run everything (safe default).
  if (unmappedCodeFile || changedProjects.size === 0) {
    return { all: true, projects: [...projects.tests.unit, ...projects.tests.integration] };
  }

  const affected = (list) => list.filter(t => {
    const refs = transitive.get(t) || new Set([t]);
    for (const cp of changedProjects) {
      if (refs.has(cp)) return true;
    }
    return false;
  });

  return {
    all: false,
    unit: affected(projects.tests.unit),
    integration: affected(projects.tests.integration),
  };
}

// Returns true when the commit must be blocked (caller exits 2).
function run(input) {
  try {
    const cmd = input?.tool_input?.command || '';

    if (!/\bgit\b.*\bcommit\b/.test(cmd)) return false;

    const repoRoot = findRepoRoot(cmd, input.cwd);
    const repoName = identifyRepo(repoRoot);

    if (!repoRoot) {
      console.error('[verify-before-commit] SKIPPED: could not detect repo root from command.');
      return false;
    }
    if (!repoName) {
      console.error(`[verify-before-commit] SKIPPED: repo at ${repoRoot} has no ${SOLUTION}.`);
      return false;
    }

    // For `-a`/`--all` the index isn't updated until after PreToolUse, so look at
    // working tree + index vs HEAD. For `--amend` without -a we still want to
    // compare against HEAD~1 to capture the full commit being rewritten.
    const hasAllFlag = /(^|\s)(-a\b|--all\b|-[A-Za-z]*a[A-Za-z]*\b)/.test(cmd);
    const hasAmendFlag = /(^|\s)--amend\b/.test(cmd);
    let diffCmd;
    if (hasAmendFlag) {
      diffCmd = hasAllFlag
        ? 'git diff HEAD~1 --name-only'
        : 'git diff HEAD~1 --cached --name-only';
    } else {
      diffCmd = hasAllFlag
        ? 'git diff HEAD --name-only'
        : 'git diff --cached --name-only';
    }
    let touchedFiles = '';
    try {
      touchedFiles = execSync(diffCmd, {
        cwd: repoRoot, stdio: 'pipe', timeout: 5000,
      }).toString();
    } catch {
      // If we can't read the diff, fall through and run full verification.
    }
    if (touchedFiles) {
      const hasCodeChange = touchedFiles
        .split('\n')
        .some(f => {
          const t = f.trim();
          return /\.(cs|csproj|sln|slnx|props|targets)$/i.test(t)
            || /(^|\/)nuget\.config$/i.test(t)
            || FRONTEND_FILE_RE.test(t);
        });
      if (!hasCodeChange) {
        console.error(`[verify-before-commit] ${repoName}: SKIPPED — no code/frontend files in diff (${diffCmd}).`);
        return false;
      }
    }

    const projects = discoverProjects(repoRoot);
    const totalTests = projects.tests.unit.length + projects.tests.integration.length;
    const transitive = buildTransitiveRefs(projects.all);

    let unitToRun, integrationToRun, label;
    if (touchedFiles) {
      const sel = selectAffectedTests(repoRoot, touchedFiles, projects, transitive);
      if (sel.all) {
        unitToRun = projects.tests.unit;
        integrationToRun = projects.tests.integration;
        label = `build + ${totalTests}/${totalTests} test project(s) (full — unmapped change)`;
      } else {
        unitToRun = sel.unit;
        integrationToRun = sel.integration;
        const affectedCount = unitToRun.length + integrationToRun.length;
        label = `build + ${affectedCount}/${totalTests} affected test project(s)`;
      }
    } else {
      unitToRun = projects.tests.unit;
      integrationToRun = projects.tests.integration;
      label = `build + ${totalTests}/${totalTests} test project(s) (full — no diff)`;
    }

    // Default: unit tests only. Integration tests are opt-in via env var to
    // keep the commit-time gate fast. User runs them manually (or sets
    // RUN_INTEGRATION=1) when integration coverage is required for the change.
    const runIntegration = process.env.RUN_INTEGRATION === '1';
    if (!runIntegration && integrationToRun.length > 0) {
      label += ` — integration tests skipped (${integrationToRun.length}; set RUN_INTEGRATION=1 to enable)`;
      integrationToRun = [];
    }
    // Frontend selection: which package.json dirs have touched files under them?
    const touchedList = touchedFiles
      ? touchedFiles.split('\n').map(s => s.trim()).filter(Boolean)
      : [];
    const touchedAbs = touchedList.map(f =>
      path.resolve(repoRoot, f).replace(/\\/g, '/').toLowerCase());
    const affectedFrontends = projects.frontends.filter(fe => {
      const dirLc = fe.dir.toLowerCase() + '/';
      return touchedAbs.some(f => f.startsWith(dirLc) && FRONTEND_FILE_RE.test(f));
    });
    // If we have no diff (touchedFiles empty), fall back to all frontends.
    const frontendsToRun = touchedFiles ? affectedFrontends : projects.frontends;

    if (frontendsToRun.length > 0) {
      label += ` + ${frontendsToRun.length}/${projects.frontends.length} frontend package(s)`;
    }
    console.error(`[verify-before-commit] ${repoName}: ${label}...`);

    if (projects.all.length > 0) {
      try {
        execSync(`dotnet build ${SOLUTION}`, {
          cwd: repoRoot,
          stdio: ['ignore', 'inherit', 'inherit'],
          timeout: 300000,
        });
      } catch {
        console.error(`[VERIFY-COMMIT BLOCKED] dotnet build failed in ${repoName}. Fix build errors before committing.`);
        return true;
      }
    }

    const inSolution = solutionProjects(repoRoot);
    const buildFlag = (proj) => (inSolution.has(proj.toLowerCase()) ? ' --no-build' : '');

    for (const testProj of unitToRun) {
      const rel = path.relative(repoRoot, testProj);
      console.error(`[verify-before-commit]   unit: ${rel}`);
      try {
        execSync(`dotnet test "${testProj}"${buildFlag(testProj)}`, {
          cwd: repoRoot,
          stdio: ['ignore', 'inherit', 'inherit'],
          timeout: 300000,
        });
      } catch {
        console.error(`[VERIFY-COMMIT BLOCKED] Unit tests failed: ${rel}`);
        return true;
      }
    }

    for (const testProj of integrationToRun) {
      const rel = path.relative(repoRoot, testProj);
      console.error(`[verify-before-commit]   integration: ${rel}`);
      try {
        execSync(`dotnet test "${testProj}"${buildFlag(testProj)}`, {
          cwd: repoRoot,
          stdio: ['ignore', 'inherit', 'inherit'],
          timeout: 300000,
        });
      } catch {
        console.error(`[VERIFY-COMMIT BLOCKED] Integration tests failed: ${rel}`);
        return true;
      }
    }

    for (const fe of frontendsToRun) {
      const relDir = path.relative(repoRoot, fe.dir).replace(/\\/g, '/') || '.';
      for (const script of FRONTEND_SCRIPTS) {
        if (!fe.scripts.has(script)) continue;
        console.error(`[verify-before-commit]   frontend (${relDir}): npm run ${script}`);
        try {
          execSync(`npm run ${script}`, {
            cwd: fe.dir,
            stdio: ['ignore', 'inherit', 'inherit'],
            timeout: 300000,
            shell: true,
          });
        } catch {
          console.error(`[VERIFY-COMMIT BLOCKED] npm run ${script} failed in ${relDir}`);
          return true;
        }
      }
    }

    console.error(`[verify-before-commit] ${repoName}: OK`);
  } catch (e) {
    console.error(
      `[VERIFY-COMMIT HOOK ERROR] ${e && e.message ? e.message : String(e)}\n` +
      (e && e.stack ? e.stack : '')
    );
  }
  return false;
}

module.exports = { run };

// Standalone invocation. Normally this hook runs via pre-tool-guard.js, which
// bundles both PreToolUse checks into a single process — every extra process is
// another chance for Git Bash to die on startup under the local antivirus.
if (require.main === module) {
  let data = '';
  process.stdin.on('data', chunk => data += chunk);
  process.stdin.on('end', () => {
    let input;
    try {
      input = JSON.parse(data);
    } catch (e) {
      console.error(`[VERIFY-COMMIT HOOK ERROR] ${e && e.message ? e.message : String(e)}`);
      return;
    }
    if (run(input)) process.exit(2);
  });
}
