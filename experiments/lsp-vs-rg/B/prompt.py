"""Build one subject prompt. Usage: python3 -I prompt.py <task_id> <RG|LSP>"""
import json, pathlib, sys
HERE = pathlib.Path(__import__("os").environ.get("EXP_DATA") or pathlib.Path(__file__).parent)
Q = "node /home/user/yession/experiments/lsp-vs-rg/B/lsp-query.mjs"
PREAMBLE = """You are answering one code-navigation question about the F# repository at /home/user/yession (working directory). This is read-only: do not edit or create files, and do not build, compile or run tests. Be efficient, and be exact.

QUESTION: {question}

{tools}

Finish with a single line `ANSWER: <answer>` in exactly the format the question asks for, and nothing after it."""
RG = """TOOLS: use only rg/grep (through Bash) and the Read tool. Do NOT use lsp-query.mjs or any language server. The repo's rule for finding F# symbols (from AGENTS.md):
"Never search for a bare name. F# reuses one identifier for a type, its module, a union case and record fields. Search for the declaration form, then scope to the owning file: rg '^\\s*(type|module|let|and)\\s+Foo\\b'. Each .fsproj lists <Compile Include> in order, and a symbol is declared in its file or an earlier one. Text search cannot find an inferred type. Follow the right-hand side to its declaration, or write the type you expect and let check tell you." (Here you may not run check.)"""
LSP = f"""TOOLS: you may use rg/grep (through Bash) and the Read tool, and also a warm F# language server (fsautocomplete; compiler-accurate, answers in milliseconds) through Bash. Files are repo-relative, lines 1-based, <symbol> is the identifier on that line (or a 1-based column):
  {Q} definition <file> <line> <symbol>   # declaration site of that exact symbol
  {Q} references <file> <line> <symbol>   # every use of that exact symbol, grouped by file (includes the declaration)
  {Q} hover <file> <line> <symbol>        # inferred type / signature
  {Q} symbols <name>                      # declarations matching a name, with kind and location"""
def build(task_id, cond):
    t = next(t for t in json.loads((HERE / "tasks.json").read_text())["tasks"] if t["id"] == task_id)
    return PREAMBLE.format(question=t["prompt"], tools=RG if cond == "RG" else LSP)
if __name__ == "__main__":
    print(build(sys.argv[1], sys.argv[2]))
