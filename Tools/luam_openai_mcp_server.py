#!/usr/bin/env python3
"""
Minimal stdio MCP server for LuaM official OpenAI access.

The LuaM gateway talks to this process through MCP JSON-RPC over stdio. This
keeps official OpenAI authorization in a separate process and avoids reusing a
custom OpenAI-compatible provider key from OPENAI_API_KEY.

Environment:
  OPENAI_OFFICIAL_API_KEY       required official OpenAI API key
  OPENAI_OFFICIAL_BASE_URL      optional base URL, defaults to https://api.openai.com/v1
  LUAM_MCP_RESPONSES_URL        optional full /responses URL for local tests
  LUAM_OPENAI_MCP_RESPONSES_URL legacy alias for LUAM_MCP_RESPONSES_URL
  OPENAI_TIMEOUT                optional request timeout in seconds
"""

from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request
from typing import Any


OPENAI_DEFAULT_BASE_URL = "https://api.openai.com/v1"
SERVER_NAME = "luam-openai-mcp"
SERVER_VERSION = "1.0.0"
TOOL_OPENAI_RESPONSES = "openai_responses_json"


def configure_stdio() -> None:
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            reconfigure(encoding="utf-8")


def write_message(message: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def result_response(message_id: Any, result: dict[str, Any]) -> dict[str, Any]:
    return {
        "jsonrpc": "2.0",
        "id": message_id,
        "result": result,
    }


def error_response(message_id: Any, code: int, message: str, data: Any = None) -> dict[str, Any]:
    error: dict[str, Any] = {
        "code": code,
        "message": message,
    }
    if data is not None:
        error["data"] = data
    return {
        "jsonrpc": "2.0",
        "id": message_id,
        "error": error,
    }


def official_api_key() -> str:
    key = os.environ.get("OPENAI_OFFICIAL_API_KEY", "").strip()
    if not key:
        raise RuntimeError("OPENAI_OFFICIAL_API_KEY is not set for LuaM OpenAI MCP server")
    return key


def responses_url() -> str:
    explicit = (
        os.environ.get("LUAM_MCP_RESPONSES_URL", "")
        or os.environ.get("LUAM_OPENAI_MCP_RESPONSES_URL", "")
    ).strip()
    if explicit:
        return explicit

    base = (os.environ.get("OPENAI_OFFICIAL_BASE_URL", "") or OPENAI_DEFAULT_BASE_URL).rstrip("/")
    return f"{base}/responses"


def post_openai_responses(body: dict[str, Any]) -> dict[str, Any]:
    payload = json.dumps(body, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        responses_url(),
        data=payload,
        method="POST",
        headers={
            "Authorization": f"Bearer {official_api_key()}",
            "Content-Type": "application/json",
        },
    )
    timeout = float(os.environ.get("OPENAI_TIMEOUT", "20"))
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        body_text = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"OpenAI HTTP {exc.code}: {body_text[:1200]}") from exc

    data = json.loads(raw)
    if not isinstance(data, dict):
        raise RuntimeError("OpenAI response is not a JSON object")
    return data


def initialize_result(params: dict[str, Any]) -> dict[str, Any]:
    protocol_version = params.get("protocolVersion")
    if not isinstance(protocol_version, str) or not protocol_version:
        protocol_version = "2024-11-05"

    return {
        "protocolVersion": protocol_version,
        "capabilities": {
            "tools": {
                "listChanged": False,
            },
        },
        "serverInfo": {
            "name": SERVER_NAME,
            "version": SERVER_VERSION,
        },
    }


def tools_list_result() -> dict[str, Any]:
    return {
        "tools": [
            {
                "name": TOOL_OPENAI_RESPONSES,
                "description": "Send one official OpenAI Responses API JSON request for LuaM.",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "request": {
                            "type": "object",
                            "description": "Raw OpenAI Responses API request JSON.",
                        },
                        "endpoint": {
                            "type": "string",
                            "description": "LuaM endpoint name: event, chat, review, or responses.",
                        },
                        "model": {
                            "type": "string",
                            "description": "LuaM-selected model name.",
                        },
                    },
                    "required": ["request"],
                    "additionalProperties": False,
                },
            }
        ],
    }


def tool_call_result(params: dict[str, Any]) -> dict[str, Any]:
    name = params.get("name")
    arguments = params.get("arguments")
    if name != TOOL_OPENAI_RESPONSES:
        raise ValueError(f"unknown tool: {name}")
    if not isinstance(arguments, dict):
        raise ValueError("tool arguments must be an object")
    request = arguments.get("request")
    if not isinstance(request, dict):
        raise ValueError("tool argument 'request' must be an object")

    response = post_openai_responses(request)
    return {
        "content": [
            {
                "type": "text",
                "text": json.dumps({"response": response}, ensure_ascii=False, separators=(",", ":")),
            }
        ],
        "isError": False,
    }


def handle_request(message: dict[str, Any]) -> dict[str, Any] | None:
    message_id = message.get("id")
    method = message.get("method")
    params = message.get("params")
    if params is None:
        params = {}
    if not isinstance(params, dict):
        return error_response(message_id, -32602, "params must be an object")

    try:
        if method == "initialize":
            return result_response(message_id, initialize_result(params))
        if method == "ping":
            return result_response(message_id, {})
        if method == "tools/list":
            return result_response(message_id, tools_list_result())
        if method == "tools/call":
            return result_response(message_id, tool_call_result(params))
        if method and str(method).startswith("notifications/"):
            return None
        return error_response(message_id, -32601, f"method not found: {method}")
    except ValueError as exc:
        return error_response(message_id, -32602, str(exc))
    except Exception as exc:
        return error_response(message_id, -32000, f"{type(exc).__name__}: {exc}")


def main() -> int:
    configure_stdio()
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
            if not isinstance(message, dict):
                raise ValueError("message must be an object")
        except Exception as exc:
            write_message(error_response(None, -32700, f"parse error: {exc}"))
            continue

        response = handle_request(message)
        if response is not None:
            write_message(response)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
