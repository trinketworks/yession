"""Analysis for the lsp-vs-rg experiment, replicate B. Standard library only.
Usage: python3 -I analyze.py [--through-block K]   (reads trials.csv, prints a markdown report section)"""
import csv, json, math, pathlib, random, statistics as st, sys
from collections import defaultdict
HERE = pathlib.Path(__import__("os").environ.get("EXP_DATA") or pathlib.Path(__file__).parent)
B = 10000; SEED = 7
LOG_RATIO_TARGET = math.log(0.75)  # a 25% time difference
ACC_TARGET = 0.15
Z_A, Z_B = 1.959964, 0.841621  # alpha .05 two-sided, power .80

def load(through=None):
    rows = list(csv.DictReader(open(HERE / "trials.csv")))
    for r in rows:
        for k in ("block", "correct", "tool_calls", "lsp_used", "lsp_calls", "tok_input", "tok_output", "tok_cache_read", "tok_cache_write"):
            r[k] = int(r[k])
        r["seconds"] = float(r["seconds"])
        r["tokens"] = r["tok_input"] + r["tok_output"] + r["tok_cache_read"] + r["tok_cache_write"]
        r["timing_ok"] = "daemon restarted" not in r["note"] and "exclude-timing" not in r["note"]
    if through: rows = [r for r in rows if r["block"] <= through]
    return rows

def pairs(rows):
    by = defaultdict(dict)
    for r in rows: by[(r["task"], r["block"])][r["cond"]] = r
    return [(k, v["RG"], v["LSP"]) for k, v in sorted(by.items()) if "RG" in v and "LSP" in v]

def pct(xs, q):
    xs = sorted(xs); i = q * (len(xs) - 1); lo = math.floor(i); hi = math.ceil(i)
    return xs[lo] + (xs[hi] - xs[lo]) * (i - lo)

def boot(items, stat, cluster=None, seed=SEED):
    """Percentile bootstrap CI. cluster: key fn -> resample clusters (then items within) instead of items."""
    rng = random.Random(seed); out = []
    if cluster:
        groups = defaultdict(list)
        for it in items: groups[cluster(it)].append(it)
        keys = list(groups)
    for _ in range(B):
        if cluster:
            s = []
            for k in (rng.choice(keys) for _ in keys):
                g = groups[k]; s += [rng.choice(g) for _ in g]
        else:
            s = [rng.choice(items) for _ in items]
        v = stat(s)
        if v is not None: out.append(v)
    return pct(out, .025), pct(out, .975)

def mcnemar_exact(b, c):
    n = b + c
    if n == 0: return 1.0
    k = min(b, c)
    p = sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n
    return min(1.0, 2 * p)

def logdiff(prs, field="seconds", need_both_correct=True):
    return [math.log(l[field]) - math.log(r[field]) for _, r, l in prs
            if r[field] > 0 and l[field] > 0 and r["timing_ok"] and l["timing_ok"]
            and (not need_both_correct or (r["correct"] and l["correct"]))]

def mean(xs): return sum(xs) / len(xs) if xs else None

def summarize(prs, label=""):
    d = logdiff(prs)
    acc = [l["correct"] - r["correct"] for _, r, l in prs]
    b = sum(1 for _, r, l in prs if r["correct"] and not l["correct"])
    c = sum(1 for _, r, l in prs if l["correct"] and not r["correct"])
    res = {"label": label, "pairs": len(prs), "both_correct": len(d),
           "acc_rg": mean([r["correct"] for _, r, _ in prs]), "acc_lsp": mean([l["correct"] for _, _, l in prs]),
           "acc_diff": mean(acc), "rg_only": b, "lsp_only": c, "mcnemar_p": mcnemar_exact(b, c),
           "logratio": mean(d)}
    if len(d) >= 2:
        res["logratio_ci"] = boot(d, mean)
        res["logratio_ci_task"] = boot([(k[0], x) for (k, r, l), x in zip([p for p in prs if p[1]["correct"] and p[2]["correct"] and p[1]["timing_ok"] and p[2]["timing_ok"]], d)],
                                       lambda s: mean([x for _, x in s]), cluster=lambda it: it[0])
        res["sd_logdiff"] = st.stdev(d)
    if len(acc) >= 2:
        res["acc_ci"] = boot(acc, mean)
        res["acc_ci_task"] = boot([(k[0], x) for (k, _, _), x in zip(prs, acc)], lambda s: mean([x for _, x in s]), cluster=lambda it: it[0])
    da = logdiff(prs, need_both_correct=False)
    res["logratio_all"] = mean(da); res["logratio_all_ci"] = boot(da, mean) if len(da) > 1 else None
    for f in ("tool_calls", "tokens"):
        x = [math.log(max(l[f], 1)) - math.log(max(r[f], 1)) for _, r, l in prs]
        res[f + "_logratio"] = mean(x); res[f + "_ci"] = boot(x, mean) if len(x) > 1 else None
    return res

def power(prs):
    d = logdiff(prs, need_both_correct=False)
    sd = st.stdev(d)
    n_time = math.ceil(((Z_A + Z_B) * sd / abs(LOG_RATIO_TARGET)) ** 2)
    disc = mean([int(r["correct"] != l["correct"]) for _, r, l in prs])
    pd = max(disc, ACC_TARGET + 0.01)  # discordance cannot be below the effect size
    n_acc = math.ceil((Z_A * math.sqrt(pd) + Z_B * math.sqrt(pd - ACC_TARGET ** 2)) ** 2 / ACC_TARGET ** 2)
    return {"sd_log_seconds_paired": sd, "discordance": disc, "pairs_for_time": n_time,
            "blocks_for_time": math.ceil(n_time / 12), "pairs_for_acc": n_acc, "blocks_for_acc": math.ceil(n_acc / 12)}

def fmt_ci(ci, exp=False):
    if not ci: return "n/a"
    return f"[{math.exp(ci[0]):.2f}, {math.exp(ci[1]):.2f}]" if exp else f"[{ci[0]:+.2f}, {ci[1]:+.2f}]"

def main():
    through = int(sys.argv[sys.argv.index("--through-block") + 1]) if "--through-block" in sys.argv else None
    rows = load(through)
    prs = pairs(rows)
    blocks = sorted({r["block"] for r in rows})
    out = {"blocks": blocks, "trials": len(rows)}
    out["overall"] = summarize(prs, "all")
    out["by_class"] = {c: summarize([p for p in prs if p[1]["class"] == c], c) for c in ("DEFINITION", "REFERENCES", "TYPE-DRIVEN")}
    lsp = [r for r in rows if r["cond"] == "LSP"]
    out["lsp_usage_rate"] = mean([r["lsp_used"] for r in lsp])
    out["lsp_usage_by_class"] = {c: mean([r["lsp_used"] for r in lsp if r["class"] == c]) for c in ("DEFINITION", "REFERENCES", "TYPE-DRIVEN")}
    out["rg_arm_lsp_violations"] = sum(r["lsp_used"] for r in rows if r["cond"] == "RG")
    # Within the LSP arm: did the subjects that used the tool do better? (observational, not randomized)
    used = [r for r in lsp if r["lsp_used"]]; unused = [r for r in lsp if not r["lsp_used"]]
    out["lsp_arm_used_vs_not"] = {"used_n": len(used), "used_acc": mean([r["correct"] for r in used]),
                                  "unused_n": len(unused), "unused_acc": mean([r["correct"] for r in unused])}
    out["trace"] = []
    for k in blocks:
        s = summarize([p for p in prs if p[0][1] <= k], f"through block {k}")
        ci = s.get("logratio_ci"); aci = s.get("acc_ci")
        stop = bool(ci and aci and ((ci[0] > 0 or ci[1] < 0) and (aci[0] > 0 or aci[1] < 0) or (ci[1] - ci[0]) < 0.2))
        out["trace"].append({"through_block": k, "pairs": s["pairs"], "logratio": s["logratio"], "logratio_ci": ci,
                             "ci_width": (ci[1] - ci[0]) if ci else None, "acc_diff": s["acc_diff"], "acc_ci": aci,
                             "mcnemar_p": s["mcnemar_p"], "stop": stop})
    out["power_from_block1"] = power([p for p in prs if p[0][1] == 1])
    out["tokens_total"] = sum(r["tokens"] for r in rows)
    out["tokens_output_total"] = sum(r["tok_output"] for r in rows)
    out["per_task"] = {}
    for t in sorted({p[0][0] for p in prs}):
        tp = [p for p in prs if p[0][0] == t]
        out["per_task"][t] = {"acc_rg": mean([r["correct"] for _, r, _ in tp]), "acc_lsp": mean([l["correct"] for _, _, l in tp]),
                              "med_s_rg": st.median([r["seconds"] for _, r, _ in tp]), "med_s_lsp": st.median([l["seconds"] for _, _, l in tp]),
                              "lsp_used": mean([l["lsp_used"] for _, _, l in tp])}
    print(json.dumps(out, indent=1, default=lambda x: round(x, 4) if isinstance(x, float) else x))

if __name__ == "__main__": main()
