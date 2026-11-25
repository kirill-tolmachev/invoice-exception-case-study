#!/usr/bin/env python3
"""Turns a Claude Code `--output-format stream-json` log into a readable Markdown transcript.

Usage:
  python scripts/render_transcript.py <run.jsonl> [<run2.jsonl> ...] --title "Case 004" > transcript.md

Every tool call is shown with its arguments and the server's reply, in order, followed by a compact tool-call log.
Long replies are shortened in the transcript; the .jsonl files keep everything.
"""
import argparse
import json
import sys
from pathlib import Path

MAX_RESULT_CHARS = 2600


def load(path):
    events = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    return events


def pretty(value):
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            return value
    return json.dumps(value, indent=2, ensure_ascii=False)


def shorten(text):
    if len(text) <= MAX_RESULT_CHARS:
        return text
    return text[:MAX_RESULT_CHARS] + f"\n... [{len(text) - MAX_RESULT_CHARS} more characters in the .jsonl log]"


def result_text(content):
    if isinstance(content, str):
        return content
    parts = []
    for block in content or []:
        if isinstance(block, dict) and block.get("type") == "text":
            parts.append(block.get("text", ""))
    return "\n".join(parts)


def render(paths, title):
    out = [f"# {title}", ""]
    log = []
    step = 0
    for index, path in enumerate(paths, start=1):
        events = load(path)
        init = next((e for e in events if e.get("type") == "system" and e.get("subtype") == "init"), {})
        final = next((e for e in reversed(events) if e.get("type") == "result"), {})
        tools = [t for t in init.get("tools", []) if t.startswith("mcp__")]
        other_tools = [t for t in init.get("tools", []) if not t.startswith("mcp__")]
        servers = ", ".join(f"{s.get('name')} ({s.get('status')})" for s in init.get("mcp_servers", []))

        out += [f"## Run {index}: `{Path(path).name}`", ""]
        out += ["| | |", "|---|---|"]
        out += [f"| Client | Claude Code {init.get('claude_code_version', '?')} (headless, `claude -p`) |"]
        out += [f"| Model | `{init.get('model', '?')}` |"]
        out += [f"| Session | `{init.get('session_id', '?')}` |"]
        out += [f"| MCP servers | {servers or '?'} |"]
        out += [f"| MCP tools offered | {', '.join('`' + t.split('__')[-1] + '`' for t in tools) or '?'} |"]
        out += [f"| Other tools offered | {', '.join('`' + t + '`' for t in other_tools) or 'none'} |"]
        if final:
            out += [f"| Turns / duration | {final.get('num_turns', '?')} turns, {round(final.get('duration_ms', 0) / 1000, 1)} s |"]
        out += [""]

        pending = {}
        for event in events:
            kind = event.get("type")
            message = event.get("message", {})
            if kind == "user":
                content = message.get("content")
                if isinstance(content, str):
                    out += ["### User", "", content, ""]
                    continue
                for block in content or []:
                    if block.get("type") == "text":
                        out += ["### User", "", block.get("text", ""), ""]
                    elif block.get("type") == "tool_result":
                        call = pending.pop(block.get("tool_use_id"), None)
                        text = result_text(block.get("content"))
                        is_error = bool(block.get("is_error"))
                        out += [f"**Reply{' (error)' if is_error else ''}:**", "", "```json", shorten(pretty(text)), "```", ""]
                        if call is not None:
                            call["error"] = is_error
                            call["summary"] = summarize(text)
            elif kind == "assistant":
                for block in message.get("content", []):
                    if block.get("type") == "text" and block.get("text", "").strip():
                        out += ["### Agent", "", block["text"].strip(), ""]
                    elif block.get("type") == "tool_use":
                        step += 1
                        name = block.get("name", "").split("__")[-1]
                        entry = {"n": step, "run": index, "tool": name, "args": block.get("input", {}), "error": None, "summary": ""}
                        pending[block.get("id")] = entry
                        log.append(entry)
                        out += [f"### Tool call {step}: `{name}`", "", "```json", shorten(pretty(block.get("input", {}))), "```", ""]

    out += ["## Tool-call log", "", "| # | Run | Tool | Arguments | Outcome |", "|---|---|---|---|---|"]
    for entry in log:
        args = ", ".join(f"{k}={brief(v)}" for k, v in entry["args"].items())
        outcome = ("ERROR: " if entry["error"] else "") + entry["summary"]
        out += [f"| {entry['n']} | {entry['run']} | `{entry['tool']}` | {args} | {outcome} |"]
    out += [""]
    return "\n".join(out)


def brief(value):
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
    text = text.replace("|", "\\|").replace("\n", " ")
    return text if len(text) <= 60 else text[:57] + "..."


def summarize(text):
    """One line saying what the server answered: the fields that matter for following the case."""
    try:
        data = json.loads(text)
    except (json.JSONDecodeError, TypeError):
        return brief(text or "")
    if not isinstance(data, dict):
        return brief(text)
    if "error" in data:
        details = data["error"].get("details") or []
        first = f": {details[0].get('path')} {details[0].get('message')}" if details else ""
        return brief(f"{data['error'].get('code')}{first}")
    parts = []
    result = data.get("result") if isinstance(data.get("result"), dict) else data
    for key in ("input_revision", "import_disposition", "decision", "proposal_id", "status", "context_version"):
        if key in result and result[key] is not None:
            parts.append(f"{key}={result[key]}")
    if isinstance(result.get("questions"), list) and result["questions"]:
        parts.append("questions=" + ",".join(q.get("required_fact", "?") for q in result["questions"]))
    if isinstance(data.get("evidence_check"), dict):
        check = data["evidence_check"]
        parts.append(f"evidence {check.get('found_on_stated_page')}/{check.get('excerpts')} found")
    if isinstance(data.get("pages"), list):
        parts.append(f"{len(data['pages'])} page(s) of text")
    return "; ".join(parts) or "ok"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("logs", nargs="+")
    parser.add_argument("--title", default="Agent transcript")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    print(render(args.logs, args.title))


if __name__ == "__main__":
    main()
