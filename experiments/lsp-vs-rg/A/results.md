Blocks: 1, 2, 3, 4; trials: 96; pairs: 48

| subset | pairs | acc RG | acc LSP | acc diff LSP-RG [95% CI] | discordant LSP-only/RG-only, McNemar | both correct | time ratio LSP/RG (both correct) [95% CI] | median s RG / LSP | tool-call ratio [CI] | token ratio [CI] | LSP used |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ALL | 48 | 0.96 | 1.00 | 0.04 [0.00, 0.10] | 2/0 p=0.500 | 46 | 0.76 [0.65, 0.89] | 16 / 11 | 0.71 [0.64, 0.80] | 0.95 [0.93, 0.96] | 1.00 |
| DEFINITION | 16 | 1.00 | 1.00 | 0.00 [0.00, 0.00] | 0/0 p=1.000 | 16 | 1.17 [0.95, 1.46] | 12 / 13 | 1.06 [0.88, 1.27] | 1.00 [1.00, 1.01] | 1.00 |
| REFERENCES | 16 | 0.88 | 1.00 | 0.13 [0.00, 0.31] | 2/0 p=0.500 | 14 | 0.57 [0.45, 0.72] | 22 / 11 | 0.62 [0.54, 0.71] | 0.87 [0.86, 0.89] | 1.00 |
| TYPE-DRIVEN | 16 | 1.00 | 1.00 | 0.00 [0.00, 0.00] | 0/0 p=1.000 | 16 | 0.64 [0.52, 0.80] | 17 / 9 | 0.55 [0.51, 0.59] | 0.97 [0.97, 0.98] | 1.00 |

Per task (all blocks):

| task | RG correct | LSP correct | median s RG | median s LSP | LSP used |
|---|---|---|---|---|---|
| D1 | 4/4 | 4/4 | 12 | 13 | 4/4 |
| D2 | 4/4 | 4/4 | 13 | 11 | 4/4 |
| D3 | 4/4 | 4/4 | 11 | 12 | 4/4 |
| D4 | 4/4 | 4/4 | 12 | 24 | 4/4 |
| R1 | 4/4 | 4/4 | 23 | 15 | 4/4 |
| R2 | 4/4 | 4/4 | 19 | 11 | 4/4 |
| R3 | 2/4 | 4/4 | 22 | 10 | 4/4 |
| R4 | 4/4 | 4/4 | 28 | 13 | 4/4 |
| T1 | 4/4 | 4/4 | 17 | 8 | 4/4 |
| T2 | 4/4 | 4/4 | 18 | 9 | 4/4 |
| T3 | 4/4 | 4/4 | 17 | 10 | 4/4 |
| T4 | 4/4 | 4/4 | 12 | 9 | 4/4 |

Power inputs: paired SD of log(seconds) (both correct) = 0.549; (all pairs) = 0.548; discordance rate = 0.042
Pairs needed for 80% power, alpha .05: time ratio 1.25 (|log| 0.223) -> 48 pairs (4 blocks); accuracy diff 0.15 with discordance 0.160 -> 54 pairs (5 blocks)
All-pairs time ratio (incl. wrong answers): 0.75 [0.64, 0.87]

STOP-RULE after block 4: logRatioCI [-0.426, -0.116] width 0.309 excludes0=true; accCI [0.000, 0.104] excludes0=false -> CONTINUE

Budget: 96 trials, 5102265 subagent tokens total, 24.0 subagent-minutes, 260 tool calls
