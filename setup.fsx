// Fable.SageFs setup.
//
//   dotnet fsi setup.fsx             default mode: Fable.Compiler (NuGet, pinned in versions.json)
//                                    -> renamed FCS fork in vendor/ -> session/ project -> self-check
//   dotnet fsi setup.fsx --hackable  default mode, then Fable built from source (unoptimized, fsc of SDK
//                                    versions.json 'debugSdk') as SageFs session projects in hackable/
//                                    -> self-check -> SageFs hot-patch test (tests/hotpatch)
//   dotnet fsi setup.fsx --check [--hackable]   self-checks only (needs a previous setup run)
//   --no-hotpatch                    with --hackable: skip the SageFs hot-patch test
//
// Idempotent: reruns reuse downloads and rewrite nothing that is already up to date.
// Everything it produces is gitignored: vendor/, session/, .work/, bin/, obj/.

#load "tools/Common.fsx"
#load "tools/Rename.fsx"
#load "tools/Hackable.fsx"

open System
open System.IO
open Common

let versions = readVersions ()
let vendorDir = repoRoot </> "vendor"
let sessionDir = repoRoot </> "session"
let workDir = repoRoot </> ".work"
let propsFile = vendorDir </> "Fable.SageFs.props"
let sessionProject = sessionDir </> "Fable.SageFs.Session.fsproj"
let smokeProject = repoRoot </> "tests" </> "smoke" </> "Smoke.fsproj"
let tfm = "net10.0"

// ---------------------------------------------------------------- preflight

let checkSdk () =
    step "SDK"
    let r = dotnet repoRoot [ "--version" ] false
    let globalJson = File.ReadAllText(repoRoot </> "global.json")
    if r.ExitCode <> 0 then
        info (r.Output.Trim())
        fail "No .NET SDK matching global.json is installed." ("Install the .NET SDK required by global.json:\n" + globalJson + "\n    https://dotnet.microsoft.com/download/dotnet/10.0")
    ok $"dotnet {r.Output.Trim()} (global.json)"

let checkSageFs () =
    step "SageFs"
    let r = dotnet repoRoot [ "tool"; "list"; "--global" ] false
    let installed =
        r.Output.Split('\n')
        |> Array.map (fun l -> l.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries))
        |> Array.tryPick (fun cols -> if cols.Length >= 2 && cols.[0].Equals("sagefs", StringComparison.OrdinalIgnoreCase) then Some cols.[1] else None)
    match installed with
    | Some v when v = versions.TestedSageFs -> ok $"sagefs {v} (tested version)"
    | Some v -> warn $"sagefs {v} is installed; this repo was tested with {versions.TestedSageFs}. It will probably work; report problems with both versions."
    | None -> warn $"SageFs is not installed as a global tool. Install it with: dotnet tool install --global sagefs --version {versions.TestedSageFs}"

/// --hackable compiles Fable with the fsc of SDK versions.json 'debugSdk' (fsc from SDK 10.0.4xx emits
/// invalid Debug IL for Fable). Default mode only reports whether that SDK is available.
let checkDebugSdk () =
    try Hackable.debugFsc versions.DebugSdk |> ignore
    with SetupFailure(message, hint) -> warn $"{message} Only needed for --hackable. {hint}"

// ---------------------------------------------------------------- default mode

let fetchFable () =
    step $"Fable.Compiler {versions.FableCompiler} (NuGet)"
    let pkg = ensurePackage "Fable.Compiler" versions.FableCompiler
    let lib = pkg </> "lib" </> tfm
    if not (File.Exists(lib </> "FSharp.Compiler.Service.dll")) then
        fail $"Fable.Compiler {versions.FableCompiler} has no {tfm} FSharp.Compiler.Service.dll in {lib}."
             "This setup expects a Fable.Compiler package that ships Fable's FCS fork (5.x). Check versions.json 'fableCompiler'."
    let deps = nuspecDependencies pkg tfm
    if deps.IsEmpty then fail $"No {tfm} dependencies in the Fable.Compiler nuspec." "Check versions.json 'fableCompiler'."
    let astVersion =
        deps |> List.tryFind (fun (id, _) -> id = "Fable.AST") |> Option.map snd
        |> Option.defaultWith (fun () -> fail "Fable.Compiler does not depend on Fable.AST." "Check versions.json 'fableCompiler'.")
    let ast = ensurePackage "Fable.AST" astVersion </> "lib" </> "netstandard2.0" </> "Fable.AST.dll"
    for (id, v) in deps do
        info $"dependency {id} {v}"
    pkg, lib, ast, deps

let renameIntoVendor (lib: string) (ast: string) =
    step "Rename FCS fork into vendor/"
    let inputs = Rename.fableInputs lib ast
    let o = Rename.run inputs vendorDir versions.FcsFork
    // Anything else in vendor/ is left over from an older layout.
    let expected =
        set ((inputs |> List.map _.OutputName) @ [ "rename-report.txt"; Path.GetFileName propsFile ])
    for f in Directory.GetFiles vendorDir do
        if not (expected.Contains(Path.GetFileName f)) then
            info $"removing stale {rel f}"
            File.Delete f
    ok $"{o.RenamedFcs}"
    ok $"""retargeted: {o.Retargeted |> List.map fst |> List.distinct |> String.concat ", "}"""
    ok $"""vendor/: {o.Written.Length} written, {o.Unchanged.Length} unchanged (report: {rel (vendorDir </> "rename-report.txt")})"""
    inputs |> List.map _.OutputName

let writeProps (dlls: string list) (deps: (string * string) list) =
    step "vendor/Fable.SageFs.props"
    let packageRefs =
        deps
        |> List.filter (fun (id, _) -> id <> "Fable.AST") // comes renamed-safe from vendor/
        |> List.map (fun (id, v) -> $"    <PackageReference Include=\"{id}\" Version=\"{v}\" />")
    let refs =
        dlls |> List.map (fun dll ->
            $"    <Reference Include=\"{Path.GetFileNameWithoutExtension dll}\"><HintPath>$(MSBuildThisFileDirectory){dll}</HintPath></Reference>")
    let text =
        String.concat "\n" [
            "<Project>"
            $"  <!-- Generated by setup.fsx from Fable.Compiler {versions.FableCompiler}. Do not edit; rerun setup.fsx. -->"
            "  <PropertyGroup>"
            "    <!-- FSharp.Core from NuGet (versions.json 'fsharpCore'), never the one inside the Fable.Compiler package. -->"
            "    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>"
            $"    <FableCompilerVersion>{versions.FableCompiler}</FableCompilerVersion>"
            "  </PropertyGroup>"
            "  <ItemGroup>"
            $"    <PackageReference Include=\"FSharp.Core\" Version=\"{versions.FSharpCore}\" />"
            yield! packageRefs
            "  </ItemGroup>"
            "  <ItemGroup>"
            "    <!-- Cecil-renamed FCS fork and the Fable assemblies retargeted to it (tools/Rename.fsx). -->"
            yield! refs
            "  </ItemGroup>"
            "</Project>"
            "" ]
    if writeTextIfChanged propsFile text then ok $"{rel propsFile} written" else ok $"{rel propsFile} unchanged"

let generateSession () =
    step "Session project (session/)"
    let fsproj =
        String.concat "\n" [
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            "  <!-- Generated by setup.fsx. Open a SageFs session on this project with working directory session/."
            "       Regenerated on every setup run; put your own code in extra files (setup keeps Playground.fs if you edited it). -->"
            "  <PropertyGroup>"
            $"    <TargetFramework>{tfm}</TargetFramework>"
            "    <OutputType>Library</OutputType>"
            "  </PropertyGroup>"
            "  <Import Project=\"../vendor/Fable.SageFs.props\" />"
            "  <ItemGroup>"
            "    <Compile Include=\"Playground.fs\" />"
            "  </ItemGroup>"
            "</Project>"
            "" ]
    if writeTextIfChanged sessionProject fsproj then ok $"{rel sessionProject} written" else ok $"{rel sessionProject} unchanged"
    let template = File.ReadAllText(repoRoot </> "templates" </> "session" </> "Playground.fs")
    let playground = sessionDir </> "Playground.fs"
    if not (File.Exists playground) then
        writeTextIfChanged playground template |> ignore
        ok $"{rel playground} written"
    elif File.ReadAllText(playground).Replace("\r\n", "\n") = template.Replace("\r\n", "\n") then
        ok $"{rel playground} unchanged"
    else
        warn $"{rel playground} differs from templates/session/Playground.fs; keeping your edits (delete it to regenerate)."
    step "Build session project"
    let r = dotnet repoRoot [ "build"; sessionProject; "--nologo"; "-v:q"; "-clp:ErrorsOnly" ] false
    if r.ExitCode <> 0 then
        info (r.Output.Trim())
        fail $"{rel sessionProject} does not build." "See the errors above. If you edited session/Playground.fs, delete it and rerun to restore the template."
    ok $"{rel sessionProject} builds"

// ---------------------------------------------------------------- self-check

type Mode = Default | HackableMode

let selfCheck (mode: Mode) =
    let props, modeArgs, runArgs, label =
        match mode with
        | Default -> propsFile, [], [], "default: NuGet Fable in vendor/"
        | HackableMode -> Hackable.hackableProps, [ "-p:FableSageFsMode=hackable" ], [ "--"; "--expect-unoptimized" ], "hackable: unoptimized Fable from .work/fable"
    step $"Self-check, {label} (tests/smoke: FCS binding + Hello -> golden JS)"
    if not (File.Exists props) then
        fail $"{rel props} does not exist." (if mode = Default then "Run 'dotnet fsi setup.fsx' first." else "Run 'dotnet fsi setup.fsx --hackable' first.")
    let build = dotnet repoRoot ([ "build"; smokeProject; "--nologo"; "-v:q"; "-clp:ErrorsOnly" ] @ modeArgs) false
    if build.ExitCode <> 0 then
        info (build.Output.Trim())
        fail "tests/smoke does not build." "See the errors above."
    let r = dotnet repoRoot ([ "run"; "--no-build"; "--project"; smokeProject ] @ modeArgs @ runArgs) true
    if r.ExitCode <> 0 then
        fail "Self-check failed." "See the FAIL lines above. Deleting vendor/ and .work/ and rerunning setup.fsx rebuilds everything."
    ok "self-check passed"

/// The end-to-end SageFs hot-patch test (tests/hotpatch/HotPatch.fsx) in a child 'dotnet fsi'.
let hotPatchTest () =
    step "Hot-patch test through SageFs (tests/hotpatch)"
    let r = dotnet repoRoot [ "fsi"; repoRoot </> "tests" </> "hotpatch" </> "HotPatch.fsx" ] true
    match r.ExitCode with
    | 0 -> ok "hot-patch test passed"
    | 3 -> warn "hot-patch test skipped (see above); run it later with: dotnet fsi tests/hotpatch/HotPatch.fsx"
    | _ -> fail "Hot-patch test failed." "See the output above and docs/hackable.md."

// ---------------------------------------------------------------- main

let usage () =
    printfn "usage: dotnet fsi setup.fsx [--hackable [--no-hotpatch]] | --check [--hackable] | --help"

let defaultMode () =
    info $"Fable.SageFs setup in {repoRoot}"
    info $"pinned: Fable.Compiler {versions.FableCompiler}, FCS fork {versions.FcsFork}, FSharp.Core {versions.FSharpCore}, SageFs {versions.TestedSageFs}, debug SDK {versions.DebugSdk}"
    checkSdk ()
    checkSageFs ()
    let pkg, lib, ast, deps = fetchFable ()
    let dlls = renameIntoVendor lib ast
    writeProps dlls deps
    generateSession ()
    selfCheck Default
    pkg, deps

let hackableMode (hotpatch: bool) =
    let pkg, deps = defaultMode ()
    let fsc = Hackable.debugFsc versions.DebugSdk
    let sha, url = Hackable.sourceCommit pkg
    Hackable.ensureClone sha url
    let template = File.ReadAllText(repoRoot </> "templates" </> "session" </> "Playground.fs")
    Hackable.generate versions fsc deps template
    Hackable.build ()
    selfCheck HackableMode
    if hotpatch then hotPatchTest ()

let main (args: string list) =
    let has (flag: string) = List.contains flag args
    let known = set [ "--help"; "-h"; "--check"; "--hackable"; "--no-hotpatch" ]
    match args |> List.filter (known.Contains >> not) with
    | [] -> ()
    | other ->
        usage ()
        fail $"""Unknown arguments: {String.concat " " other}""" "See usage above."
    if has "--help" || has "-h" then
        usage ()
    elif has "--check" then
        checkSdk ()
        selfCheck Default
        if has "--hackable" then
            selfCheck HackableMode
            if not (has "--no-hotpatch") then hotPatchTest ()
    elif has "--hackable" then
        hackableMode (not (has "--no-hotpatch"))
        step "Done"
        info $"Open a SageFs session on {rel Hackable.hackableProject} (working directory {rel Hackable.hackableDir})."
        info "Edit Fable in .work/fable/src (or redefine functions with send_fsharp_code, nested modules instead of"
        info "'namespace'), then evaluate  Playground.compileHello ();;  -- see docs/hackable.md."
    else
        defaultMode () |> ignore
        checkDebugSdk () // optional: only reports whether --hackable could run
        step "Done"
        info $"Open a SageFs session on {rel sessionProject} (working directory {rel sessionDir}) and evaluate:"
        info "    Playground.fcsIdentity ();;"
        info "    Playground.compileHello ();;"
    0

let exitCode =
    try
        main (fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray)
    with
    | SetupFailure(message, hint) ->
        error message
        for line in hint.Split('\n') do
            info line
        1

exit exitCode
