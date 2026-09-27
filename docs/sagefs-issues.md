# SageFs issues met during Partas.Solid development

These are the problems we hit while using [SageFs](https://github.com/WillEhrendreich/SageFs) to build and use the
Partas.Solid workbench (`Workbench/Partas.Solid.Workbench.fsproj`), which runs Fable and the Partas.Solid plugin in
process.

**Sources**

- Claude Code transcripts for the Partas.Solid project, 2026-09-26. `e1080b56` covers workbench development (session
  `36d10b17`). `5a137065` and its subagents cover workbench use (session `5e90364a`). `349c61e2` and `b6563d39` made
  no SageFs calls. Times below are UTC from those transcripts.
- SageFs's own data: `get_daemon_status`, `list_sessions`, `get_friction_report`, `get_friction_summary`. We only read
  these and did not create, reset or stop any session.
- Existing notes: `Workbench/README.md` ("Why not SageFs hot reload") and `docs/hackable-status.md` ("Known issues").

**Version.** SageFs daemon `0.6.834.0` (read from `get_daemon_status` on 2026-09-27). The worker host was
`~/.SageFs/hosts/sdk-11.0.100-rc.1-<hash>/bin`. The session project targets `net10.0`.

**Verdict key.** *SageFs bug*: SageFs behaves wrongly. *SageFs limitation*: works as designed, but the design gets in
the way. *Not SageFs*: the cause is elsewhere, listed because it looked like a SageFs problem at the time.

## Summary

| # | Issue | Impact | Verdict |
| - | ----- | ------ | ------- |
| 1 | Hot reload does not patch plugin code, and gives no feedback | High | Limitation, plus a feedback bug |
| 2 | An unoptimized (Debug) plugin build throws `InvalidProgramException` | High | Probably not SageFs (fsc), but it blocks item 1 |
| 3 | Warm-up faulted on a wrong DLL probe path | High | SageFs bug |
| 4 | `hard_reset(rebuild)` is slow, and status and eval disagree during it | Medium | SageFs bug |
| 5 | A multi-statement eval runs partway after an error | Medium | Limitation, surprising |
| 6 | The "error in YOUR code (99%)" tip shows on runtime and library errors | Medium | SageFs bug (misleading hint) |
| 7 | Plugin stdout floods eval results | Medium | Limitation |
| 8 | `#load` of a project file fails; `namespace` rejected in evals | Medium | Limitation |
| 9 | Watcher reloads break later evals and cover only one directory | Medium | SageFs bug |
| 10 | Reply formats differ: HTTP vs MCP, success vs error | Low | SageFs bug |
| 11 | A range list comprehension fails to parse in eval | Low | SageFs bug |
| 12 | Sessions do not survive a daemon restart | Low | Limitation |
| 13 | In-session `FSharpChecker.Compile` rejects `--langversion:11` | Low | Not SageFs (FCS version skew) |
| 14 | No safe way for parallel agents to share one session | Low | Limitation, weak evidence |

---

## 1. Hot reload does not patch plugin code, and gives no feedback

- **Symptom.** Redefining a plugin function with `send_fsharp_code` compiles and reports success, but Fable's output
  does not change. No error or warning says the patch was declined.
- **Repro** (`e1080b56`, 12:00-12:23Z):
  1. Load the workbench session. The plugin DLL is referenced from the project.
  2. Redefine the internal `Partas.Solid.Baked.convertGettersToObject` in an eval. Output still contains
     `PARTAS_OTHERS`. Reflection shows `isPublic=false` (12:12).
  3. Redefine the public `Fable.AST.Fable.Expr.findAndDiscardElse`. It is not detoured either ("public reflect
     len: 0", 12:13:44).
  4. Compile the plugin sources into the session assembly instead of referencing the DLL. It is still not detoured
     (12:21:55).
  5. Only `Workbench.normalize`, a public function in the session's own project, was detoured (12:03:01).
- **Cause, as far as we can tell.** `SageFs.HostHarmony` refuses non-public modules. `SageFs.Core.dll` contains
  `"%s uses %s, not public, so it cannot be patched in place"`, `PatchIneffective`, `hotReloadDeclinedBindings` and
  `"Canary warning: native code unchanged"`. The plugin is almost all `internal`. The public function was probably
  inlined or called from already-compiled code, so a detour on it would not be reached.
- **Impact.** High. Hot reload was the reason for using SageFs on the plugin, and it does not reach the plugin. We
  found the reasons only by dumping strings from `SageFs.Core.dll` (12:22-12:23). About 20 minutes were lost.
- **Workaround.** `Workbench.reloadPlugin ()` rebuilds the plugin DLL and reloads it into Fable, in about 2.5 s with a
  warm checker. See `Workbench/README.md`, "Why not SageFs hot reload".
- **SageFs?** A limitation for internal code. The missing feedback is a bug: the declined or ineffective patch
  diagnostics exist in the binary but did not reach the eval reply. The eval result should say "binding X was not
  patched: not public" or "patch ineffective".

## 2. An unoptimized (Debug) plugin build throws `InvalidProgramException`

- **Symptom** (`e1080b56`, 12:05:37Z). Detours need an unoptimized build, so the workbench switched the plugin
  reference to `Configuration=Debug;Optimize=false;Tailcalls=false`. The session then loaded from shadow copy
  `sagefs-shadow-50544`, and the first eval failed with:

  ```
  Common Language Runtime detected an invalid program.
     at Partas.Solid.AST.AttributesAndProperties.|PropCollector|(...)
  ```

- **Repro.** Build the plugin Debug and unoptimized with SDK 10.0.4xx or 11 `fsc`, reference it from a session, and run
  any Fable compile that reaches `PropCollector`. Pinning `DotnetFscCompilerPath` to SDK 10.0.112's `fsc.dll`
  (12:06:49) was tried next.
- **Impact.** High, because it closes the only route (unoptimized build) that might have made item 1 work.
- **Workaround.** Keep the plugin optimized and use `Workbench.reloadPlugin` instead of detours.
- **SageFs?** Probably not. It looks like an `fsc` codegen bug for active patterns in Debug builds. SageFs did make it
  worse to diagnose, because its reply told us the error was in our submitted code (item 6).

## 3. Warm-up faulted on a wrong DLL probe path

- **Symptom** (`e1080b56`, 11:44:22Z-11:49:14Z). The session went `Faulted` during warm-up. The fault reason was a
  missing `Fable.AST` DLL that SageFs probed for under
  `C:\Users\shaya\RiderProjects\Partas.Solid\Workbench\`, the session's working directory. The project resolves it
  from the NuGet cache.
- **Repro.** Create a project session for a project whose `Fable.AST` comes from a package reference while another
  vendored copy of Fable (`Fable.Compiler` from a `vendor/` folder) is also referenced.
- **Impact.** High at the time: the session was unusable until it was reset.
- **Workaround.** Removing the vendor reference did not help (11:44:34, 11:49:23). A plain `hard_reset_fsi_session`
  without rebuild (11:45:12, 11:49:23) brought it to Ready. It then loaded `Fable.AST` 5.0.0 from the NuGet cache and
  `Fable.Compiler` 5.16.2 from `C:\Users\shaya\RiderProjects\Fable.SageFs\vendor`. That is a different clone from the
  `.workbench/` one, which is a second surprise.
- **SageFs?** Bug. Warm-up should resolve assemblies the way the build does, and the fault reason should name where it
  looked.

## 4. `hard_reset(rebuild)` is slow, and status and eval disagree during it

- **Symptom.**
  - Each rebuild took about 4.6-4.7 minutes to reach Ready: 12:06:49 → 12:11:35, 12:16:30 → 12:21:08,
    12:25:49 → 12:30:26 (`e1080b56`). A plain reset without rebuild took about 26 s (12:04:48 → 12:05:14).
  - After a rebuild at 11:48:31, `get_session_status` at 11:49:14 reported `Faulted` with the earlier warm-up fault
    reason, so the fault text looked stale.
  - `5a137065`, 17:20:01: after `hard_reset(rebuild=true)`, `get_session_status` right away reported `Ready`
    (evalCount 83, the old worker). `send_fsharp_code` at 17:20:13 then failed with `WarmingUp.` It worked at 17:20:17.
- **Repro.** Call `hard_reset_fsi_session` with `rebuild=true`, then call `get_session_status` and `send_fsharp_code`
  within a few seconds.
- **Impact.** Medium. The reply says "current worker keeps serving until the new build is ready", but an eval still
  hit a warming worker. There is no progress to poll, so we found the new worker by watching
  `%TEMP%\sagefs-shadow-*` with a shell loop.
- **Friction data.** `get_friction_report` for session `5e90364a` shows 48 events, one blocked `send_fsharp_code`
  with blocker `AffordanceMismatch`, and observed signals `observed.invalid-state-call` (Strong) and
  `observed.abandonment`. This matches the `WarmingUp.` eval. No explicit feedback was filed.
- **Workaround.** Retry after a few seconds. Avoid rebuild resets. Make the plugin reloadable from inside the session
  (`Workbench.reloadPlugin`).
- **SageFs?** Bug. Status should report the rebuild (for example `Rebuilding`, with the phase) until the swap, and an
  eval during the swap should queue or go to the old worker.

## 5. A multi-statement eval runs partway after an error

- **Symptom.** When one statement in a multi-statement `send_fsharp_code` fails, the statements before it have already
  run, and later ones fail with `Operation could not be completed due to an earlier error`. The reply shows an error,
  but side effects such as files written by `WriteAllText` have happened (`e1080b56`, 12:12:41, 12:13:14, 12:13:32;
  `5a137065`, 18:18:23, 18:19:45).
- **Impact.** Medium. It is easy to read the reply as "nothing happened" and rerun work that has already run.
- **Workaround.** One statement per eval, each ending in `;;`, and check each result.
- **SageFs?** A limitation of FSI semantics, but the reply should say which statements ran. The diagnostics do give
  the line and column.

## 6. The "error in YOUR code (99%)" tip shows on runtime and library errors

- **Symptom.** Every failed eval carries a tip saying the error is in the submitted code 99% of the time. We saw it on:
  - `FableError: Cannot find [temp/]fable-library-js ... Workbench\bin\Debug\net10.0` (11:50:20), a Fable config
    problem;
  - Fable errors about `AriaAttributes` (15:57:37), and plugin errors at 18:04:47 and 18:19:45;
  - the `InvalidProgramException` in item 2 (12:05:37);
  - `No case ThunkArguments` in a subagent (17:27:02).
- **Impact.** Medium. It points agents at the eval text when the fault is in a referenced assembly or the runtime.
- **SageFs?** Bug in the hint. The tip should be shown only for compile diagnostics, not for exceptions whose stack is
  outside the eval assembly.

## 7. Plugin stdout floods eval results

- **Symptom** (`e1080b56`, 11:54-12:31). The plugin's debug printing (`START MEMBER DECL!!!`, AST dumps) came back
  inside the eval reply, and the useful result was buried in it.
- **Impact.** Medium. We had to write results to files and read them separately.
- **Workaround.** Write output to a file from the eval. Turn off the plugin's debug flags.
- **SageFs?** Limitation. A way to cap, drop or separate captured stdout from the value would help.

## 8. `#load` of a project file fails; `namespace` rejected in evals

- **Symptom** (`e1080b56`, 11:50:51). `#load` of a source file from the project failed with
  `This declaration opens the namespace or module 'Fable.AST.Fable' through a partially qualified path` at (24,5),
  (58,11) and (60,8). The file compiles in the project. Evals that start with `namespace` are rejected, so code must
  sit in top-level modules (`Workbench/README.md`).
- **Impact.** Medium. Project code cannot be pasted or loaded as is.
- **Workaround.** Change the file and `hard_reset(rebuild)`. Make `Workbench.fs` a top-level module.
- **SageFs?** Limitation, mostly FSI's. It should be written up in the SageFs docs.

## 9. Watcher reloads break later evals and cover only one directory

- **Symptom.**
  - After the watcher reloaded `Playground.fs`, the next eval of a new definition failed with
    `Operation could not be completed due to an earlier error` (friction events #13, #14; `hackable-status.md`).
    Sending the same file with `send_fsharp_code` and `file_path` worked.
  - Replies carried `reloaded Workbench.fs` event notices (for example 12:16:35) after edits we meant only for the
    next rebuild.
  - The watcher covers only the session's working directory, so it misses edits to the plugin and bindings in
    sibling projects.
- **Impact.** Medium. The failed watcher reload leaves the session in a state where later evals fail, and nothing
  says why.
- **Workaround.** `Workbench.watchFiles` / `watchCases` / `watchSuite` own the watch loop for plugin, binding and case
  files.
- **SageFs?** Bug for the broken state after a reload. Limitation for the scope.

## 10. Reply formats differ: HTTP vs MCP, success vs error

- **Symptom.** Over HTTP an eval reply is plain text (`Result: ...`). Over MCP a success is JSON
  (`{"success":true,"result":...,"diagnostics":[]}`), but an error is plain text (`Error: Evaluation failed: ...`).
- **Impact.** Low. Scripts that parse replies need three cases.
- **SageFs?** Bug. One shape (JSON with `success: false` and diagnostics) for every transport and outcome.

## 11. A range list comprehension fails to parse in eval

- **Symptom.** `[ for _ in 1..3 -> ... ]` failed at (1,11) (`hackable-status.md`). `List.init` worked.
- **Impact.** Low.
- **Workaround.** `List.init` or `[1 .. 3]` with spaces.
- **SageFs?** Probably a bug in SageFs's statement splitting or preprocessing, since FSI accepts it.

## 12. Sessions do not survive a daemon restart

- **Symptom.** Session `36d10b17` from `e1080b56` was gone by 15:53 (`No active sessions`). The daemon PID changed
  from 91192 to 93676, and `5e90364a` was created fresh.
- **Impact.** Low. A new session means another warm-up (item 15 below).
- **SageFs?** Limitation. Worth documenting, or restoring sessions on start.

## 13. In-session `FSharpChecker.Compile` rejects `--langversion:11`

- **Symptom** (13:20:28). `(0,1)-(0,1) Unrecognized value '11' for --langversion use --langversion:? for complete list`.
  The FCS loaded in the session is older than the SDK 11 host that SageFs uses.
- **Impact.** Low. Pass a supported langversion.
- **SageFs?** Not a SageFs bug. It comes from mixing the session's FCS with the host SDK.

## 14. No safe way for parallel agents to share one session

- **Symptom** (`5a137065`, 17:17). Diagnosis agents were run one after another because they would have shared the one
  workbench session. `acquire_claim` exists but was not used, and there is no read-only or concurrent-eval mode.
- **Impact.** Low. It is slower, not broken.
- **SageFs?** Limitation, weak evidence.

## 15. Note: first evals are slow

The first `Workbench.checkAll ()` took 19058 ms (15:54:53), then 385 ms. After a reset it took 15707 ms and 12610 ms
(17:22). The first `reloadPlugin` took 4130 ms. That is mostly Fable's own warm-up, not SageFs, but together with items
4 and 12 it makes each reset cost seconds to minutes.

## Not SageFs

- A `rate-limited` error at 13:23:07 came from the Claude auto-mode classifier.
