#!/usr/bin/env python3
"""Runs the end-to-end demonstration with a local agent (headless Claude Code) and records it.

Prerequisites: the application is running (dotnet run --project src/InvoiceExceptions.Web) and `claude` is signed in.

  python scripts/run_agent_demo.py --case 004
  python scripts/run_agent_demo.py --case 010      # pauses until a person confirms the proposal on the case page

For each case it writes into submission/demo/:
  case-NNN*.jsonl        the stream-json log of every agent run: every message, tool call and reply, with the
                         client's local environment details (paths, installed plugins, usage) removed
  case-NNN-transcript.md a readable transcript with a tool-call log, client and model
  case-NNN-check.md      what the application stored versus the dataset's reference files

The agent gets the MCP tools and nothing else: no shell and no file access, so it cannot read expected/ or steps/.
"""
import argparse
import json
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER = "invoice-exceptions"

PROMPTS = {
    "004": ["Check the invoice in case 004 and tell me whether it can go on to payment. Explain the outcome."],
    "010": [
        "Check the invoice in case 010. If the purchase order cannot be determined, propose a match and explain what I need to confirm.",
        "I have reviewed your proposal on the case page and confirmed it. Run the check again and tell me the outcome.",
    ],
}


def sanitized(event):
    """Keeps what documents the run - who said what, which tool was called with what, what came back, which client and
    model - and drops what merely describes the machine it ran on: local paths, installed plugins and skills, hooks,
    token usage and cost. Returns None for events that carry nothing else."""
    kind = event.get("type")
    if kind == "system" and event.get("subtype") == "init":
        keep = ("type", "subtype", "session_id", "model", "claude_code_version", "mcp_servers", "tools", "permissionMode")
        return {key: event[key] for key in keep if key in event}
    if kind in ("assistant", "user"):
        message = event.get("message", {})
        content = message.get("content")
        if isinstance(content, list):
            content = [block for block in content if block.get("type") in ("text", "tool_use", "tool_result")]
            if not content:
                return None
        return {"type": kind, "message": {"role": message.get("role", kind), "content": content}}
    if kind == "result":
        keep = ("type", "subtype", "is_error", "session_id", "num_turns", "duration_ms", "stop_reason", "result")
        return {key: event[key] for key in keep if key in event}
    return None


def run_agent(prompt, log_path, model, resume=None):
    claude = shutil.which("claude")
    if claude is None:
        sys.exit("claude was not found on PATH")
    command = [
        claude, "-p", prompt,
        "--mcp-config", str(ROOT / ".mcp.json"), "--strict-mcp-config",
        "--tools", "",                              # no built-in tools: no shell, no file reads
        "--allowedTools", f"mcp__{SERVER}",
        "--output-format", "stream-json", "--verbose",
    ]
    if model:
        command += ["--model", model]
    if resume:
        command += ["--resume", resume]

    # A neutral working directory: the agent session has no project files around it.
    with tempfile.TemporaryDirectory(prefix="agent-demo-") as cwd:
        completed = subprocess.run(command, cwd=cwd, capture_output=True, text=True, encoding="utf-8")
    # The log starts with the prompt: the stream itself does not repeat what the user asked.
    events = [{"type": "user", "message": {"role": "user", "content": prompt}}]
    for line in completed.stdout.splitlines():
        if line.strip():
            try:
                event = sanitized(json.loads(line))
            except json.JSONDecodeError:
                continue
            if event is not None:
                events.append(event)
    Path(log_path).write_text("".join(json.dumps(e, ensure_ascii=False) + "\n" for e in events), encoding="utf-8")
    final = next((e for e in reversed(events) if e.get("type") == "result"), {})
    init = next((e for e in events if e.get("type") == "system" and e.get("subtype") == "init"), {})
    failed = completed.returncode != 0 or final.get("is_error") or not any(
        s.get("name") == SERVER and s.get("status") == "connected" for s in init.get("mcp_servers", []))
    if failed:
        sys.exit(f"The agent run failed (exit {completed.returncode}): {final.get('result') or completed.stderr.strip()[:500]}")
    print(f"  agent finished: {final.get('num_turns')} turns -> {log_path.name}")
    return init.get("session_id")


def stored_result(base_url, case_id):
    manifest = json.loads((ROOT / "dataset" / "cases" / case_id / "case.json").read_text(encoding="utf-8"))
    document_id = manifest["input_documents"][0]["document_id"]
    with urllib.request.urlopen(f"{base_url}/api/cases/{case_id}/documents/{document_id}/result", timeout=15) as response:
        return json.loads(response.read().decode("utf-8"))["result"]


def wait_for_confirmation(base_url, case_id, timeout_seconds):
    print(f"  waiting for a person to confirm the proposal at {base_url}/cases/{case_id} ...")
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        result = stored_result(base_url, case_id)
        if result.get("order_match_source") == "human_confirmation" or any(
                line.get("match_source") == "human_confirmation" for line in result.get("lines", [])):
            print(f"  confirmed: input revision {result['input_revision']}")
            return
        time.sleep(2)
    sys.exit("Timed out waiting for the confirmation.")


def script(name, *args):
    completed = subprocess.run([sys.executable, str(ROOT / "scripts" / name), *args],
                               capture_output=True, text=True, encoding="utf-8")
    return completed.returncode, completed.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--case", required=True, choices=sorted(PROMPTS))
    parser.add_argument("--model", default="opus", help="model alias or id passed to claude --model")
    parser.add_argument("--base-url", default="http://localhost:5080")
    parser.add_argument("--out", type=Path, default=ROOT / "submission" / "demo")
    parser.add_argument("--confirmation-timeout", type=int, default=900)
    args = parser.parse_args()

    case_id = f"case-{args.case}"
    args.out.mkdir(parents=True, exist_ok=True)
    prompts = PROMPTS[args.case]
    logs, checks, session = [], [], None

    for index, prompt in enumerate(prompts, start=1):
        if index > 1:
            # Between the two agent runs of case 010 the decision belongs to a person, on the page.
            wait_for_confirmation(args.base_url, case_id, args.confirmation_timeout)
        log = args.out / (f"{case_id}.jsonl" if len(prompts) == 1 else f"{case_id}-part{index}.jsonl")
        print(f"[{case_id}] run {index}: {prompt}")
        session = run_agent(prompt, log, args.model, resume=session)
        logs.append(log)

        # Case 010 is compared after each run: with step 2 once the proposal is in, with step 3 after confirmation.
        step = [] if len(prompts) == 1 else ["--step", str(index + 1)]
        code, report = script("compare_with_reference.py", "--case", case_id, "--base-url", args.base_url, *step)
        checks.append(report)
        print("  " + ("matches the reference" if code == 0 else "DIFFERS from the reference - see the check file"))

    _, transcript = script("render_transcript.py", *map(str, logs), "--title", f"Case {args.case}: agent run through MCP")
    (args.out / f"{case_id}-transcript.md").write_text(transcript, encoding="utf-8")
    (args.out / f"{case_id}-check.md").write_text("\n".join(checks), encoding="utf-8")
    print(f"[{case_id}] transcript: {args.out / (case_id + '-transcript.md')}")


if __name__ == "__main__":
    main()
