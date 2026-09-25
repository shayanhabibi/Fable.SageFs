# Fable.SageFs

Run the [Fable](https://github.com/fable-compiler/Fable) compiler live inside a
SageFs session, and compile F# to JavaScript from the REPL in
milliseconds. In hackable mode, you can also change Fable's own transform code from the REPL, and the next
compile uses your change without a rebuild.

A single script, `setup.fsx`, does all the setup: it downloads Fable, renames the compiler DLLs, and optionally
builds Fable from source. No binaries are committed to this repository.

| Mode | What you get | Status |
|------|--------------|--------|
| Default | Fable's compiler from NuGet (optimized), callable from a SageFs session | **Verified.** Windows 11, SageFs 0.6.830. The output JS matches standalone Fable byte for byte. A warm recompile of the sample takes under 25 ms. |
| `--hackable` | Fable built from source, unoptimized, loaded as SageFs projects so its functions can be hot-patched | **Verified, with known issues.** Windows 11, SageFs 0.6.830. The end-to-end hot-patch test passed: a patched Fable function changed the JS, and reverting it restored the JS. See [docs/hackable-status.md](docs/hackable-status.md). |

CI runs setup and the non-SageFs self-checks on Windows and Linux. Linux has not been tested by hand yet.

## The problem

Fable compiles with its own fork of FSharp.Compiler.Service (FCS) 43.11.200. SageFs evaluates your code in a
host built against the FCS of your .NET SDK (43.12.401 on SDK 10.0.401). Both assemblies have the same name and
public key token, so loading Fable's copy naively silently gives Fable the SDK's FCS instead, and Fable fails.

## Prerequisites

- .NET SDK **10.0.401** or a later 10.0.4xx patch (see `global.json`).
- SageFs, as a global tool. This repo was tested with **0.6.830**:

  ```sh
  dotnet tool install --global sagefs --version 0.6.830
  ```

  Setup warns if another version is installed, but it does not stop. SageFs is not needed to run setup or its
  self-check. You need it to use the session.
- For `--hackable` only: .NET SDK **10.0.112**, installed next to 10.0.401. It is only used to compile Fable;
  see [Why a pinned fsc](docs/hackable.md#why-a-pinned-fsc). Setup tells you what to install if it is missing.
  `git` must be on `PATH`.
- Network access to nuget.org and, for `--hackable`, github.com.

## Quickstart (default mode)

```sh
git clone <this repo> Fable.SageFs
cd Fable.SageFs
dotnet fsi setup.fsx
```

Setup finishes with a self-check. Every line must say `PASS`:

```text
PASS FCS identity: InteractiveChecker bound to 'Fable.FSharp.Compiler.Service, Version=43.11.200.0, ...'
PASS Fable assemblies bound to the renamed fork: ...
PASS Hello -> golden JS (cold): ...
PASS Hello -> golden JS (warm): warm: 9 ms, ...
```

Then open a SageFs session on `session/Fable.SageFs.Session.fsproj`, with working directory `session/`, and
evaluate:

```fsharp
Playground.fcsIdentity ();;     // "Fable.FSharp.Compiler.Service, Version=43.11.200.0, ..."
Playground.compileHello ();;    // the JS of samples/Hello/Hello.fs
Playground.compileFile "<path to your .fsproj>" "<path to a .fs file in it>";;
```

`Playground` keeps the cracked project and Fable's checker warm, so each project is cracked once. After that, a
recompile of an edited file takes milliseconds. Add your own code in extra files in `session/`. Setup keeps an
edited `Playground.fs`, but regenerates the `.fsproj`.

Other commands:

```sh
dotnet fsi setup.fsx --check          # self-check only (after a setup run)
dotnet fsi tests/fsi/FsiHost.fsx      # the same self-check inside plain dotnet fsi (SDK FCS already loaded)
dotnet fsi setup.fsx --help
```

Reruns are idempotent: files that are already up to date are not rewritten.

## Hackable mode

```sh
dotnet fsi setup.fsx --hackable                 # default mode + Fable from source + self-checks + hot-patch test
dotnet fsi setup.fsx --hackable --no-hotpatch   # the same, without the SageFs hot-patch test
```

Hackable mode clones Fable into `.work/fable` at the exact commit that the pinned `Fable.Compiler` package was
built from. It builds `Fable.AST`, `Fable.Transforms`, `Fable.Transforms.Babel` and `Fable.Compiler`
unoptimized, against the renamed FCS, and makes them projects of the SageFs session in `hackable/`. SageFs hot
reload can only detour methods in the session's projects, never in referenced DLLs, which is why this mode
exists.

The hot-patch test (`tests/hotpatch/HotPatch.fsx`) creates its own SageFs session, patches
`Fable2Babel.Util.getUnionCaseName`, checks that the JS changes on exactly one line, reverts the patch, and stops
the session. On the author's machine, a patch eval took about 270 ms and the recompile about 10 ms. The test
never starts, stops or restarts your SageFs daemon. If another session already uses `hackable/`, the test is
skipped, and setup still ends with "Done". Check its output for a `WARN` line.

Usage, patching rules and how to edit Fable's source: [docs/hackable.md](docs/hackable.md).
Status and known issues: [docs/hackable-status.md](docs/hackable-status.md).

## How it works

1. **Download.** Setup fetches `Fable.Compiler` at the version pinned in `versions.json`. The package already
   ships Fable's FCS fork, so default mode does not need to build Fable.
2. **Rename and retarget.** `tools/Rename.fsx` uses Mono.Cecil to rename the fork's assembly from
   `FSharp.Compiler.Service` to `Fable.FSharp.Compiler.Service` and clear its public key. It then rewrites the
   FCS references in `Fable.Transforms`, `Fable.Transforms.Babel` and `Fable.Compiler` to point to the new
   name. The F# metadata resource names stay as they are, because Fable's pickled signatures refer to them.
   The output is deterministic: reruns are byte-identical. It is re-read from disk and verified. A report is
   written to `vendor/rename-report.txt`.
3. **Session project.** Setup writes `vendor/Fable.SageFs.props`, which references the renamed DLLs and Fable's
   NuGet dependencies. It also generates `session/`, a small project that imports those props and contains
   `Playground.fs`.
4. **Self-check.** `tests/smoke` runs `Playground.selfCheck` in a plain process. It checks that the loaded FCS is
   `Fable.FSharp.Compiler.Service, 43.11.200.0`, and that `samples/Hello` compiles to
   `samples/Hello/Hello.expected.js`.

After the rename, the two compilers have different names and load side by side: the SDK's FCS for the REPL and
the fork for Fable.

Everything that setup produces goes into gitignored folders: `vendor/`, `session/`, `.work/` and `hackable/`.

## Limitations

- **What can't be hot-patched.**
  - Default mode: nothing in Fable. The NuGet assemblies are referenced DLLs, and SageFs does not detour DLLs.
  - Either mode: the FCS fork itself.
  - In hackable mode, a detour cannot replace these: `inline` functions, active patterns, static initialization
    tables (such as `replacedModules`), generic methods, and changes to a signature. Private helpers cannot be
    redefined from the REPL either. For these, edit the source and run `hard_reset_fsi_session rebuild=true`.
  - Patches must use nested modules instead of `namespace`, and qualify Fable types with `global.`.
  - SageFs's file watcher does not see edits to `.work/fable/src`. Send the edited definition as an eval
    instead, or rebuild.
- **Coupling to the SageFs version.** The design relies on how SageFs hosts user code (an FsiHost built
  against the SDK's FCS) and on how it detours methods. It was tested with SageFs 0.6.830 only. Setup warns
  about other versions, and the self-check in the session (`Playground.selfCheck ()`) shows whether a new
  version still works.
- **The Fable version is pinned.** `versions.json` pins `Fable.Compiler` 5.16.2, the FCS fork version it ships
  (43.11.200), `FSharp.Core`, the tested SageFs version and the SDK used for Debug builds. Changing
  `fableCompiler` may work for other 5.x releases that ship the fork. After changing it, update `fcsFork`,
  regenerate `samples/Hello/Hello.expected.js` if the JS changes, and update the package versions in
  `tests/fsi/FsiHost.fsx`. Only 5.16.2 has been tested.
- **The SDK is pinned.** `global.json` requires SDK 10.0.4xx. Hackable mode needs SDK 10.0.112, because fsc
  from 10.0.4xx emits invalid Debug IL for Fable.
- **Tested by hand only on Windows 11.** The scripts use only `System.IO` paths and no shell commands. CI covers
  the setup on Linux.

## Troubleshooting

| Symptom | Cause and fix |
|---------|---------------|
| `No .NET SDK matching global.json is installed.` | Install SDK 10.0.401 or a later 10.0.4xx patch. |
| `SDK 10.0.112 is not installed` (with `--hackable`, or as a note at the end of default setup) | Install SDK 10.0.112 next to 10.0.401. Only hackable mode needs it. |
| `sagefs X is installed; this repo was tested with 0.6.830` | This is a warning. Things will probably work. If they don't, report both versions. |
| `FAIL FCS identity: ... bound to 'FSharp.Compiler.Service, Version=43.12...'` | Fable got the SDK's FCS. Rerun `dotnet fsi setup.fsx`. In a session, make sure it was opened on `session/` (or `hackable/`) and that you did not `#r` Fable DLLs by hand. |
| `FAIL Hello -> golden JS` | Your Fable output differs from the golden file. If you changed `fableCompiler` in `versions.json`, regenerate the golden file. Otherwise, delete `vendor/` and `.work/` and rerun setup. |
| `session/Fable.SageFs.Session.fsproj does not build` | You probably edited `session/Playground.fs`. Delete it and rerun setup to restore the template. |
| Download failed | Check your connection to nuget.org, or restore the package into your NuGet cache and rerun. |
| Hackable: `git checkout ... failed`, or missing files in `.work/fable` on Windows | Long paths. Setup sets `core.longpaths` for new clones. For an older clone, delete `.work/fable` and rerun. |
| Hackable: the hot-patch test was skipped (exit 3) | No reachable SageFs daemon, or another session already uses `hackable/`. Stop that session yourself, or run the manual steps it prints. |
| Hackable: an edit in `.work/fable/src` has no effect | The file watcher does not cover that folder. Send the definition as an eval, or run `hard_reset_fsi_session rebuild=true`. |
| Hackable: `Operation could not be completed due to earlier error` after the watcher reloaded `hackable/Playground.fs` | This is a known issue. Send the block with `send_fsharp_code` (`file_path`, `eval_mode=block`). |
| `InvalidProgramException` in `Fable2Babel` | Fable was compiled with the fsc of SDK 10.0.4xx. Rerun `setup.fsx --hackable`: it pins fsc from SDK 10.0.112 in `hackable/Directory.Build.props`. |

## Repository layout

```text
setup.fsx                 entry point (default, --hackable, --check)
versions.json             pinned versions: Fable.Compiler, FCS fork, FSharp.Core, tested SageFs, debug SDK
global.json               SDK 10.0.4xx
tools/Rename.fsx          Cecil rename + retarget (also usable standalone)
tools/Hackable.fsx        clone, generate and build Fable from source
tools/Common.fsx          shared helpers (process, NuGet download, output)
templates/session/        Playground.fs, copied into session/ and hackable/
samples/Hello/            sample project + golden JS
tests/smoke/              self-check in a plain process (used by setup)
tests/fsi/FsiHost.fsx     self-check inside dotnet fsi, next to the SDK FCS
tests/hotpatch/           end-to-end SageFs hot-patch test (hackable mode)
docs/                     hackable mode docs and status
```

## Credits

This repository packages the results of a separate research effort, the "Fable.SageFs.Lab" research notes. That
work found the silent FCS clash, verified the rename, and measured which Fable methods SageFs can detour. The
notes are not public yet.

Built on [Fable](https://github.com/fable-compiler/Fable), the [F# compiler](https://github.com/dotnet/fsharp),
SageFs and [Mono.Cecil](https://github.com/jbevain/cecil).

## License

MIT, see [LICENSE](LICENSE). Setup downloads Fable and FCS binaries and modifies them on your machine. This
repository does not redistribute them. See [NOTICE.md](NOTICE.md).
