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

## Free local TTS with Piper

Piper is the default free local TTS path for LuaM. It runs beside the local
gateway and does not require an API key.

Install Piper into an isolated venv and download the default Russian voice:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\setup_luam_piper_tts.ps1
```

Start the local stack with TTS enabled:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset -PiperTts
```

`-PiperTts` starts the gateway with:

- `LUAM_TTS_PROVIDER=piper`
- `LUAM_TTS_PIPER_MODEL=ru_RU-irina-medium`
- `LUAM_TTS_PIPER_VOICES=ru_RU-irina-medium,ru_RU-denis-medium,ru_RU-dmitri-medium,ru_RU-ruslan-medium`
- `LUAM_TTS_PIPER_DATA_DIR=C:\MonolithTemp\luam-piper-voices`

It also enables `luam.ai_director.tts_characters_enabled` only in the temporary
local server config copy. `luam.ai_director.tts_enabled` stays disabled so the
local Piper voice is used for character IC lines, not AI Director lines. The
default checked-in config keeps TTS disabled.

Verify the gateway sees the TTS provider:

```powershell
Invoke-RestMethod http://127.0.0.1:8787/health
```

## OpenAI GPT-5.5 through MCP

To run the local LuaM gateway through the LuaM OpenAI MCP server, set an
official OpenAI key in a separate variable before starting the stack. Do not
commit or paste the key into config files.

```powershell
$env:OPENAI_OFFICIAL_API_KEY = "<your official OpenAI API key>"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset -OfficialOpenAI
```

`-OfficialOpenAI` starts the gateway with `LUAM_AI_PROVIDER=mcp`,
`OPENAI_MODEL=gpt-5.5`, and `Tools\luam_openai_mcp_server.py` as a local MCP
stdio server. The game server still talks only to the local LuaM gateway; the
gateway calls the MCP tool, and the MCP server owns official OpenAI
authorization.

`OPENAI_API_KEY` is intentionally not used by this official MCP path. Keep a
custom provider key out of the official route and use the `LUAM_COMPAT_*`
variables below for that separate provider.

During the gateway process startup, `-OfficialOpenAI` temporarily clears custom
provider/proxy variables such as `OPENAI_BASE_URL`, `OPENAI_API_BASE`,
`LUAM_COMPAT_API_KEY`, `LUAM_COMPAT_BASE_URL`, and old direct OpenAI URL
overrides.

Verify the gateway connection without exposing the key:

```powershell
Invoke-RestMethod http://127.0.0.1:8787/health
```

Expected OpenAI fields:

- `provider`: `mcp`
- `api`: `mcp`
- `model`: `gpt-5.5`
- `baseUrl`: `mcp://luam-openai`
- `hasApiKey`: `true`

## Custom OpenAI-compatible provider

For an OpenAI-compatible proxy instead of official OpenAI MCP, use the separate
custom-provider variables:

```powershell
$env:LUAM_AI_PROVIDER = "openai-compatible"
$env:LUAM_COMPAT_API_KEY = "<your custom provider key>"
$env:LUAM_COMPAT_BASE_URL = "https://api.apiprovider.pro/v1"
$env:LUAM_COMPAT_MODEL = "5.5"
$env:LUAM_COMPAT_API_MODE = "auto"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Do not put this custom key into `OPENAI_OFFICIAL_API_KEY`.

## External MCP provider

To connect a Codex-style, OpenClav/OpenClave, Hermes, or other stdio MCP
provider, point LuaM at that MCP command and tool. The gateway sends a sanitized
LuaM request as the `request` argument and also includes `endpoint` and `model`.

```powershell
$env:LUAM_AI_PROVIDER = "mcp"
$env:LUAM_MCP_COMMAND = "npx -y your-mcp-server"
$env:LUAM_MCP_TOOL = "openai_responses_json"
$env:LUAM_MCP_SERVER_ID = "hermes"
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\start_local_stack.ps1 -Reset
```

Compatible MCP tool outputs:

- `{"response": <OpenAI Responses-style JSON with output_text>}`
- direct OpenAI Responses-style JSON with `output_text`
- direct LuaM response objects for event/chat/review, such as `templateId`,
  `reply/action`, or `summary/recommendedActions`

If a provider uses a different tool contract, keep the gateway unchanged and add
a small MCP adapter script that translates that provider into one of these
shapes.

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
