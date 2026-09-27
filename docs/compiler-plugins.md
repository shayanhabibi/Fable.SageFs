# Using Fable.SageFs from your own project

This guide is for projects that want Fable in their own SageFs session: a project with its own helpers, often
one that develops a Fable compiler plugin. It covers what the sample session in `session/` does not: matching the
`dotnet fable` CLI output, loading a plugin you are editing, and what SageFs hot reload can and cannot do there.

Everything here was learned building the [Partas.Solid](https://github.com/shayanhabibi/Partas.Solid) workbench,
which compiles Partas.Solid's snapshot tests and runtime fixtures with its Fable plugin inside SageFs. Its code
(`Workbench/Workbench.fs`, `Workbench/Partas.Solid.Workbench.fsproj`, `workbench.fsx`) is a complete working
example. The code below is taken from it. It targets Fable 5.13.0, so check names against your Fable version.

Problems with SageFs itself that we hit on the way are listed in [sagefs-issues.md](sagefs-issues.md).

## Setup

### Pin this repository

Clone Fable.SageFs into a gitignored folder of your project and check out a fixed commit. Don't track a branch:
the props file and `Playground.fs` can change between commits. The Partas.Solid script (`workbench.fsx`) does this:

1. Clones into `.workbench/Fable.SageFs` if the folder is missing.
2. If `HEAD` is not the pinned commit, it restores `versions.json`, refuses to go on if anything else is
   modified, fetches, and checks out the pinned commit.
3. Sets `versions.json` `fableCompiler` to the Fable version in the project's `.config/dotnet-tools.json`, so
   the session compiles with the same Fable as the CLI.
4. Deletes `vendor/Fable.SageFs.props`, runs setup, and checks that the props file exists and contains
   `<FableCompilerVersion>{version}</FableCompilerVersion>`. Setup leaves an unchanged props file alone, so
   deleting it first is the only way to know this run wrote it.

### Run setup with `--vendor-only`

```sh
dotnet fsi setup.fsx --vendor-only
```

This downloads Fable, renames the FCS fork into `vendor/` and writes `vendor/Fable.SageFs.props`. It skips the
sample session and the self-check, which your project does not need.

**Older Fable versions.** `templates/session/Playground.fs` targets Fable.Compiler 5.16.2. Older 5.x releases
lack some of its API (`CompileResult.Logs` and `CodeServices.getFSharpDiagnostics` are missing in 5.13), so the
sample session does not build there. `vendor/` does not depend on it. Plain `dotnet fsi setup.fsx` with an
untested `fableCompiler` warns, skips the sample and the self-check, and still exits 0. With 5.16.2 a sample
build failure is still an error. `--hackable` needs the sample, so it fails on other versions.

Setup against 5.13.0 was checked: the rename reports the same FCS fork (43.11.200), and the Partas.Solid
workbench compiles byte-identical output to the 5.13 CLI.

### The session project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <!-- SageFs loads references from the output folder. -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <FableSageFsRoot Condition="'$(FableSageFsRoot)' == ''">$(MSBuildThisFileDirectory)..\.workbench\Fable.SageFs</FableSageFsRoot>
  </PropertyGroup>
  <Import Project="$(FableSageFsRoot)\vendor\Fable.SageFs.props" />
  <ItemGroup>
    <Compile Include="Workbench.fs" />
  </ItemGroup>
  <ItemGroup>
    <!-- Only when a ProjectReference brings the Fable.AST package in too; see below. -->
    <Reference Remove="Fable.AST" />
    <ProjectReference Include="..\MyPlugin\MyPlugin.fsproj" AdditionalProperties="Configuration=Release" />
  </ItemGroup>
</Project>
```

| Setting | Why |
|---------|-----|
| `CopyLocalLockFileAssemblies=true` | SageFs loads a project's references from its output folder. Without it, NuGet dependencies are missing at run time. |
| `FableSageFsRoot` with a condition | Lets a user point at another clone without editing the project. |
| `<Reference Remove="Fable.AST" />` | Your plugin references the `Fable.AST` package, and the props file references `vendor/Fable.AST.dll`. Two items with the same assembly name made SageFs fault while warming the session. Keep one. They are the same Fable.AST version. |
| `Configuration=Release` on the plugin | See [Hot reload](#what-sagefs-hot-reload-can-reach). A Debug build of the plugin can throw `InvalidProgramException`. |

Use top-level modules (`module Workbench`), not `namespace`: SageFs rejects `namespace` in evals, so a file that
starts with one cannot be sent to the session.

After editing the `.fsproj` or adding a source file, run `hard_reset_fsi_session` with `rebuild=true`.

## Matching the CLI

To compare the session's output with the CLI's, or to write files the CLI would write, the session has to run
Fable with the same options. The Fable.Compiler API does not expose a single "compile like the CLI" call, so
the workbench calls the pipeline steps itself.

### Options

| CLI flag | `CliArgs` field |
|----------|-----------------|
| `-c Release` | `Configuration = "Release"`, and `CompilerOptionsHelper.Make(debugMode = false, ...)` |
| `--optimize` | `CompilerOptionsHelper.Make(optimizeFSharpAst = true, ...)` |
| `-e .fs.jsx` | `CompilerOptionsHelper.Make(fileExtension = ".fs.jsx", ...)` |
| `--exclude MyPlugin` | `Exclude = [ "MyPlugin" ]` |
| `--noCache` | `NoCache = true` |
| `-o .` | `OutDir = Some projDir` (no flag: `None`) |
| (always set by the CLI) | `CompilerOptionsHelper.Make(define = [ "FABLE_COMPILER"; "FABLE_COMPILER_5"; "FABLE_COMPILER_JAVASCRIPT" ], ...)` |
| (none) | `FableLibraryPath = Some <path>`; see [fable-library](#fable-library) |

Cracking with `NoCache = true` resets the project's `fable_modules`, as the CLI does. Cracking also builds
referenced projects. Both matter if something else compiles the same project; see [Locks](#locks).

### The pipeline

Crack once and keep the result and the checker. Then, for each compile:

1. `CodeServices.typeCheckProject sourceReader checker cliArgs cracked`. Build the source reader from the
   cracked source files each time, so edits on disk are seen.
2. `Project.From(...)` with the check results and your `getPlugin` (below).
3. For each file: a new `CompilerImpl`, then `FSharp2Fable.Compiler.transformFile` →
   `FableTransforms.transformFile` → `Fable2Babel.Compiler.transformFile` → `BabelPrinter.run writer`.

Give each file its own `CompilerImpl`: its log collection is not thread-safe, and files can compile in parallel.

These details make the output match the CLI byte for byte:

- **Empty output.** When `Fable2Babel` returns an empty program (a file with only erased types, say), the CLI
  writes no file. Printing it anyway gives a 4-byte file the CLI never writes.
- **The writer.** Implement `Fable.Transforms.Printer.Writer`. `MakeImportPath` must call
  `Imports.getImportPath resolver sourcePath targetPath projDir cliArgs.OutDir path`, and for a path ending in
  `.fs` call `File.changeExtensionButUseDefaultExtensionInFableModules`. That is what `Fable.Cli/Pipeline.fs`
  does. A writer that returns the path unchanged gives wrong import paths.
- **The path resolver.** One per project. `GetOrAddDeduplicateTargetDir` memoizes by lowercased directory, as in
  `Fable.Cli/Main.fs`:

  ```fsharp
  let pathResolver () =
      let targetDirs = Collections.Concurrent.ConcurrentDictionary<string, string>()
      { new PathResolver with
          member _.TryPrecompiledOutPath(_sourceDir, _relativePath) = None
          member _.GetOrAddDeduplicateTargetDir(importDir, addTargetDir) =
              targetDirs.GetOrAdd(importDir.ToLower(), fun _ -> targetDirs.Values |> addTargetDir) }
  ```

- **The output path.** With an `OutDir`, use `Imports.getTargetAbsolutePath resolver file projDir outDir`, then
  `File.changeExtensionButUseDefaultExtensionInFableModules`. That is `getOutPath` in `Fable.Cli/Main.fs`.
- **F# diagnostics.** Fable's logs don't include the F# checker's. Turn each `FSharpDiagnostic` into a
  `LogEntry` with tag `"FSHARP"` and add 1 to the columns, as the CLI does.

With all of this, the Partas.Solid workbench writes files that are byte-identical to `dotnet fable -o .` for
three projects (the largest has 38 output files), including the library sources that land under the output
folder.

### fable-library

`ProjectCracker.getFableLibraryPath` looks for `fable-library-js` next to the running process
(`AppContext.BaseDirectory`, searching upward). In a SageFs session, that is SageFs's host folder, so cracking
fails with "Cannot find fable-library-js". Set `FableLibraryPath` explicitly to where the CLI would put it, and
copy the library there from the Fable tool package:

```fsharp
let fableVersion = typeof<CrackerResponse>.Assembly.GetName().Version.ToString 3

let fableLibraryTarget (projDir: string) =
    Path.Join(projDir, "fable_modules", $"fable-library-js.{fableVersion}")

let copyFableLibrary (projDir: string) =
    let packages =
        match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
        | null | "" -> Path.Join(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
        | dir -> dir
    let source = Path.Join(packages, "fable", fableVersion, "fable-library-js")
    // ... copy every file under source to fableLibraryTarget projDir
```

`dotnet tool restore` puts the `fable` tool package into the NuGet cache. Copy after cracking, because a
`NoCache` crack resets `fable_modules`. The sample `Playground.fs` only sets the path and copies nothing. That is
enough to compile, because only the import paths depend on it, but not to run the output.

## Loading a plugin you are editing

Fable loads plugins through the `getPlugin` argument of `Project.From`. The default is
`Reflection.loadType cliArgs pluginRef`, which loads the DLL from disk once per process. To use a new build without
restarting the session, pass your own:

```fsharp
let getPlugin (cliArgs: CliArgs) (r: PluginRef) : System.Type =
    if Path.GetFileNameWithoutExtension r.DllPath = "MyPlugin" then
        plugin.GetTypes()   // the assembly from the latest reload
        |> Array.tryFind (fun t -> t.FullName.Replace("+", ".") = r.TypeFullName)
        |> Option.defaultWith (fun () -> failwith $"The plugin assembly has no type {r.TypeFullName}")
    else
        Reflection.loadType cliArgs r
```

`PluginRef.TypeFullName` uses `.` for nested types, and `Type.FullName` uses `+`. The `Replace` handles that.

To reload:

1. `dotnet build` the plugin project in Release.
2. Load the DLL, and its `.pdb` if present, from **bytes** (`MemoryStream`) into a new collectible
   `AssemblyLoadContext`. Loading from bytes keeps the file unlocked for the next build.
3. Unload the previous context.

```fsharp
type PluginLoadContext(name: string) =
    inherit System.Runtime.Loader.AssemblyLoadContext(name, isCollectible = true)
    override _.Load(_: System.Reflection.AssemblyName) : System.Reflection.Assembly = null
```

`Load` returns `null`, so the context resolves nothing itself, and the plugin gets `Fable.AST` and `FSharp.Core`
from the session's default context. That is required: if the plugin had its own copy of `Fable.AST`, its
`MemberDeclarationPluginAttribute` would be a different type from Fable's.

### The checker holds the plugin's metadata

The project that is compiled references the plugin (for its attributes), so the cracked options contain
`-r:<plugin>.dll`, and the checker reads the plugin's metadata from that file. Two problems follow:

- After a reload, the checker still sees the old public API: a new attribute or enum value is unknown.
- The checker keeps the DLL open, so the next build cannot overwrite it.

The workbench handles both by hashing the plugin's **public surface**: exported types, their base types,
interfaces and attributes, and public members, with the values of literals (enum cases). Skip types whose
names start with `__SageFs`, which SageFs adds to assemblies it loads. Then:

- The checker's `-r:` points to a copy of the DLL under `plugin-ref/<hash>/`, not to `bin/Release`.
- When a reload changes the hash, the next compile makes a new checker (`InteractiveChecker.Create` with the
  cracked options, `-r:` swapped for the new copy). The cracked options are kept, so there is no new crack.
- When the hash is unchanged, which is the case for any edit to `internal` code, the checker stays warm.
- Creating a checker deletes the copies of other hashes. Ignore `IOException` and `UnauthorizedAccessException`:
  another session may still have a copy open. It is deleted on a later run.

### Getting the AST a plugin receives

A `MemberDeclarationPluginAttribute` receives a member after `FableTransforms`, not after `FSharp2Fable`. To
inspect that AST without running the plugin, pass a `getPlugin` that returns a no-op plugin, and run the first
two steps:

```fsharp
type NoOpPlugin(_arg: obj) =
    inherit MemberDeclarationPluginAttribute()
    new() = NoOpPlugin(null)
    override _.FableMinimumVersion = "5.0"
    override _.Transform(_, _, decl) = decl
    override _.TransformCall(_, _, expr) = expr

// Project.From(..., getPlugin = fun _ -> typeof<NoOpPlugin>)
// FSharp2Fable.Compiler.transformFile com |> FableTransforms.transformFile com
```

Fable instantiates the plugin with the attribute's constructor arguments, so a `NoOpPlugin` needs a constructor
that takes one argument as well as the parameterless one.

## What SageFs hot reload can reach

SageFs hot-patches a function by detouring its compiled method to a redefinition sent as an eval. For a plugin,
this does not work:

- SageFs never detours methods in referenced DLLs. That includes a plugin referenced by the session project.
- SageFs declines to patch modules that are not public. Most plugin code is `internal`.
- An eval that redefined a *public* plugin function was not detoured either, both with the plugin referenced and
  with its sources compiled into the session project.
- Detours need an unoptimized build. With the fsc of SDK 10.0.4xx and 11, a Debug build of the Partas.Solid
  plugin threw `InvalidProgramException`. Fable has the same problem (see
  [Why a pinned fsc](hackable.md#why-a-pinned-fsc)).

So rebuild and reload instead. For Partas.Solid, the Release plugin build takes about 2 s, and the warm checker
makes the rest cheap. Hot reload does work for public functions in the session's own files.

For hot-patching Fable itself rather than a plugin, see [hackable.md](hackable.md).

## Watching files

A `FileSystemWatcher` gives an edit-save-see loop:

- Watch `*.fs` and handle `Changed`, `Created` and `Renamed`. Many editors save through a temporary file and a
  rename.
- Ignore paths under `bin`, `obj`, `fable_modules` and `node_modules`.
- Debounce: restart a `Threading.Timer` with `timer.Change(300, Timeout.Infinite)` on each event.
- Set a flag when a file in the plugin's folder changed, and reload the plugin before compiling.
- Catch and print exceptions in the callback. An unhandled one stops the watcher.
- Take a `lock` around compiles and reloads: the timer callback runs on a thread-pool thread, and an eval can
  start a compile at the same time.

Output printed from the watcher's thread may not appear in an MCP client's eval result. Write results to files
(the workbench rewrites the `.fs.jsx` open in the editor) rather than relying on `printfn`.

## Locks

If a CLI build, a test runner or another session can compile the same projects, share a cross-process lock
around cracking (it rewrites `fable_modules` and builds references), plugin builds and output writes. The
workbench uses the same protocol as Partas.Solid's Node test runner:

- A lock directory, created by building `{lockDir}.{pid}.tmp` with an `owner.json` (host, pid, token, start
  time) and moving it into place. `Directory.Move` fails if the target exists, so this is atomic. .NET's
  `CreateDirectory` is not.
- A heartbeat that touches `owner.json` every 5 s.
- A lock is stale when its owner process on the same host has exited, or `owner.json` is more than 2 minutes
  old. Remove a stale lock by moving it aside and deleting it.
- Release only if `owner.json` still has your token.

## Measured

Partas.Solid workbench, Fable 5.13.0, Windows 11, SageFs 0.6.834:

| What | Time |
|------|------|
| First compile of a project (crack + cold type-check) | about 10 s |
| Warm check of all 50 snapshot cases (one type-check, 50 files) | under 1 s |
| One file, warm | about 100 ms |
| Plugin reload, public surface unchanged | about 2 s (the build), then a warm compile |
| Plugin reload, public surface changed | about 3.5 s more for the new checker's type-check |
| Plugin save to updated `.fs.jsx` in the editor, with a watcher | about 3 s |

## Troubleshooting

| Symptom | Cause and fix |
|---------|---------------|
| `Cannot find fable-library-js ...` pointing into SageFs's host folder | Set `FableLibraryPath`, see [fable-library](#fable-library). |
| 4-byte output files the CLI doesn't write | Skip files whose `Fable2Babel` output is empty. |
| Import paths differ from the CLI's | The writer's `MakeImportPath` and the output path must use `Imports.getImportPath` and `getTargetAbsolutePath`, see [The pipeline](#the-pipeline). |
| A new plugin attribute or enum case is unknown after a reload | The checker still has the old plugin metadata. Recreate it when the public surface changes. |
| The plugin build fails because the DLL is in use | The checker or a load context holds it. Load from bytes, and point the checker at a copy. |
| The session faults on warm-up after adding the plugin reference | Two `Fable.AST` references. Add `<Reference Remove="Fable.AST" />`. |
| `The type 'CompileResult' does not define the field ... 'Logs'` during setup | The sample session targets 5.16.2. Use `--vendor-only`, or ignore it: setup warns and continues on other versions. |
| `InvalidProgramException` in plugin code | A Debug build. Build the plugin in Release. |
| A redefinition sent as an eval has no effect | It is in a referenced DLL or a non-public module. See [hot reload](#what-sagefs-hot-reload-can-reach). |
