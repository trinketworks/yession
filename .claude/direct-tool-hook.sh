#!/usr/bin/env bash
# PreToolUse hook on Bash (.claude/settings.json): keeps agents on the task verbs.
#
# `dotnet`, `fable` and `esbuild` run directly skip the tool versions and PATH the task scripts
# pin, so a call can pass here and fail in CI. This denies one and says what to run instead.
# `dotnet fsi tasks.fsx <verb>` is the documented standalone route and passes.
#
# A script and not a tasks.fsx verb, because the harness runs it before EVERY Bash call and a
# call cannot wait for `dotnet fsi` to start. The fast path is one `jq` and string tests; nothing
# else forks unless a call is denied or escaped.
#
# FAILS OPEN. A missing `jq`, a payload that does not parse, an unset CLAUDE_PROJECT_DIR (no log
# to keep) or any surprise below ends in exit 0 and no output: the call goes on to the user's
# permission prompts. A deny is printed only after a command word has matched.
#
# WHERE `jq` COMES FROM. One place: the devshell's own profile, `<dotfile>/profile/bin/jq`, which
# devenv links on every entry (`pkgs.jq` is in devenv.nix's `packages`). `<dotfile>` is
# $DEVENV_DOTFILE, devenv's own record of where it put the profile, and the `.devenv` of the
# checkout this script is in when that is unset. The harness runs this hook with ITS environment,
# outside the devshell, so PATH says nothing about the devshell and is never searched: a `jq`
# found there would be whichever one the box happens to have. Reading `jq` off PATH is how this
# hook went blind on a box without one, silently.
#
# A checkout nobody has entered the devshell in has no profile yet, so the hook is blind there.
# `--check` says so: it prints one line when the profile has no `jq`, and nothing when it has.
# `.claude/setup.sh --hook` runs it at session start, so the model is told rather than left
# believing the hook is on. The path is spelled here and nowhere else.
#
# The escape: prefix the command with YESSION_DIRECT_TOOL="<reason>" (non-empty). It passes, and
# one line goes to .devenv/direct-tool.log (gitignored): timestamp, tool and first subcommand,
# reason. From the 3rd escape of the same `dotnet test` on, the model is told in
# `additionalContext` to ask the user for a tasks.fsx verb. No permission decision is set there,
# so nothing is bypassed.
#
# Matching is a word walk, not a shell parser. Per command word, after separators and prefixes:
#   1. Quoted strings become @Q<n>@ (so `echo "a; dotnet b"` and a quoted reason are not read as
#      commands), and everything from the first `<<` on is dropped (a heredoc body is text).
#   2. `&` `|` `;` `(` `)` and backticks split the rest into segments.
#   3. Per segment, skip `VAR=value`, `env`, `time`, `exec`, `sudo`, `nice`, `nohup`, `timeout N`,
#      `flock <lockfile>`, `devenv shell --`, and `then`/`do`/`if`... Their `-x` options go too.
#   4. The next word is the command word: `dotnet`, `fable`, `esbuild` (or a path ending there),
#      `npx fable`, `npx esbuild`. Arguments are never read: `rg dotnet` passes.
#
# Known gaps (all let a call through; none denies a call that is not direct):
#   * An option that takes a value hides the command: `sudo -u me dotnet`, `nice -n 5 dotnet`,
#     `flock -w 5 lock dotnet`, `env -u X dotnet`.
#   * A command inside a string or a substitution: `bash -c "dotnet build"`, `"$(dotnet x)"`,
#     `xargs dotnet`, `find -exec dotnet`, `nix develop -c dotnet`, an alias, `$DOTNET build`.
#   * A command after a heredoc's `<<` on the same line.
#   * An escaped quote inside a quoted string misreads that string. At worst it hides a command.

here=${BASH_SOURCE[0]}
[[ $here == */* ]] && here=${here%/*} || here=.
jq=${DEVENV_DOTFILE:-$here/../.devenv}/profile/bin/jq

if [[ ${1-} == --check ]]; then
  [[ -x $jq ]] || echo "direct-tool hook is OFF: no jq at $jq, so .claude/direct-tool-hook.sh lets every direct dotnet, fable and esbuild call through. devenv puts jq there when the devshell is entered: run \`devenv shell -- check\` (any task) once in this checkout. A fresh Claude Code container does it when its background setup ends."
  exit 0
fi

payload=$(cat)
[[ -x $jq ]] || exit 0
cmd=$("$jq" -r '.tool_input.command // empty' <<<"$payload" 2>/dev/null) || exit 0

# Fast path: nearly every call names none of the three.
[[ $cmd == *dotnet* || $cmd == *fable* || $cmd == *esbuild* ]] || exit 0

cmd=${cmd%%<<*}

# Quoted strings out, in order, so a word that was quoted cannot be mistaken for a command and a
# quoted reason survives. An empty pair (`VAR=""`) leaves nothing behind.
sq="'" dq='"'
quoted="^([^$dq$sq]*)($dq[^$dq]*$dq|$sq[^$sq]*$sq)(.*)\$"
quotes=()
masked= rest=$cmd
while [[ $rest =~ $quoted ]]; do
  masked+=${BASH_REMATCH[1]}
  text=${BASH_REMATCH[2]}
  rest=${BASH_REMATCH[3]}
  text=${text:1:${#text}-2}
  if [[ -n $text ]]; then
    masked+="@Q${#quotes[@]}@"
    quotes+=("$text")
  fi
done
masked+=$rest

segments=${masked//[&|;(\`]/$'\n'}
segments=${segments//)/ }

deny() {
  local reason
  reason="Direct \`$1\` blocked. The task scripts match tool versions and PATH to CI; a direct call can pass here and fail there.

Instead: \`devenv shell -- <task>\` (\`devenv shell -- help\` lists the verbs), or \`dotnet fsi tasks.fsx <verb>\` when there is no devenv.

If no verb covers what you need, run the same command prefixed with YESSION_DIRECT_TOOL=\"<why no verb covers this>\" (a non-empty reason). The reason is logged, and repeated escapes are how a missing verb gets added."
  "$jq" -n --arg reason "$reason" '{hookSpecificOutput: {hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: $reason}}'
  exit 0
}

escaped_keys=()
escaped_reasons=()
while IFS= read -r segment; do
  read -ra w <<<"$segment"
  n=${#w[@]} i=0 reason=
  while ((i < n)); do
    case ${w[i]} in
      YESSION_DIRECT_TOOL=?*)
        reason=${w[i]#*=}
        [[ $reason =~ ^@Q([0-9]+)@$ ]] && reason=${quotes[BASH_REMATCH[1]]}
        [[ -n ${reason//[[:space:]]/} ]] || reason= ;;
      [A-Za-z_]*=*) ;;
      env | time | exec | sudo | nice | nohup | then | do | else | elif | if | while | until | '!' | '{')
        while [[ ${w[i + 1]-} == -* ]]; do i=$((i + 1)); done ;;
      flock | timeout)
        while [[ ${w[i + 1]-} == -* ]]; do i=$((i + 1)); done
        i=$((i + 1)) ;;
      devenv)
        [[ ${w[i + 1]-} == shell ]] || break
        j=$((i + 2))
        while ((j < n)) && [[ ${w[j]} != -- ]]; do j=$((j + 1)); done
        ((j < n)) || break
        i=$j ;;
      *) break ;;
    esac
    i=$((i + 1))
  done
  ((i < n)) || continue

  key=
  tool=${w[i]##*/}
  case $tool in
    dotnet)
      sub=${w[i + 1]-}
      if [[ $sub == fsi ]]; then
        j=$((i + 2))
        while [[ ${w[j]-} == -* ]]; do j=$((j + 1)); done
        [[ ${w[j]-} == tasks.fsx || ${w[j]-} == */tasks.fsx ]] && continue
      fi
      key="dotnet${sub:+ $sub}" ;;
    fable | esbuild) key=$tool ;;
    npx)
      j=$((i + 1))
      while [[ ${w[j]-} == -* ]]; do j=$((j + 1)); done
      case ${w[j]-} in fable | esbuild) key=${w[j]} ;; esac ;;
  esac
  [[ -n $key ]] || continue

  [[ -n $reason ]] || deny "$key"
  escaped_keys+=("$key")
  escaped_reasons+=("$reason")
done <<<"$segments"

# Every direct call in this command is escaped. Without a checkout to keep the log in, that is all.
[[ -n ${CLAUDE_PROJECT_DIR-} && ${#escaped_keys[@]} -gt 0 ]] || exit 0
log=$CLAUDE_PROJECT_DIR/.devenv/direct-tool.log
mkdir -p "${log%/*}" 2>/dev/null

context=
for ((k = 0; k < ${#escaped_keys[@]}; k++)); do
  key=${escaped_keys[k]}
  prior=0
  if [[ -r $log ]]; then
    while IFS=$'\t' read -r _ logged _; do
      [[ $logged == "$key" ]] && prior=$((prior + 1))
    done <"$log"
  fi
  reason=${escaped_reasons[k]//[$'\t\n\r']/ }
  printf '%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$key" "$reason" >>"$log" 2>/dev/null
  count=$((prior + 1))
  if ((count >= 3)); then
    context+="\`$key\` has now been run directly under YESSION_DIRECT_TOOL $count times in this checkout (reasons: .devenv/direct-tool.log), so no task verb covers it. Tell the user which tasks.fsx verb would (name it, say what it would run) and ask whether to add it.
"
  fi
done
context=${context%$'\n'}

if [[ -n $context ]]; then
  "$jq" -n --arg context "$context" '{hookSpecificOutput: {hookEventName: "PreToolUse", additionalContext: $context}}'
fi
exit 0
