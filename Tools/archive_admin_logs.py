#!/usr/bin/env python3
"""Monolith-DS admin-log archiver.

Archives admin_log / admin_log_player rows for rounds older than the most recent
KEEP_ROUNDS rounds into a compressed SQLite archive, verifies the copy, then
deletes the archived rows from the live preferences.db in small batches (short
transactions, safe alongside the running server in WAL mode). Runs VACUUM only
when no players are connected and enough free space is available, so the
6-7 GB admin-log growth is reclaimed instead of just unlinked pages.

Usage:
  archive_admin_logs.py [--dry-run] [--keep-rounds N] [--skip-vacuum]
"""

import argparse
import gzip
import json
import os
import shutil
import sqlite3
import sys
import time
import urllib.request
from datetime import datetime, timezone

LIVE_DB = "/opt/monolith-ds/data/preferences.db"
ARCHIVE_DIR = "/opt/monolith-ds/admin-log-archive"
LOCK_FILE = os.path.join(ARCHIVE_DIR, ".archive.lock")
STATUS_URL = "http://127.0.0.1:1212/status"
BATCH = 100_000
MIN_FREE_GB = 12


def log(msg):
    print("%s %s" % (datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), msg), flush=True)


def free_gb(path=LIVE_DB):
    st = os.statvfs(path)
    return st.f_bavail * st.f_frsize / (1024 ** 3)


def players_connected():
    try:
        with urllib.request.urlopen(STATUS_URL, timeout=10) as r:
            data = json.load(r)
        return int(data.get("players", -1))
    except Exception as exc:
        log("players check failed (%s); assuming players present" % exc)
        return 1


def acquire_lock():
    try:
        fd = os.open(LOCK_FILE, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        try:
            with open(LOCK_FILE) as f:
                pid = int(f.read().strip())
            os.kill(pid, 0)
            log("another archive run is active (pid %d); exiting" % pid)
            sys.exit(0)
        except (ValueError, OSError):
            log("stale lock removed")
            os.unlink(LOCK_FILE)
            return acquire_lock()
    with os.fdopen(fd, "w") as f:
        f.write(str(os.getpid()))
    return True


def release_lock():
    try:
        os.unlink(LOCK_FILE)
    except FileNotFoundError:
        pass


def table_ddl(cur, name):
    cur.execute(
        "SELECT sql FROM sqlite_master WHERE type='table' AND tbl_name=? AND sql IS NOT NULL",
        (name,),
    )
    return [r[0] for r in cur.fetchall()]


def index_ddl(cur, name):
    cur.execute(
        "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=? AND sql IS NOT NULL",
        (name,),
    )
    return [r[0] for r in cur.fetchall()]


def copy_rows(src_cur, dst_cur, table, where, params):
    """Stream rows matching WHERE into the destination, chunked."""
    total = 0
    src_cur.execute("SELECT * FROM %s WHERE %s" % (table, where), params)
    while True:
        chunk = src_cur.fetchmany(BATCH)
        if not chunk:
            break
        qmarks = ",".join("?" * len(chunk[0]))
        dst_cur.executemany(
            "INSERT INTO %s VALUES (%s)" % (table, qmarks), chunk
        )
        total += len(chunk)
    return total


def maybe_vacuum(con, args):
    """Reclaim free pages when the live DB has lots of them and it is safe."""
    if args.skip_vacuum:
        log("VACUUM skipped (--skip-vacuum)")
        return
    cur = con.cursor()
    cur.execute("PRAGMA page_count")
    page_count = cur.fetchone()[0]
    cur.execute("PRAGMA freelist_count")
    freelist = cur.fetchone()[0]
    if page_count <= 0 or freelist < page_count * 0.2:
        log("VACUUM not needed (freelist %.1f%% of %d pages)" % (100.0 * freelist / page_count if page_count else 0, page_count))
        return
    players = players_connected()
    free = free_gb()
    if players != 0:
        log("VACUUM deferred: %d player(s) connected" % players)
        return
    if free <= MIN_FREE_GB:
        log("VACUUM deferred: only %.1f GB free" % free)
        return
    log(
        "VACUUM: %d free pages of %d (%.1f%%); no players, %.1f GB free; DB write lock for a few minutes"
        % (freelist, page_count, 100.0 * freelist / page_count, free)
    )
    con.execute("VACUUM")
    con.execute("PRAGMA optimize")
    log("VACUUM done")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--keep-rounds", type=int, default=5)
    ap.add_argument("--skip-vacuum", action="store_true")
    args = ap.parse_args()

    keep = max(args.keep_rounds, 1)
    os.makedirs(ARCHIVE_DIR, exist_ok=True)
    acquire_lock()
    try:
        con = sqlite3.connect(LIVE_DB, timeout=60, isolation_level=None)
        con.execute("PRAGMA busy_timeout=60000")
        con.execute("PRAGMA wal_autocheckpoint=0")
        cur = con.cursor()

        cur.execute("SELECT MAX(round_id), MIN(round_id), COUNT(*) FROM admin_log")
        max_round, min_round, total = cur.fetchone()
        if not max_round:
            log("no admin logs; nothing to do")
            return
        cutoff = max_round - keep + 1
        if cutoff <= min_round:
            log("only %d rounds present; keeping all (cutoff would be %d)" % (max_round - min_round + 1, cutoff))
            maybe_vacuum(con, args)
            return

        cur.execute("SELECT COUNT(*) FROM admin_log WHERE round_id < ?", (cutoff,))
        to_archive_log = cur.fetchone()[0]
        cur.execute("SELECT COUNT(*) FROM admin_log_player WHERE round_id < ?", (cutoff,))
        to_archive_plr = cur.fetchone()[0]

        db_size_before = os.path.getsize(LIVE_DB) / (1024 ** 3)
        log(
            "rounds %d..%d kept, archiving rounds < %d: %d admin_log rows, %d admin_log_player rows; DB %.2f GB; free %.1f GB"
            % (cutoff, max_round, cutoff, to_archive_log, to_archive_plr, db_size_before, free_gb())
        )
        if to_archive_log == 0 and to_archive_plr == 0:
            log("nothing to archive")
            maybe_vacuum(con, args)
            return
        if args.dry_run:
            log("dry-run: nothing changed")
            return

        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        raw_archive = os.path.join(ARCHIVE_DIR, "admin-log-before-round-%d-%s.sqlite" % (cutoff, stamp))
        gz_archive = raw_archive + ".gz"
        tmp_archive = raw_archive + ".tmp"

        dst = sqlite3.connect(tmp_archive)
        dst_cur = dst.cursor()
        for ddl in table_ddl(cur, "admin_log") + table_ddl(cur, "admin_log_player") + table_ddl(cur, "round"):
            dst_cur.execute(ddl)

        log("copying admin_log...")
        copied_log = copy_rows(cur, dst_cur, "admin_log", "round_id < ?", (cutoff,))
        log("copying admin_log_player...")
        copied_plr = copy_rows(cur, dst_cur, "admin_log_player", "round_id < ?", (cutoff,))
        log("copying round metadata...")
        copied_rounds = copy_rows(cur, dst_cur, "round", "round_id < ?", (cutoff,))
        for idx in index_ddl(cur, "admin_log"):
            dst_cur.execute(idx)
        for idx in index_ddl(cur, "admin_log_player"):
            dst_cur.execute(idx)
        dst.commit()

        dst_cur.execute("SELECT COUNT(*) FROM admin_log")
        archived_log = dst_cur.fetchone()[0]
        dst_cur.execute("SELECT COUNT(*) FROM admin_log_player")
        archived_plr = dst_cur.fetchone()[0]
        dst_cur.execute("SELECT COUNT(*) FROM round")
        archived_rnd = dst_cur.fetchone()[0]
        dst.close()

        if archived_log != to_archive_log or archived_plr != to_archive_plr:
            os.unlink(tmp_archive)
            log(
                "VERIFICATION FAILED (src %d/%d vs dst %d/%d); aborted, nothing deleted"
                % (to_archive_log, to_archive_plr, archived_log, archived_plr)
            )
            sys.exit(1)

        log("archive verified (%d/%d rows, %d rounds); deleting from live DB..." % (archived_log, archived_plr, archived_rnd))
        while True:
            cur.execute(
                "DELETE FROM admin_log WHERE round_id < ? AND admin_log_id IN "
                "(SELECT admin_log_id FROM admin_log WHERE round_id < ? LIMIT ?)",
                (cutoff, cutoff, BATCH),
            )
            deleted = cur.rowcount
            if deleted == 0:
                break
        while True:
            cur.execute(
                "DELETE FROM admin_log_player WHERE round_id < ? AND log_id IN "
                "(SELECT log_id FROM admin_log_player WHERE round_id < ? LIMIT ?)",
                (cutoff, cutoff, BATCH),
            )
            deleted = cur.rowcount
            if deleted == 0:
                break
        con.execute("PRAGMA wal_checkpoint(TRUNCATE)")

        cur.execute("SELECT COUNT(*) FROM admin_log")
        remaining = cur.fetchone()[0]
        log("live admin_log rows remaining: %d" % remaining)

        maybe_vacuum(con, args)

        with open(gz_archive, "wb") as f_out, gzip.GzipFile(fileobj=f_out, mode="wb") as gz_out:
            with open(tmp_archive, "rb") as f_in:
                shutil.copyfileobj(f_in, gz_out, 1024 * 1024)
        os.unlink(tmp_archive)

        db_size_after = os.path.getsize(LIVE_DB) / (1024 ** 3)
        log(
            "DONE: %s (%d rounds, %d log rows) archived; live DB %.2f -> %.2f GB; free %.1f GB"
            % (gz_archive, archived_rnd, archived_log + archived_plr, db_size_before, db_size_after, free_gb())
        )
    finally:
        release_lock()


if __name__ == "__main__":
    main()
