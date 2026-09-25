/// Drive the Fable compiler (Fable.Compiler.CodeServices) from a SageFs session.
///
/// Runs on the Cecil-renamed FCS fork 'Fable.FSharp.Compiler.Service' from vendor/, next to the
/// SDK FCS that the SageFs FsiHost itself uses. Cracked project options and InteractiveChecker are
/// kept warm per project, so a recompile after an edit takes milliseconds.
///
/// In the session:
///     Playground.fcsIdentity ();;          // "Fable.FSharp.Compiler.Service, Version=43.11.200.0, ..."
///     Playground.compileHello ();;         // JS of samples/Hello/Hello.fs
///     Playground.compileFile "<path to .fsproj>" "<path to .fs>";;
///     Playground.selfCheck () |> Playground.printChecks;;
///
/// A top-level module on purpose: SageFs rejects 'namespace' in evals.
module Playground

open System
open System.IO
open System.Diagnostics
open Fable
open Fable.Compiler.Util
open Fable.Compiler.ProjectCracker
open Fable.Transforms.State
open FSharp.Compiler.SourceCodeServices

/// Name the renamed fork must load under (see tools/Rename.fsx).
[<Literal>]
let RenamedFcsName = "Fable.FSharp.Compiler.Service"

/// Repository root: the directory holding versions.json above this file.
let repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith $"versions.json not found above {__SOURCE_DIRECTORY__}"
        elif File.Exists(IO.Path.Join(dir.FullName, "versions.json")) then dir.FullName
        else up dir.Parent
    up (DirectoryInfo __SOURCE_DIRECTORY__)

let private versionOf (key: string) =
    use doc = Text.Json.JsonDocument.Parse(File.ReadAllText(IO.Path.Join(repoRoot, "versions.json")))
    doc.RootElement.GetProperty(key).GetString()

// ---------------------------------------------------------------- FCS identity

/// Full name of the FCS assembly Fable's InteractiveChecker is bound to.
let fcsIdentity () = typeof<InteractiveChecker>.Assembly.FullName

/// The identity the renamed fork must have, from versions.json 'fcsFork'.
let expectedFcsIdentity () =
    $"""{RenamedFcsName}, Version={versionOf "fcsFork"}.0, Culture=neutral, PublicKeyToken=null"""

/// Every loaded FCS / Fable / FSharp.Core assembly: identity, load context and location.
let fcsReport () =
    let describe (a: Reflection.Assembly) =
        let alc = Runtime.Loader.AssemblyLoadContext.GetLoadContext a
        let location = if a.IsDynamic then "<dynamic>" else a.Location
        $"""{a.FullName} | ALC={(if isNull alc then "<null>" else alc.Name)} | {location}"""
    [ yield "InteractiveChecker -> " + describe typeof<InteractiveChecker>.Assembly
      for a in AppDomain.CurrentDomain.GetAssemblies() do
          let n = a.GetName().Name
          if n.Contains "Compiler.Service" || n.StartsWith "Fable." || n = "FSharp.Core" then
              yield "loaded: " + describe a ]
    |> String.concat "\n"

// ---------------------------------------------------------------- compiling

let private cliArgsFor (projFile: string) =
    let projDir = IO.Path.GetDirectoryName projFile
    { CliArgs.ProjectFile = projFile
      // The JS is not run here, so any fixed directory works (same trick as Fable's own tests).
      FableLibraryPath = Some(IO.Path.Join(projDir, "fable_modules", "fable-library-js"))
      RootDir = projDir
      Configuration = "Debug"
      OutDir = None
      IsWatch = false
      Precompile = false
      PrecompiledLib = None
      PrintAst = false
      SourceMaps = false
      SourceMapsRoot = None
      NoRestore = false
      NoCache = false
      NoGitignore = false
      NoParallelTypeCheck = false
      Exclude = [ "Fable.Core" ]
      Replace = Map.empty
      RunProcess = None
      CompilerOptions = CompilerOptionsHelper.Make()
      Verbosity = Verbosity.Silent }

let private pathResolver =
    { new PathResolver with
        member _.TryPrecompiledOutPath(_sourceDir, _relativePath) = None
        member _.GetOrAddDeduplicateTargetDir(_importDir, _addTargetDir) = "" }

/// A cracked project with its warm checker.
type WarmProject =
    { CliArgs: CliArgs
      Cracked: CrackerResponse
      Checker: InteractiveChecker }

let private projects = Collections.Concurrent.ConcurrentDictionary<string, WarmProject>()

/// Cracks the project (MSBuild design-time build) and creates its checker, once per project.
let warm (fsproj: string) =
    let fsproj = Path.normalizeFullPath fsproj
    projects.GetOrAdd(fsproj, fun p ->
        let cliArgs = cliArgsFor p
        let resolver: ProjectCrackerResolver = Fable.Compiler.MSBuildCrackerResolver()
        let cracked = CrackerOptions(cliArgs, false) |> getFullProjectOpts resolver
        { CliArgs = cliArgs
          Cracked = cracked
          Checker = InteractiveChecker.Create cracked.ProjectOptions })

/// Forget cracked projects (after editing an .fsproj).
let reset () = projects.Clear()

type Compiled =
    { /// Source file (normalized full path) -> JS.
      Js: Map<string, string>
      Logs: string[]
      ElapsedMs: float }

/// Type-checks the project and compiles `files` (default: every project source outside fable_modules) to JS.
/// Sources are re-read on every call, so edits are always picked up (CodeServices has no output cache).
let compileProjectFiles (fsproj: string) (files: string list option) =
    let sw = Stopwatch.StartNew()
    let w = warm fsproj
    let _, sourceReader =
        w.Cracked.ProjectOptions.SourceFiles
        |> Array.map Fable.Compiler.File
        |> Fable.Compiler.File.MakeSourceReader
    let files =
        match files with
        | Some fs -> fs |> List.map Path.normalizeFullPath
        | None ->
            w.Cracked.ProjectOptions.SourceFiles
            |> Array.filter (fun f -> not (f.Contains "fable_modules"))
            |> List.ofArray
    let result =
        async {
            let! typeChecked = Fable.Compiler.CodeServices.typeCheckProject sourceReader w.Checker w.CliArgs w.Cracked
            return!
                Fable.Compiler.CodeServices.compileMultipleFilesToJavaScript
                    pathResolver w.CliArgs w.Cracked typeChecked files
        }
        |> Async.RunSynchronously
    sw.Stop()
    { Js = result.CompiledFiles
      Logs = result.Logs |> Array.map (fun l -> $"{l.Severity} {l.Tag} {l.Message}")
      ElapsedMs = sw.Elapsed.TotalMilliseconds }

/// Compiles one source file of a project to JS.
let compileFile (fsproj: string) (file: string) =
    let file = Path.normalizeFullPath file
    let c = compileProjectFiles fsproj (Some [ file ])
    match c.Js |> Map.tryFind file with
    | Some js -> js
    | None -> failwithf "Fable produced no JS for %s. Logs:\n%s" file (String.concat "\n" c.Logs)

// ---------------------------------------------------------------- the Hello sample

let helloProject = IO.Path.Join(repoRoot, "samples", "Hello", "Hello.fsproj")
let helloSource = IO.Path.Join(repoRoot, "samples", "Hello", "Hello.fs")
let helloGolden = IO.Path.Join(repoRoot, "samples", "Hello", "Hello.expected.js")

let compileHello () = compileFile helloProject helloSource

/// Line endings and surrounding blank lines are not significant for the golden comparison.
let normalizeJs (js: string) = js.Replace("\r\n", "\n").Trim()

// ---------------------------------------------------------------- self-check

type Check = { Name: string; Passed: bool; Detail: string }

/// FCS identity + Hello-to-golden (cold, then warm). Used by tests/smoke and handy in a session.
let selfCheck () =
    let check name f =
        try
            let passed, detail = f ()
            { Name = name; Passed = passed; Detail = detail }
        with ex ->
            { Name = name; Passed = false; Detail = $"{ex.GetType().Name}: {ex.Message}" }
    let golden = lazy (normalizeJs (File.ReadAllText helloGolden))
    let helloVsGolden label =
        let c = compileProjectFiles helloProject (Some [ Path.normalizeFullPath helloSource ])
        let js = c.Js |> Map.tryFind (Path.normalizeFullPath helloSource) |> Option.defaultValue ""
        let errors = c.Logs |> Array.filter (fun l -> l.StartsWith "Error")
        let same = normalizeJs js = golden.Value
        same && errors.Length = 0,
        $"{label}: {c.ElapsedMs:F0} ms, {js.Length} chars, matches golden: {same}, error logs: {errors.Length}"
        + (if errors.Length > 0 then "\n" + String.concat "\n" errors else "")
    [ check "FCS identity" (fun () ->
          let actual, expected = fcsIdentity (), expectedFcsIdentity ()
          actual = expected, $"InteractiveChecker bound to '{actual}' (expected '{expected}')")
      check "Hello -> golden JS (cold)" (fun () -> helloVsGolden "cold")
      check "Hello -> golden JS (warm)" (fun () -> helloVsGolden "warm") ]

let printChecks (checks: Check list) =
    for c in checks do
        printfn "%s %s: %s" (if c.Passed then "PASS" else "FAIL") c.Name c.Detail
    checks |> List.forall _.Passed
