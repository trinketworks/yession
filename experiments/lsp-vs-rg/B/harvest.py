"""Turn subject transcripts into trials.csv rows, and grade them.
Usage: python3 -I harvest.py   (reads runs.jsonl, tasks.json; writes trials.csv)
runs.jsonl: one {"trial","block","seq","task","cond","agent_id","note"} per spawned subject,
appended by the experimenter as each subject is launched."""
import csv, json, pathlib, re, sys
from datetime import datetime
HERE = pathlib.Path(__import__("os").environ.get("EXP_DATA") or pathlib.Path(__file__).parent)
ROOT = "/home/user/yession/"
TRANSCRIPTS = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else
    "/root/.claude/projects/-home-user-yession/a6bba015-6bf4-5be7-a673-94b6fbd56e27/subagents")
TASKS = {t["id"]: t for t in json.loads((HERE / "tasks.json").read_text())["tasks"]}

def norm_path(p):
    p = p.strip().strip("`'\"").strip()
    if p.startswith(ROOT): p = p[len(ROOT):]
    if p.startswith("./"): p = p[2:]
    return p

def norm_type(s):
    s = s.strip().strip("`").strip()
    s = re.sub(r"\s+", "", s)
    return s

def grade(task, answer):
    """Returns (correct 0/1, detail dict)."""
    if answer is None: return 0, {"why": "no answer"}
    rule = task["scoring"]
    exp = task["expected"]
    if rule == "location":  # exact file:line
        m = re.search(r"([\w./\-]+\.fsi?)\s*:\s*(\d+)", answer)
        if not m: return 0, {"why": "unparseable"}
        got = f"{norm_path(m.group(1))}:{m.group(2)}"
        return int(got == exp), {"got": got}
    if rule == "file_set":
        got = {norm_path(x) for x in re.split(r"[,\n;]+", answer) if x.strip()}
        got = {g for g in got if g}
        e = set(exp)
        tp = len(got & e)
        return int(got == e), {"got": sorted(got), "precision": tp / len(got) if got else 0, "recall": tp / len(e)}
    if rule == "integer":
        m = re.search(r"-?\d+", answer)
        if not m: return 0, {"why": "unparseable"}
        return int(int(m.group()) == exp), {"got": int(m.group())}
    if rule == "type":
        got = norm_type(answer)
        return int(got in {norm_type(a) for a in [exp] + task.get("accept", [])}), {"got": got}
    raise ValueError(rule)

def ts(s): return datetime.fromisoformat(s.replace("Z", "+00:00")).timestamp()

def parse(agent_id):
    f = TRANSCRIPTS / f"agent-{agent_id}.jsonl"
    rows = [json.loads(l) for l in f.read_text().splitlines() if l.strip()]
    start = next(r["timestamp"] for r in rows if r.get("type") == "user")
    end, answer, calls, lsp_calls, cmds = None, None, 0, 0, []
    seen, tok_in, tok_out, tok_cache_read, tok_cache_write = set(), 0, 0, 0, 0
    for r in rows:
        if r.get("type") != "assistant": continue
        m = r["message"]
        mid = m.get("id")
        if mid not in seen and m.get("usage"):
            seen.add(mid); u = m["usage"]
            tok_in += u.get("input_tokens", 0); tok_out += u.get("output_tokens", 0)
            tok_cache_read += u.get("cache_read_input_tokens", 0); tok_cache_write += u.get("cache_creation_input_tokens", 0)
        for c in m.get("content") or []:
            if c.get("type") != "tool_use": continue
            if c["name"] == "SubagentHandback":
                end = r["timestamp"]; answer = c["input"].get("message", ""); continue
            calls += 1
            cmd = c["input"].get("command", "") if c["name"] == "Bash" else ""
            if "lsp-query" in cmd: lsp_calls += 1
            cmds.append(f'{c["name"]}:{(cmd or json.dumps(c["input"]))[:160]}')
    if end is None:  # final text answer instead of handback
        last = [r for r in rows if r.get("type") == "assistant"]
        if last:
            end = last[-1]["timestamp"]
            answer = " ".join(c.get("text", "") for c in last[-1]["message"].get("content") or [] if c.get("type") == "text")
    final = None
    if answer:
        ms = re.findall(r"ANSWER:\s*(.+(?:\n(?!\s*$).+)*)", answer)
        final = ms[-1].strip() if ms else answer.strip()
    return {"seconds": round(ts(end) - ts(start), 1) if end else None, "tool_calls": calls,
            "lsp_calls": lsp_calls, "lsp_used": int(lsp_calls > 0), "answer": final,
            "tok_input": tok_in, "tok_output": tok_out, "tok_cache_read": tok_cache_read,
            "tok_cache_write": tok_cache_write, "commands": cmds}

def main():
    runs = [json.loads(l) for l in (HERE / "runs.jsonl").read_text().splitlines() if l.strip()]
    out = []
    for run in runs:
        p = parse(run["agent_id"])
        task = TASKS[run["task"]]
        correct, detail = grade(task, p["answer"])
        out.append({**{k: run[k] for k in ("trial", "block", "seq", "task", "cond", "agent_id")},
                    "class": task["class"], "correct": correct, "seconds": p["seconds"],
                    "tool_calls": p["tool_calls"], "lsp_calls": p["lsp_calls"], "lsp_used": p["lsp_used"],
                    "tok_input": p["tok_input"], "tok_output": p["tok_output"],
                    "tok_cache_read": p["tok_cache_read"], "tok_cache_write": p["tok_cache_write"],
                    "note": run.get("note", ""), "answer": (p["answer"] or "").replace("\n", " | ")[:600],
                    "grade_detail": json.dumps(detail), "commands": " || ".join(p["commands"])[:4000]})
    with open(HERE / "trials.csv", "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(out[0].keys())); w.writeheader(); w.writerows(out)
    for o in out:
        print(o["trial"], o["task"], o["cond"], "correct" if o["correct"] else "WRONG", o["seconds"], "s", o["tool_calls"], "calls", "lsp" if o["lsp_used"] else "", o["answer"][:80])
if __name__ == "__main__": main()
