#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
import tempfile
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP
from pathlib import Path
from typing import Any


DISPLAY_FIELDS = [
    "ts",
    "requestId",
    "path",
    "provider",
    "model",
    "status",
    "fallback",
    "inputTokens",
    "outputTokens",
    "cacheCreationInputTokens",
    "cacheReadInputTokens",
    "cachedInputTokens",
    "estimatedCostRub",
]


def read_events(path: Path) -> list[dict[str, Any]]:
    events: list[dict[str, Any]] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        text = line.strip()
        if not text:
            continue
        try:
            event = json.loads(text)
        except json.JSONDecodeError as exc:
            raise ValueError(f"{path}:{line_number}: invalid JSONL: {exc}") from exc
        if not isinstance(event, dict):
            raise ValueError(f"{path}:{line_number}: JSONL entry is not an object")
        events.append(event)
    return events


def merge_audit_events(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    gateways: dict[str, dict[str, Any]] = {}
    providers: list[dict[str, Any]] = []

    for event in events:
        event_type = event.get("event")
        request_id = event.get("requestId")
        if event_type == "gateway_request" and isinstance(request_id, str):
            gateways[request_id] = event
        elif event_type == "provider_request":
            providers.append(event)

    rows: list[dict[str, Any]] = []
    for provider in providers:
        request_id = provider.get("requestId")
        gateway = gateways.get(request_id, {}) if isinstance(request_id, str) else {}
        row: dict[str, Any] = {
            "ts": provider.get("ts", ""),
            "requestId": request_id or "",
            "path": gateway.get("path", ""),
            "provider": provider.get("provider", ""),
            "model": provider.get("model", ""),
            "status": provider.get("status", ""),
            "fallback": gateway.get("fallback", ""),
            "durationMs": provider.get("durationMs", ""),
        }

        for field in [
            "inputTokens",
            "outputTokens",
            "totalTokens",
            "cacheCreationInputTokens",
            "cacheReadInputTokens",
            "cachedInputTokens",
            "estimatedInputCostRub",
            "estimatedOutputCostRub",
            "estimatedCacheCreationCostRub",
            "estimatedCacheReadCostRub",
            "estimatedCachedInputCostRub",
            "estimatedCostRub",
            "estimatedCostCurrency",
            "error",
        ]:
            if field in provider:
                row[field] = provider[field]

        rows.append(row)

    return rows


def format_table(rows: list[dict[str, Any]]) -> str:
    if not rows:
        return "No provider_request rows found."

    widths = {
        field: max(len(field), *(len(str(row.get(field, ""))) for row in rows))
        for field in DISPLAY_FIELDS
    }
    header = "  ".join(field.ljust(widths[field]) for field in DISPLAY_FIELDS)
    divider = "  ".join("-" * widths[field] for field in DISPLAY_FIELDS)
    lines = [header, divider]
    for row in rows:
        lines.append("  ".join(str(row.get(field, "")).ljust(widths[field]) for field in DISPLAY_FIELDS))
    return "\n".join(lines)


def summarize(path: Path) -> list[dict[str, Any]]:
    return merge_audit_events(read_events(path))


def parse_datetime(value: str) -> datetime:
    text = value.strip()
    if not text:
        raise ValueError("empty datetime")
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"

    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as exc:
        raise ValueError(f"expected ISO datetime, got {value!r}") from exc

    return parsed


def comparable_datetime(value: datetime, reference: datetime) -> datetime:
    if value.tzinfo is None and reference.tzinfo is not None:
        return value.replace(tzinfo=timezone.utc)
    if value.tzinfo is not None and reference.tzinfo is None:
        return value.astimezone(timezone.utc).replace(tzinfo=None)
    return value


def text_matches(row: dict[str, Any], field: str, expected: str | None) -> bool:
    if not expected:
        return True
    return expected.casefold() in str(row.get(field, "")).casefold()


def bool_matches(value: Any, expected: bool | None) -> bool:
    if expected is None:
        return True
    if isinstance(value, bool):
        return value is expected
    if isinstance(value, str):
        lowered = value.strip().casefold()
        if lowered in {"true", "1", "yes"}:
            return expected is True
        if lowered in {"false", "0", "no"}:
            return expected is False
    return False


def status_matches(value: Any, expected: str | None) -> bool:
    if not expected:
        return True
    return str(value) == expected


def filter_rows(
    rows: list[dict[str, Any]],
    *,
    since: datetime | None = None,
    until: datetime | None = None,
    request_id: str | None = None,
    model: str | None = None,
    provider: str | None = None,
    path: str | None = None,
    status: str | None = None,
    fallback: bool | None = None,
) -> list[dict[str, Any]]:
    filtered: list[dict[str, Any]] = []
    for row in rows:
        if not text_matches(row, "requestId", request_id):
            continue
        if not text_matches(row, "model", model):
            continue
        if not text_matches(row, "provider", provider):
            continue
        if not text_matches(row, "path", path):
            continue
        if not status_matches(row.get("status"), status):
            continue
        if not bool_matches(row.get("fallback"), fallback):
            continue

        if since is not None or until is not None:
            try:
                row_ts = parse_datetime(str(row.get("ts", "")))
            except ValueError:
                continue
            if since is not None and comparable_datetime(row_ts, since) < since:
                continue
            if until is not None and comparable_datetime(row_ts, until) > until:
                continue

        filtered.append(row)

    return filtered


def decimal_from_value(value: Any) -> Decimal | None:
    if value is None or value == "":
        return None
    text = str(value).replace(",", ".").replace("₽", "").strip()
    if not text:
        return None
    try:
        return Decimal(text)
    except InvalidOperation:
        return None


def rounded_float(value: Decimal) -> float:
    return float(value.quantize(Decimal("0.000001"), rounding=ROUND_HALF_UP))


def sum_decimal(rows: list[dict[str, Any]], field: str) -> Decimal:
    total = Decimal("0")
    for row in rows:
        value = decimal_from_value(row.get(field))
        if value is not None:
            total += value
    return total


def sum_int(rows: list[dict[str, Any]], field: str) -> int:
    total = 0
    for row in rows:
        value = row.get(field)
        if isinstance(value, int):
            total += value
        elif isinstance(value, float) and value.is_integer():
            total += int(value)
    return total


def build_observed_cost_summary(rows: list[dict[str, Any]], observed_cost: Decimal) -> dict[str, Any]:
    total_tokens = sum_int(rows, "totalTokens")
    if total_tokens == 0:
        total_tokens = (
            sum_int(rows, "inputTokens")
            + sum_int(rows, "outputTokens")
            + sum_int(rows, "cacheCreationInputTokens")
            + sum_int(rows, "cacheReadInputTokens")
            + sum_int(rows, "cachedInputTokens")
        )

    estimated_cost = sum_decimal(rows, "estimatedCostRub")
    summary: dict[str, Any] = {
        "observedCostRub": rounded_float(observed_cost),
        "estimatedCostRub": rounded_float(estimated_cost),
        "costDeltaRub": rounded_float(observed_cost - estimated_cost),
        "rowCount": len(rows),
        "totalTokens": total_tokens,
        "inputTokens": sum_int(rows, "inputTokens"),
        "outputTokens": sum_int(rows, "outputTokens"),
        "cacheCreationInputTokens": sum_int(rows, "cacheCreationInputTokens"),
        "cacheReadInputTokens": sum_int(rows, "cacheReadInputTokens"),
        "cachedInputTokens": sum_int(rows, "cachedInputTokens"),
    }
    if total_tokens > 0:
        summary["effectiveRubPerMillionTokens"] = rounded_float(observed_cost * Decimal("1000000") / Decimal(total_tokens))

    return summary


def format_observed_cost_summary(summary: dict[str, Any]) -> str:
    lines = [
        "",
        "Observed cost summary:",
        f"  observedCostRub: {summary.get('observedCostRub')}",
        f"  estimatedCostRub: {summary.get('estimatedCostRub')}",
        f"  costDeltaRub: {summary.get('costDeltaRub')}",
        f"  totalTokens: {summary.get('totalTokens')}",
        f"  effectiveRubPerMillionTokens: {summary.get('effectiveRubPerMillionTokens', '')}",
    ]
    return "\n".join(lines)


def run_self_test() -> None:
    request_id = "test123"
    events = [
        {
            "ts": "2026-07-03T00:00:00.000Z",
            "event": "provider_request",
            "requestId": request_id,
            "provider": "anthropic",
            "model": "claude-haiku-4-5-20251001",
            "status": 200,
            "inputTokens": 1667,
            "outputTokens": 103,
            "cacheCreationInputTokens": 0,
            "cacheReadInputTokens": 0,
            "estimatedCostRub": 0.2697,
        },
        {
            "ts": "2026-07-03T00:00:00.001Z",
            "event": "gateway_request",
            "requestId": request_id,
            "path": "/propose_event",
            "fallback": False,
        },
    ]
    with tempfile.TemporaryDirectory() as temp_dir:
        path = Path(temp_dir) / "audit.jsonl"
        path.write_text("\n".join(json.dumps(event, separators=(",", ":")) for event in events), encoding="utf-8")
        rows = summarize(path)
        filtered = filter_rows(
            rows,
            since=parse_datetime("2026-07-03T00:00:00Z"),
            until=parse_datetime("2026-07-03T00:00:01Z"),
            request_id="test",
            model="haiku-4-5",
            provider="anthropic",
            path="/propose",
            status="200",
            fallback=False,
        )
        filtered_out = filter_rows(rows, since=parse_datetime("2026-07-03T00:00:01Z"))
        observed = build_observed_cost_summary(rows, Decimal("0.25"))

    assert len(rows) == 1
    assert len(filtered) == 1
    assert filtered[0]["requestId"] == request_id
    assert len(filtered_out) == 0
    row = rows[0]
    assert row["requestId"] == request_id
    assert row["path"] == "/propose_event"
    assert row["inputTokens"] == 1667
    assert row["outputTokens"] == 103
    assert row["cacheCreationInputTokens"] == 0
    assert row["cacheReadInputTokens"] == 0
    assert row["estimatedCostRub"] == 0.2697
    assert observed["observedCostRub"] == 0.25
    assert observed["estimatedCostRub"] == 0.2697
    assert observed["costDeltaRub"] == -0.0197
    assert observed["totalTokens"] == 1770
    assert observed["effectiveRubPerMillionTokens"] == 141.242938


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize LuaM AI gateway JSONL audit logs.")
    parser.add_argument("path", nargs="?", default=r"C:\MonolithTemp\gw-audit.jsonl")
    parser.add_argument("--json", action="store_true", help="print JSON rows instead of a text table")
    parser.add_argument("--observed-cost-rub", help="observed provider cost, for example 0,25")
    parser.add_argument("--since", help="only include rows at or after this ISO timestamp")
    parser.add_argument("--until", help="only include rows at or before this ISO timestamp")
    parser.add_argument("--request-id", help="only include rows whose requestId contains this value")
    parser.add_argument("--model", help="only include rows whose model contains this value")
    parser.add_argument("--provider", help="only include rows whose provider contains this value")
    parser.add_argument("--path", dest="request_path", help="only include rows whose gateway path contains this value")
    parser.add_argument("--status", help="only include rows with this provider status, for example 200")
    parser.add_argument("--fallback", choices=["true", "false"], help="only include fallback or non-fallback gateway rows")
    parser.add_argument("--self-test", action="store_true", help="run the built-in parser self-test")
    args = parser.parse_args()

    if args.self_test:
        run_self_test()
        print(json.dumps({"ok": True}, separators=(",", ":")))
        return 0

    path = Path(args.path)
    if not path.exists():
        print(f"audit log not found: {path}", file=sys.stderr)
        return 2

    try:
        since = parse_datetime(args.since) if args.since else None
        until = parse_datetime(args.until) if args.until else None
    except ValueError as exc:
        print(f"invalid datetime filter: {exc}", file=sys.stderr)
        return 2

    fallback_filter = None
    if args.fallback is not None:
        fallback_filter = args.fallback == "true"

    rows = filter_rows(
        summarize(path),
        since=since,
        until=until,
        request_id=args.request_id,
        model=args.model,
        provider=args.provider,
        path=args.request_path,
        status=args.status,
        fallback=fallback_filter,
    )
    observed_summary = None
    if args.observed_cost_rub:
        observed_cost = decimal_from_value(args.observed_cost_rub)
        if observed_cost is None:
            print(f"invalid --observed-cost-rub value: {args.observed_cost_rub}", file=sys.stderr)
            return 2
        observed_summary = build_observed_cost_summary(rows, observed_cost)

    if args.json:
        payload: Any = rows if observed_summary is None else {"rows": rows, "observedCost": observed_summary}
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    else:
        output = format_table(rows)
        if observed_summary is not None:
            output += "\n" + format_observed_cost_summary(observed_summary)
        print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
