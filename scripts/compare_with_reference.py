#!/usr/bin/env python3
"""End-to-end check of an agent run: what the application stored versus the dataset's reference files.

Usage:
  python scripts/compare_with_reference.py --case case-004 [--step 1] [--base-url http://localhost:5080]

It reads, from the running application's read-only API,
  * the facts the agent registered  -> compared field by field with expected/extraction.json
  * the stored conclusion           -> compared with expected/step-NNN.json using the acceptance runner's own rules
The agent never sees the reference files; this script is for the person evaluating the run.
Exit code 0 when both match, 1 otherwise.
"""
import argparse
import json
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
from run_evals import compare  # noqa: E402  (the runner's comparison is the definition of "matches")


def fetch(url):
    with urllib.request.urlopen(url, timeout=15) as response:
        return json.loads(response.read().decode("utf-8"))


def flatten(value, path=""):
    if isinstance(value, dict):
        for key, item in value.items():
            yield from flatten(item, f"{path}/{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from flatten(item, f"{path}/{index}")
    else:
        yield path, value


def compare_facts(expected, actual):
    skip = ("evidence", "missing_fields")
    exp = dict(flatten({k: v for k, v in expected.items() if k not in skip}))
    act = dict(flatten({k: v for k, v in actual.items() if k not in skip}))
    diffs = [f"{path}: expected {exp.get(path)!r}, got {act.get(path)!r}"
             for path in sorted(set(exp) | set(act)) if exp.get(path) != act.get(path)]

    exp_missing = {(m["field_path"], m["reason"]) for m in expected["missing_fields"]}
    act_missing = {(m["field_path"], m["reason"]) for m in actual["missing_fields"]}
    if exp_missing != act_missing:
        diffs.append(f"missing_fields: expected {sorted(exp_missing)}, got {sorted(act_missing)}")

    exp_evidence = {e["field_path"]: e for e in expected["evidence"]}
    act_evidence = {e["field_path"]: e for e in actual["evidence"]}
    without_source = sorted(set(exp_evidence) - set(act_evidence))
    same_excerpt = sum(1 for path, e in exp_evidence.items()
                       if path in act_evidence and act_evidence[path]["excerpt"] == e["excerpt"]
                       and act_evidence[path]["page"] == e["page"])
    return diffs, len(exp), without_source, same_excerpt, len(exp_evidence)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--case", required=True)
    parser.add_argument("--step", type=int, help="expected step to compare the conclusion with; default: the last one")
    parser.add_argument("--base-url", default="http://localhost:5080")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    case_dir = ROOT / "dataset" / "cases" / args.case
    manifest = json.loads((case_dir / "case.json").read_text(encoding="utf-8"))
    document_id = manifest["input_documents"][0]["document_id"]
    step = args.step or manifest["steps"][-1]["step"]
    expected_facts = json.loads((case_dir / "expected" / "extraction.json").read_text(encoding="utf-8"))
    expected_step = json.loads((case_dir / "expected" / f"step-{step:03d}.json").read_text(encoding="utf-8"))

    base = f"{args.base_url}/api/cases/{args.case}/documents/{document_id}"
    facts = fetch(f"{base}/facts")
    stored = fetch(f"{base}/result")

    fact_diffs, fact_count, without_source, same_excerpt, evidence_count = compare_facts(expected_facts, facts)

    # The agent path legitimately differs from the runner path in these three: the context version is derived
    # from the context files, and revisions count the agent's own calls.
    actual = dict(stored["result"], case_id=args.case, step=step, schema_version="1.0")
    for field in ("context_version",):
        actual[field] = expected_step[field]
    result_diffs = compare(expected_step, actual)

    print(f"## {args.case} / {document_id}: stored state vs reference")
    print()
    print(f"- Extracted facts: {fact_count - len(fact_diffs)}/{fact_count} values equal to expected/extraction.json")
    for diff in fact_diffs:
        print(f"  - {diff}")
    print(f"- Evidence: {evidence_count - len(without_source)}/{evidence_count} expected fields have a source; "
          f"{same_excerpt}/{evidence_count} with the identical page and excerpt")
    for path in without_source:
        print(f"  - no evidence for {path}")
    print(f"- Conclusion (assessment `{stored['assessment_id']}`, input revision {stored['result']['input_revision']}): "
          f"decision `{stored['result']['decision']}`, import `{stored['result']['import_disposition']}` - "
          f"{'matches' if not result_diffs else 'DIFFERS from'} expected/step-{step:03d}.json under the runner's rules")
    for diff in result_diffs:
        print(f"  - {diff}")
    return 0 if not fact_diffs and not result_diffs and not without_source else 1


if __name__ == "__main__":
    sys.exit(main())
