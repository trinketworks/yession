#!/usr/bin/env bash
# Cold-start measurement: start the daemon and time first definition, first workspace-wide
# references, and a warm repeat. Run with no daemon running. FSAC=<path to fsautocomplete>.
cd /home/user/yession
uptime
T0=$(date +%s.%N)
(FSAC=${FSAC:?path to fsautocomplete} FSAC_PATH_PREFIX=/home/user/yession/.devenv/profile/bin ROOT=/home/user/yession LSP_LOG=${LSP_LOG:-/tmp/fsac-queries.jsonl} setsid nohup node experiments/lsp-vs-rg/B/lsp-daemon.mjs > /tmp/fsac-daemon.log 2>&1 &)
sleep 0.5
Q="node experiments/lsp-vs-rg/B/lsp-query.mjs"
$Q definition tests/Yession.Tests/Browser.fs 922 Refusal >/dev/null; T1=$(date +%s.%N)
$Q references src/Yession.Domain/Identity.fs 57 SessionId | head -1; T2=$(date +%s.%N)
$Q references src/Yession.Domain/Identity.fs 57 SessionId | head -1; T3=$(date +%s.%N)
echo "first-def $(echo "$T1-$T0"|bc) first-refs $(echo "$T2-$T1"|bc) warm-refs $(echo "$T3-$T2"|bc)"
ps -o rss=,comm= -C fsautocomplete; ps -eo rss=,comm= | sort -rn | head -3
