"""Randomized complete block schedule. One block = 12 tasks x 2 conditions.
Task order and condition order randomized per block from a recorded seed.
Usage: python3 -I design.py <block>  -> prints JSON list of trials for that block."""
import json, random, sys, pathlib
SEED = 20261102  # replicate B base seed; block b uses SEED + b
def block(b):
    tasks = [t["id"] for t in json.loads((pathlib.Path(__import__("os").environ.get("EXP_DATA") or pathlib.Path(__file__).parent) / "tasks.json").read_text())["tasks"]]
    rng = random.Random(SEED + b)
    trials = [(t, c) for t in tasks for c in ("RG", "LSP")]
    rng.shuffle(trials)  # randomizes task order and, within task, which condition runs first
    return [{"trial": f"b{b}-{i+1:02d}", "block": b, "seq": i + 1, "task": t, "cond": c} for i, (t, c) in enumerate(trials)]
if __name__ == "__main__":
    print(json.dumps(block(int(sys.argv[1])), indent=1))
