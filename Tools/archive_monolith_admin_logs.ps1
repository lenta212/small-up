[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int]$BeforeRoundId,
    [string]$StatusUrl = "http://188.127.225.57:1212/status",
    [string]$RemoteStatusUrl = "http://127.0.0.1:1212/status",
    [string]$SshTarget = "monolith-new",
    [string]$ServiceName = "monolith-ds.service",
    [string]$DatabasePath = "/opt/monolith-ds/data/preferences.db",
    [string]$ArchiveDir = "/opt/monolith-ds/admin-log-archive",
    [string]$BackupDir = "/opt/monolith-ds/backups",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$NativeArgs
    )

    & $FilePath @NativeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function ConvertTo-ShellSingleQuoted {
    param([string]$Value)

    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Invoke-RemoteBash {
    param([string]$Script)

    $normalizedScript = $Script -replace "`r`n", "`n" -replace "`r", "`n"
    $encoded = [Convert]::ToBase64String(([System.Text.UTF8Encoding]::new($false)).GetBytes($normalizedScript))
    Invoke-CheckedNative ssh $SshTarget "printf %s $encoded | base64 -d | bash"
}

function ConvertTo-SafeAbsoluteLinuxPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $normalized = ($Value.Trim() -replace "\\", "/").TrimEnd("/")
    if ($normalized -notmatch '^/[A-Za-z0-9._/-]+$' -or
        $normalized -eq "/" -or
        $normalized.Contains("//") -or
        @($normalized.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
        throw "$Name must be a safe non-root absolute Linux path: $Value"
    }

    return $normalized
}

function Get-ValidatedServerStatus {
    param([Parameter(Mandatory = $true)][string]$Uri)

    $response = Invoke-RestMethod -Uri $Uri -TimeoutSec 10
    $playersProperty = $response.PSObject.Properties['players']
    $roundProperty = $response.PSObject.Properties['round_id']
    $players = 0
    $roundId = 0L
    if ($null -eq $playersProperty -or
        -not [int]::TryParse([string]$playersProperty.Value, [ref]$players) -or
        $players -lt 0) {
        throw "Status response does not contain a valid non-negative players value."
    }
    if ($null -eq $roundProperty -or
        -not [long]::TryParse([string]$roundProperty.Value, [ref]$roundId) -or
        $roundId -le 0) {
        throw "Status response does not contain a valid positive round_id value."
    }

    return [pscustomobject]@{
        players = $players
        round_id = $roundId
    }
}

if ($BeforeRoundId -le 0) {
    throw "BeforeRoundId must be positive. Got $BeforeRoundId."
}

if ($SshTarget -notmatch '^[A-Za-z0-9_.@-]+$' -or $SshTarget.StartsWith('-')) {
    throw "SshTarget contains unsupported characters: $SshTarget"
}
if ($ServiceName -notmatch '^[A-Za-z0-9_.@-]+\.service$' -or $ServiceName.StartsWith('-')) {
    throw "ServiceName must be a plain systemd .service unit name: $ServiceName"
}

$DatabasePath = ConvertTo-SafeAbsoluteLinuxPath -Value $DatabasePath -Name "DatabasePath"
$ArchiveDir = ConvertTo-SafeAbsoluteLinuxPath -Value $ArchiveDir -Name "ArchiveDir"
$BackupDir = ConvertTo-SafeAbsoluteLinuxPath -Value $BackupDir -Name "BackupDir"
if ($ArchiveDir -eq $BackupDir) {
    throw "ArchiveDir and BackupDir must be different directories."
}

$statusUri = $null
if (-not [Uri]::TryCreate($StatusUrl, [UriKind]::Absolute, [ref]$statusUri) -or
    $statusUri.Scheme -notin @('http', 'https') -or
    -not [string]::IsNullOrEmpty($statusUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($statusUri.Query) -or
    -not [string]::IsNullOrEmpty($statusUri.Fragment)) {
    throw "StatusUrl must be an absolute credential-free HTTP(S) URL without query or fragment."
}
$remoteStatusUri = $null
if (-not [Uri]::TryCreate($RemoteStatusUrl, [UriKind]::Absolute, [ref]$remoteStatusUri) -or
    $remoteStatusUri.Scheme -ne 'http' -or
    $remoteStatusUri.Host -notin @('127.0.0.1', 'localhost', '::1') -or
    -not [string]::IsNullOrEmpty($remoteStatusUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($remoteStatusUri.Query) -or
    -not [string]::IsNullOrEmpty($remoteStatusUri.Fragment)) {
    throw "RemoteStatusUrl must be a credential-free loopback HTTP URL without query or fragment."
}

$status = Get-ValidatedServerStatus -Uri $StatusUrl
if ($status.players -ne 0) {
    throw "Server has $($status.players) player(s); refusing admin-log maintenance."
}
if ($status.round_id -le $BeforeRoundId) {
    throw "Current round $($status.round_id) must be newer than archive cutoff $BeforeRoundId."
}

$probe = @"
set -euo pipefail
db=$(ConvertTo-ShellSingleQuoted $DatabasePath)
test -f "`$db"
python3 - "`$db" $(ConvertTo-ShellSingleQuoted ([string]$BeforeRoundId)) <<'PY'
import json
import os
import sqlite3
import sys

path = sys.argv[1]
cutoff = int(sys.argv[2])
con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
try:
    total = con.execute("select count(*) from admin_log").fetchone()[0]
    candidates = con.execute("select count(*) from admin_log where round_id < ?", (cutoff,)).fetchone()[0]
    print(json.dumps({"database_bytes": os.path.getsize(path), "total_rows": total, "archive_rows": candidates, "keep_rows": total - candidates}))
finally:
    con.close()
PY
"@

if ($DryRun) {
    Invoke-RemoteBash $probe
    exit 0
}

Invoke-RemoteBash @"
set -euo pipefail
db=$(ConvertTo-ShellSingleQuoted $DatabasePath)
archive_dir=$(ConvertTo-ShellSingleQuoted $ArchiveDir)
backup_dir=$(ConvertTo-ShellSingleQuoted $BackupDir)
service=$(ConvertTo-ShellSingleQuoted $ServiceName)
remote_status_url=$(ConvertTo-ShellSingleQuoted $RemoteStatusUrl)
cutoff=$(ConvertTo-ShellSingleQuoted ([string]$BeforeRoundId))
stamp=`$(date -u +%Y%m%dT%H%M%SZ)
archive_base="`$archive_dir/admin-log-before-round-`$cutoff-`$stamp"
backup_base="`$backup_dir/preferences-before-admin-log-archive-`$stamp"

test -f "`$db"
sudo install -d -m 0750 -o monolith -g monolith "`$archive_dir" "`$backup_dir"

archive="`$archive_base.sqlite"
backup="`$backup_base.sqlite"
suffix=0
while sudo test -e "`$archive" || sudo test -e "`$backup"; do
  suffix=`$((suffix + 1))
  archive="`$archive_base-`$suffix.sqlite"
  backup="`$backup_base-`$suffix.sqlite"
done

players=`$(python3 - "`$remote_status_url" <<'PY'
import json
import sys
import urllib.request

with urllib.request.urlopen(sys.argv[1], timeout=10) as response:
    value = json.load(response).get("players")
if isinstance(value, bool) or not isinstance(value, int) or value < 0:
    raise SystemExit("remote status returned an invalid players value")
print(value)
PY
)
if [ "`$players" != "0" ]; then
  echo "ABORT players=`$players" >&2
  exit 51
fi

if ! sudo systemctl is-active --quiet "`$service"; then
  echo "Server service is not active before maintenance; refusing to mutate the database." >&2
  exit 52
fi

mutation_started=0
recover() {
  rc="`$1"
  trap - ERR
  echo "Admin-log maintenance failed; restoring the pre-maintenance database." >&2
  recovery_failed=0

  sudo systemctl stop "`$service" >/dev/null 2>&1 || true
  if [ "`$mutation_started" -eq 1 ] && sudo test -s "`$backup"; then
    restore_succeeded=0
    if sudo rm -f -- "`$db-wal" "`$db-shm" &&
       sudo install -o monolith -g monolith -m 0640 "`$backup" "`$db" &&
       sudo -u monolith python3 -c 'import sqlite3, sys; con = sqlite3.connect(sys.argv[1]); result = con.execute("pragma quick_check").fetchone()[0]; con.close(); raise SystemExit(0 if result == "ok" else 1)' "`$db"; then
      restore_succeeded=1
    else
      recovery_failed=1
    fi

    if [ "`$restore_succeeded" -eq 1 ]; then
      sudo rm -f -- "`$archive" "`$archive-wal" "`$archive-shm" || recovery_failed=1
    else
      echo "Database restore failed; preserving archive and backup artifacts." >&2
    fi
  fi

  sudo systemctl start "`$service" || recovery_failed=1
  healthy=0
  attempt=0
  while [ "`$attempt" -lt 45 ]; do
    if sudo systemctl is-active --quiet "`$service" &&
       curl -fsS "`$remote_status_url" >/dev/null; then
      healthy=1
      break
    fi
    attempt=`$((attempt + 1))
    sleep 2
  done
  if [ "`$healthy" -ne 1 ]; then
    recovery_failed=1
  fi

  if [ "`$recovery_failed" -ne 0 ]; then
    echo "Database/service recovery did not restore a healthy server." >&2
    exit 70
  fi

  exit "`$rc"
}
trap 'recover `$?' ERR

sudo systemctl stop "`$service"
if sudo systemctl is-active --quiet "`$service"; then
  echo "Server service remained active after stop." >&2
  false
fi

mutation_started=1
sudo -u monolith python3 - "`$db" "`$archive" "`$backup" "`$cutoff" <<'PY'
import os
import sqlite3
import sys

db_path, archive_path, backup_path, cutoff_raw = sys.argv[1:]
cutoff = int(cutoff_raw)

if os.path.exists(archive_path) or os.path.exists(backup_path):
    raise SystemExit("Refusing to overwrite an existing archive or backup")

source = sqlite3.connect(db_path, timeout=30)
try:
    if source.execute("pragma quick_check").fetchone()[0] != "ok":
        raise RuntimeError("source database quick_check failed before maintenance")

    checkpoint = source.execute("pragma wal_checkpoint(truncate)").fetchone()
    if checkpoint is None or checkpoint[0] != 0:
        raise RuntimeError(f"WAL checkpoint remained busy: {checkpoint}")

    backup = sqlite3.connect(backup_path, timeout=30)
    try:
        source.backup(backup)
        if backup.execute("pragma quick_check").fetchone()[0] != "ok":
            raise RuntimeError("backup database quick_check failed")
    finally:
        backup.close()

    before_total = source.execute("select count(*) from admin_log").fetchone()[0]
    archive_count = source.execute("select count(*) from admin_log where round_id < ?", (cutoff,)).fetchone()[0]
    if archive_count <= 0:
        raise SystemExit("No admin_log rows matched the requested cutoff")

    table_row = source.execute(
        "select sql from sqlite_master where type='table' and name='admin_log'"
    ).fetchone()
    if table_row is None or not table_row[0]:
        raise RuntimeError("admin_log table schema was not found")
    table_sql = table_row[0]
    index_sql = [
        row[0]
        for row in source.execute(
            "select sql from sqlite_master "
            "where type='index' and tbl_name='admin_log' and sql is not null "
            "order by name"
        )
    ]

    archive = sqlite3.connect(archive_path, timeout=30)
    try:
        archive.execute(table_sql)
        archive.commit()
    finally:
        archive.close()

    source.execute("attach database ? as archive_db", (archive_path,))
    try:
        source.execute(
            "insert into archive_db.admin_log "
            "select * from main.admin_log where round_id < ?",
            (cutoff,),
        )
        copied = source.execute("select count(*) from archive_db.admin_log").fetchone()[0]
        if copied != archive_count:
            raise RuntimeError(
                f"archive verification mismatch: expected {archive_count}, copied {copied}"
            )
        source.commit()
    except BaseException:
        source.rollback()
        raise
    finally:
        source.execute("detach database archive_db")

    archive = sqlite3.connect(archive_path, timeout=30)
    try:
        for statement in index_sql:
            archive.execute(statement)
        archive.execute(
            "create table archive_metadata "
            "(key text primary key not null, value text not null)"
        )
        archive.executemany(
            "insert into archive_metadata(key, value) values (?, ?)",
            [
                ("cutoff_round_exclusive", str(cutoff)),
                ("archived_rows", str(archive_count)),
                ("source_rows_before", str(before_total)),
            ],
        )
        archive.commit()
        if archive.execute("pragma quick_check").fetchone()[0] != "ok":
            raise RuntimeError("archive database quick_check failed")
    finally:
        archive.close()

    source.execute("begin immediate")
    try:
        source.execute("delete from admin_log where round_id < ?", (cutoff,))
        remaining_candidates = source.execute(
            "select count(*) from admin_log where round_id < ?", (cutoff,)
        ).fetchone()[0]
        after_total = source.execute("select count(*) from admin_log").fetchone()[0]
        if remaining_candidates != 0 or after_total != before_total - archive_count:
            raise RuntimeError(
                f"source verification mismatch: before {before_total}, "
                f"archived {archive_count}, after {after_total}, "
                f"remaining candidates {remaining_candidates}"
            )
        source.commit()
    except BaseException:
        source.rollback()
        raise

    if source.execute("pragma quick_check").fetchone()[0] != "ok":
        raise RuntimeError("source database quick_check failed after delete")

    source_vacuum = "ok"
    try:
        source.execute("vacuum")
    except sqlite3.Error as exc:
        source_vacuum = f"failed:{type(exc).__name__}"
finally:
    source.close()

archive_vacuum = "ok"
archive = sqlite3.connect(archive_path, timeout=30)
try:
    try:
        archive.execute("vacuum")
    except sqlite3.Error as exc:
        archive_vacuum = f"failed:{type(exc).__name__}"
    if archive.execute("pragma quick_check").fetchone()[0] != "ok":
        raise RuntimeError("archive database quick_check failed after vacuum")
finally:
    archive.close()

print(f"admin_log_before={before_total}")
print(f"admin_log_archived={archive_count}")
print(f"admin_log_after={after_total}")
print(f"source_vacuum={source_vacuum}")
print(f"archive_vacuum={archive_vacuum}")
print(f"database_bytes_after={os.path.getsize(db_path)}")
print(f"archive_bytes={os.path.getsize(archive_path)}")
print(f"backup_bytes={os.path.getsize(backup_path)}")
PY

sudo chown monolith:monolith "`$archive" "`$backup"
sudo chmod 0640 "`$archive" "`$backup"
sudo systemctl start "`$service"
healthy=0
attempt=0
while [ "`$attempt" -lt 45 ]; do
  if sudo systemctl is-active --quiet "`$service" &&
     curl -fsS "`$remote_status_url" >/dev/null; then
    healthy=1
    break
  fi
  attempt=`$((attempt + 1))
  sleep 2
done
if [ "`$healthy" -ne 1 ]; then
  echo "Server did not become healthy after admin-log maintenance." >&2
  false
fi

trap - ERR
echo "admin_log_archive=`$archive"
echo "admin_log_backup=`$backup"
"@

$postStatus = $null
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    try {
        $postStatus = Get-ValidatedServerStatus -Uri $StatusUrl
        break
    }
    catch {
        if ($attempt -lt 11) {
            Start-Sleep -Seconds 5
        }
    }
}
if ($null -eq $postStatus) {
    throw "Server did not return a valid public status after maintenance."
}
[pscustomobject]@{
    ok = $true
    cutoff_round = $BeforeRoundId
    service = $ServiceName
    post_round = $postStatus.round_id
    post_players = $postStatus.players
} | ConvertTo-Json -Depth 4
