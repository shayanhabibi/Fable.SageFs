# Hackable mode: status

Status as of 2026-09-25, with SageFs 0.6.830, Fable.Compiler 5.16.2 (source commit 42a9d6cdb), the FCS fork
43.11.200, and the fsc of SDK 10.0.112. Tested on Windows 11.

## Gate: passed

`dotnet fsi setup.fsx --hackable` ran end to end, including `tests/hotpatch/HotPatch.fsx` against a real SageFs
session. A rerun was idempotent: every generated file was reported `unchanged`.

| Step | Result |
|------|--------|
| 1. Session on `hackable/Fable.SageFs.Hackable.fsproj` | Ready in about 8 s. Loads Hackable, Fable.AST, Fable.Transforms, Fable.Transforms.Babel and Fable.Compiler. |
| 2. Fable in the session | All four assemblies are unoptimized and reference `Fable.FSharp.Compiler.Service, Version=43.11.200.0`. |
| 3. Baseline | `Playground.compileHello ()` equals `samples/Hello/Hello.expected.js`. |
| 4. Hot patch | Redefined `Fable2Babel.Util.getUnionCaseName` via `send_fsharp_code`. The JS changed on exactly line 14: `return ["Circle", "Rect"];` became `return ["HOTPATCH:Circle", "HOTPATCH:Rect"];`. The patch eval took 270 ms, the compile 10 ms, and the edit to JS about 360 ms including MCP round-trips. |
| 5. Revert | Sending the original body made the JS byte-identical to the baseline again. No rebuild or reset was needed. |

## Known issues and limitations

- **Source-file edits in `.work/fable/src` are not hot-reloaded.** SageFs's file watcher covers only the session's
  working directory (`hackable/`). I edited `getUnionCaseName` in `.work/fable/src/Fable.Transforms/Babel/Fable2Babel.fs`
  and the next compile was unchanged. `Playground.compileHello () |> fun js -> (js = jsA, js.Contains "HOTPATCH")`
  returned `(true, false)`. There are two workarounds:
  - Send the edited definition as an eval.
  - Run `hard_reset_fsi_session rebuild=true`. This path has not been timed.
- **A watcher reload of `hackable/Playground.fs` broke the new definition.** I appended
  `let watcherProbe () = "v1"` to `hackable/Playground.fs`. SageFs then logged `reloaded Playground.fs`, but
  `Playground.watcherProbe ();;` failed twice (events #13 and #14) with:
  `Evaluation failed: Exception: Operation could not be completed due to earlier error`.
  Sending the same line with `send_fsharp_code` (`file_path` = `hackable/Playground.fs`, `eval_mode=block`) worked:
  `val watcherProbe: unit -> string`. I did not investigate the reload failure further. The file was restored.
- **The first patch can be slow.** In the first manual run, the first patch eval took 10.3 s, and edit to JS took
  19.9 s. That session had already run several compiles. The automated run in a fresh session took 270 ms. Repeated
  patches were fast (28 ms eval, 6 ms compile).
- **SageFs friction.** `[ for _ in 1..3 -> ... ]` sent through `send_fsharp_code` failed at (1,11). `List.init 3 ...`
  worked. The test avoids range expressions and the `fsi` object in code it sends to the session.
- **The hot-patch test does not share sessions.** If another session already uses `hackable/`, the test exits 3
  (skipped) instead of creating a duplicate or touching that session.
- **The test uses the HTTP endpoint.** It talks to the SageFs MCP endpoint at `http://localhost:<McpPort>/`, with
  the port read from `~/.SageFs/daemon-info.json`. Over HTTP, `send_fsharp_code` returns plain text
  (`Result: ...`), not the JSON that stdio clients get. The test therefore does not rely on the reply format: it
  checks files that the evals write to `.work/hotpatch/`.
- **Only tested on Windows.** The scripts use only `System.IO` paths and no shell.
