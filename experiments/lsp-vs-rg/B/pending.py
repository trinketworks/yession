"""Print trials in runs.jsonl whose subject has not handed back yet. Usage: python3 -I pending.py"""
import json, pathlib, os
HERE = pathlib.Path(os.environ.get("EXP_DATA") or pathlib.Path(__file__).parent)
T = pathlib.Path("/root/.claude/projects/-home-user-yession/a6bba015-6bf4-5be7-a673-94b6fbd56e27/subagents")
def done(a):
    f = T / f"agent-{a}.jsonl"
    if not f.exists(): return False
    for l in f.read_text().splitlines():
        d = json.loads(l)
        if d.get("type") == "assistant" and any(c.get("type") == "tool_use" and c.get("name") == "SubagentHandback" for c in d["message"].get("content") or []):
            return True
    return False
p = [r["trial"] for r in map(json.loads, (HERE / "runs.jsonl").read_text().splitlines()) if not done(r["agent_id"])]
print(" ".join(p) if p else "none")
