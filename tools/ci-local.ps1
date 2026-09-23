# The local gate: what .github/workflows/ci.yml checks, run on this machine.
#
# Every PR is merged on this output, pasted into its body. Run from the repository
# (or worktree) root on the branch to be merged. Exits 1 on any failure.
#
#   pwsh tools/ci-local.ps1 -Base origin/main
#
# Database tests read their server from the environment, never from the product's
# own configuration: EO_TEST_PG_PASSWORD (and EO_TEST_PG_HOST / _PORT / _USER /
# _DATABASE when they differ from 127.0.0.1:5432 observatory/observatory), plus
# EO_TEST_PG_ADMIN_USER / _PASSWORD for the setup tests. The gate never reads
# user-secrets: a test run should not need, or be able to reach, the production
# password.
param([string]$Base = 'origin/main')

$ErrorActionPreference = 'Continue'
$results = [ordered]@{}

# --- No orphaned test processes (23 September 2026) ---
# A testhost or vstest whose parent has exited, left by an earlier run, keeps
# a worktree's assemblies locked; the build then fails to copy and the gate
# reported "0 projects / no summary" as if the code were at fault. Counted and
# named, never killed: a live one may belong to a gate running in parallel.
$all = Get-CimInstance Win32_Process
$alive = @{}; $all | ForEach-Object { $alive[[int]$_.ProcessId] = $true }
$orphans = $all | Where-Object {
    ($_.Name -eq 'testhost.exe' -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'vstest\.console|testhost')) -and
    -not $alive.ContainsKey([int]$_.ParentProcessId)
}
$results['Orphaned test processes'] = if (-not $orphans) { 'pass' }
    else { "FAIL: $(@($orphans).Count) orphaned, close them first: " + (($orphans | ForEach-Object { "$($_.Name) $($_.ProcessId)" }) -join ', ') }

# --- Build & test (Release, all projects) + Persistence (PostgreSQL, 0 skipped) ---
$missing = @('EO_TEST_PG_PASSWORD', 'EO_TEST_PG_ADMIN_USER', 'EO_TEST_PG_ADMIN_PASSWORD') |
    Where-Object { -not [Environment]::GetEnvironmentVariable($_) }

dotnet restore -v q 2>&1 | Out-Null
$b = dotnet build --no-restore -c Release -nologo -v q 2>&1
$results['Build'] = if ($LASTEXITCODE -eq 0) { 'pass' } else { 'FAIL: ' + (($b | Select-String ' error ' | Select-Object -First 2) -join ' | ') }
if ($LASTEXITCODE -eq 0) {
    $t = dotnet test --no-build -c Release -nologo 2>&1
    $sum = $t | Select-String -Pattern 'Başarısız!|Başarılı!|Failed!|Passed!'
    $failed = $sum | Where-Object { $_.Line -match 'Başarısız!|Failed!' }
    # Which tests, not just how many: an intermittent failure you can't name
    # can't be told apart from a real one.
    $names = $t | Select-String -Pattern '^\s*(Başarısız|Failed)\s+(\S+)' | ForEach-Object { $_.Matches[0].Groups[2].Value } | Select-Object -Unique -First 5
    $results['Build & test'] = if ($failed) { 'FAIL: ' + (($failed | ForEach-Object { $_.Line.Trim() -replace '\s+',' ' }) -join ' | ') + $(if ($names) { ' || failing: ' + ($names -join ', ') } else { '' }) } else { "pass ($($sum.Count) projects)" }

    # Zero failed AND zero skipped: a database test that skipped did not run, and a
    # line that only checked skips once printed "pass" beside a failure.
    $pgLine = $sum | Where-Object { $_.Line -match 'Persistence.Postgres' } | Select-Object -First 1
    $results['Persistence (PostgreSQL)'] = if ($missing) { 'FAIL: not set: ' + ($missing -join ', ') }
        elseif (-not $pgLine) { 'FAIL: no summary' }
        elseif ($pgLine.Line -notmatch 'Başarısız:\s*0\b|Failed:\s*0\b') { 'FAIL: failing tests: ' + ($pgLine.Line -replace '\s+',' ') }
        elseif ($pgLine.Line -match 'Atlanan:\s*0\b|Skipped:\s*0\b') { 'pass (' + (($pgLine.Line -replace '\s+',' ') -replace '^.*?- ','' -replace ', Süre.*','') + ')' }
        else { 'FAIL: skipped tests: ' + ($pgLine.Line -replace '\s+',' ') }
}

# --- Web interface ---
Push-Location web
# npm ci wipes node_modules first, and on Windows a native module it is
# deleting can be briefly locked (EPERM, 23 September 2026); with its output
# swallowed the gate then reported "'tsc' is not recognized". So: install
# only when the lock file changed, retry once, and say what npm said.
$installed = 'node_modules/.package-lock.json'
$stale = -not (Test-Path $installed) -or
    (Get-Item package-lock.json).LastWriteTimeUtc -gt (Get-Item $installed).LastWriteTimeUtc
$ci = $null
if ($stale) {
    foreach ($attempt in 1..2) {
        $ci = npm ci --silent 2>&1
        if ($LASTEXITCODE -eq 0) { $ci = $null; break }
    }
}
if ($ci) {
    $results['Web interface'] = 'FAIL: npm ci: ' + (($ci | Select-String -Pattern 'npm error (code|path|syscall)|ERR' | Select-Object -First 3) -join ' | ')
}
else {
    $w = npm run build 2>&1
    $results['Web interface'] = if ($LASTEXITCODE -eq 0) { 'pass' } else { 'FAIL: ' + (($w | Select-Object -Last 3) -join ' | ') }
    if ($LASTEXITCODE -eq 0) {
        $cat = npm run catalogues:check 2>&1
        $results['Web interface'] = if ($LASTEXITCODE -eq 0) { 'pass' } else { 'FAIL: catalogues:check: ' + (($cat | Select-Object -Last 5) -join ' | ') }
    }
}
Pop-Location

# --- Design tokens ---
node web/design/validate-contrast.mjs 2>&1 | Out-Null
$ok1 = $LASTEXITCODE -eq 0
$gen = node web/design/generate-css.mjs 2>$null
$cur = Get-Content web/src/styles/tokens.css -Raw
$ok2 = (($gen -join "`n").Trim() -replace "`r","") -eq (($cur).Trim() -replace "`r","")
$results['Design tokens'] = if ($ok1 -and $ok2) { 'pass' } else { "FAIL: contrast=$ok1 cssUpToDate=$ok2" }

# --- Commit message format (the repo's own hook, retried on the MSYS sh crash) ---
$bad = @()
foreach ($sha in (git rev-list "$Base..HEAD")) {
    $tmp = [System.IO.Path]::GetTempFileName()
    git log -1 --format=%B $sha | Set-Content $tmp
    $ok = $false
    for ($i = 0; $i -lt 5; $i++) {
        $o = & 'C:\Program Files\Git\bin\bash.exe' .githooks/commit-msg ($tmp -replace '\\','/') 2>&1
        if ($LASTEXITCODE -eq 0) { $ok = $true; break }
        if (-not ($o -match 'add_item|fatal error')) { break }
        Start-Sleep 2
    }
    if (-not $ok) { $bad += $sha.Substring(0,7) }
    Remove-Item $tmp -ErrorAction SilentlyContinue
}
$results['Commit message format'] = if ($bad.Count -eq 0) { 'pass' } else { 'FAIL: ' + ($bad -join ', ') }

# --- No catalogue picked by position (P1/P2 removed it; P3a nearly brought it back as [2]) ---
# Any index, and the parameterless First()/Last()/Single()/ElementAt(): identity
# comes from CatalogueDescriptor, never from order. Single(c => ...) with a
# predicate is a lookup by identity and is NOT flagged. The widened form found a
# live crash on its first run (#123: Single() on a list of three).
# Production code only; tests register catalogues in reverse on purpose.
$byPosition = git grep -n -E 'Catalogues\s*(\[[0-9]+\]|\.(First|Last|Single)\(\s*\)|\.ElementAt\()' -- src
$results['Catalogue by position'] = if (-not $byPosition) { 'pass' } else { 'FAIL: ' + (($byPosition | Select-Object -First 3) -join ' | ') }

$head = git rev-parse --short HEAD
"LOCAL CI @ $head (base $Base, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))"
$results.GetEnumerator() | ForEach-Object { "{0} = {1}" -f $_.Key, $_.Value }
if ($results.Values | Where-Object { $_ -like 'FAIL*' }) { exit 1 } else { exit 0 }
