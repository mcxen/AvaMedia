#!/usr/bin/env python3
"""Direct client for AvaMedia's stateless Streamable HTTP MCP endpoint."""
import argparse
import json
import sys
import uuid
from urllib.error import HTTPError
from urllib.request import Request, urlopen

PROTOCOL = "2026-07-28"
DEFAULT_URL = "http://127.0.0.1:18920/mcp"


def rpc(url, method, params=None, timeout=55):
    request_id = str(uuid.uuid4())
    parameters = dict(params or {})
    parameters["_meta"] = {
        "io.modelcontextprotocol/protocolVersion": PROTOCOL,
        "io.modelcontextprotocol/clientCapabilities": {},
        "io.modelcontextprotocol/clientInfo": {"name": "avamedia-skill", "version": "1.0"},
    }
    payload = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": parameters}
    request = Request(url, data=json.dumps(payload).encode("utf-8"), method="POST", headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": PROTOCOL, "Mcp-Method": method, "Mcp-Name": parameters.get("name", ""),
    })
    try:
        with urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
    except HTTPError as error:
        raise RuntimeError(f"HTTP {error.code}: {error.read().decode('utf-8', errors='replace')[:2000]}") from error
    if body.lstrip().startswith("{"):
        messages = [json.loads(body)]
    else:
        messages = []
        for event in body.replace("\r\n", "\n").split("\n\n"):
            data = "\n".join(line[5:].lstrip() for line in event.splitlines() if line.startswith("data:"))
            if data:
                messages.append(json.loads(data))
    reply = next((message for message in messages if message.get("id") == request_id), None)
    if reply is None:
        raise RuntimeError("MCP did not return a response for this request.")
    if "error" in reply:
        raise RuntimeError(json.dumps(reply["error"], ensure_ascii=False))
    result = reply["result"]
    if result.get("isError"):
        raise RuntimeError(json.dumps(result.get("content"), ensure_ascii=False))
    return result


def call(url, name, arguments=None, timeout=55):
    result = rpc(url, "tools/call", {"name": name, "arguments": arguments or {}}, timeout)
    if "structuredContent" in result:
        return result["structuredContent"]
    text = next((item["text"] for item in result.get("content", []) if item.get("type") == "text"), None)
    return json.loads(text) if text is not None else result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default=DEFAULT_URL)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("tools")
    invoke = commands.add_parser("call")
    invoke.add_argument("name")
    invoke.add_argument("--arguments-file", help="JSON arguments file; '-' reads stdin")
    args = parser.parse_args()
    try:
        arguments = {}
        if args.command == "call" and args.arguments_file:
            if args.arguments_file == "-":
                arguments = json.load(sys.stdin)
            else:
                with open(args.arguments_file, encoding="utf-8") as stream:
                    arguments = json.load(stream)
            if not isinstance(arguments, dict):
                raise ValueError("Tool arguments must be a JSON object.")
        result = rpc(args.url, "tools/list") if args.command == "tools" else call(args.url, args.name, arguments)
        print(json.dumps(result, ensure_ascii=False, indent=2))
    except (OSError, RuntimeError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
