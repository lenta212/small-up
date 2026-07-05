# Local Stack

Scripts for running the local Monolith/LuaM stack without the remote server.

## Start

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

This starts:

- LuaM AI gateway on `127.0.0.1:8787`
- Content server on `127.0.0.1:1213`
- Content client connected to the local server

## Claude Haiku 4.5

To run the local gateway through Anthropic Claude Haiku 4.5 instead of the
OpenAI-compatible fallback, set these variables before starting the stack:

```powershell
$env:LUAM_AI_PROVIDER = "anthropic"
$env:ANTHROPIC_API_KEY = "<your Anthropic API key>"
$env:ANTHROPIC_MODEL = "claude-haiku-4-5-20251001"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

The shorthand `claude-haiku-4.5` is accepted locally and normalized to
`claude-haiku-4-5-20251001`.

## AI audit

`Tools\start_local_stack.ps1` writes the gateway audit path as `gateway_audit`.
By default it is:

```powershell
C:\MonolithTemp\gw-audit.jsonl
```

Summarize the audit log after a local test run:

```powershell
python Tools\summarize_luam_ai_audit.py
python Tools\summarize_luam_ai_audit.py C:\MonolithTemp\gw-audit.jsonl --json
python Tools\summarize_luam_ai_audit.py C:\MonolithTemp\gw-audit.jsonl --observed-cost-rub "0,25 ₽"
python Tools\summarize_luam_ai_audit.py C:\MonolithTemp\gw-audit.jsonl --since "2026-07-02 16:00" --until "2026-07-02 16:20"
python Tools\summarize_luam_ai_audit.py C:\MonolithTemp\gw-audit.jsonl --model claude-haiku --status 200 --fallback false --path /chat
```

The summary joins gateway and provider records by `requestId`, so a provider
panel row such as `1667 / 103 / 0 / 0` can be checked against the game request:

- `inputTokens`
- `outputTokens`
- `cacheCreationInputTokens`
- `cacheReadInputTokens`
- `fallback`
- `estimatedCostRub`
- `observedCostRub`, `costDeltaRub`, and `effectiveRubPerMillionTokens` when
  `--observed-cost-rub` is provided

Use `--request-id`, `--model`, `--provider`, `--path`, `--status`,
`--fallback`, `--since`, and `--until` to isolate a provider billing row or a
specific gateway request.

Cost fields are only written when local tariff variables are set before the
gateway starts:

```powershell
$env:LUAM_AI_INPUT_RUB_PER_MILLION_TOKENS = "100"
$env:LUAM_AI_OUTPUT_RUB_PER_MILLION_TOKENS = "1000"
$env:LUAM_AI_CACHE_CREATION_RUB_PER_MILLION_TOKENS = "125"
$env:LUAM_AI_CACHE_READ_RUB_PER_MILLION_TOKENS = "10"
$env:LUAM_AI_CACHED_INPUT_RUB_PER_MILLION_TOKENS = "50"
```

## Stop

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\stop_local_stack.ps1 -Force
```

## Smoke test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\test_local_stack.ps1
```

Use `-SkipClient` if you only need the gateway and server.
