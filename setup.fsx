// Fable.SageFs setup.
//
//   dotnet fsi setup.fsx             default mode: Fable.Compiler (NuGet, pinned in versions.json)
//                                    -> renamed FCS fork in vendor/ -> session/ project -> self-check
//   dotnet fsi setup.fsx --check     self-check only (needs a previous setup run)
//   dotnet fsi setup.fsx --hackable  Debug Fable.Transforms from source as a session project (not implemented yet)
//
// Idempotent: reruns reuse downloads and rewrite nothing that is already up to date.
// Everything it produces is gitignored: vendor/, session/, .work/, bin/, obj/.

#load "tools/Common.fsx"
#load "tools/Rename.fsx"

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

/// The hackable build compiles Fable with the debug SDK through a nested global.json under .work/
/// (fsc from SDK 10.0.4xx emits invalid Debug IL for Fable). Default mode only reports whether it is available.
let checkDebugSdk (required: bool) =
    step $"Debug SDK {versions.DebugSdk} (for --hackable)"
    let dir = workDir </> "debug-sdk"
    let json = $"{{\n  \"sdk\": {{\n    \"version\": \"{versions.DebugSdk}\",\n    \"rollForward\": \"disable\"\n  }}\n}}\n"
    writeTextIfChanged (dir </> "global.json") json |> ignore
    let r = dotnet dir [ "--version" ] false
    if r.ExitCode = 0 && r.Output.Trim() = versions.DebugSdk then
        ok $"""nested {rel (dir </> "global.json")} selects dotnet {r.Output.Trim()}"""
    else
        let hint =
            $"Install .NET SDK {versions.DebugSdk}: https://dotnet.microsoft.com/download/dotnet/10.0 "
            + $"(or: dotnet-install.sh --version {versions.DebugSdk} / dotnet-install.ps1 -Version {versions.DebugSdk})."
        if required then fail $"SDK {versions.DebugSdk} is not installed." hint
        else warn $"SDK {versions.DebugSdk} is not installed; only needed for --hackable. {hint}"

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
    lib, ast, deps

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

let selfCheck () =
    step "Self-check (tests/smoke: FCS identity + Hello -> golden JS)"
    if not (File.Exists propsFile) then
        fail "vendor/ is not set up." "Run 'dotnet fsi setup.fsx' first."
    let build = dotnet repoRoot [ "build"; smokeProject; "--nologo"; "-v:q"; "-clp:ErrorsOnly" ] false
    if build.ExitCode <> 0 then
        info (build.Output.Trim())
        fail "tests/smoke does not build." "See the errors above."
    let r = dotnet repoRoot [ "run"; "--no-build"; "--project"; smokeProject ] true
    if r.ExitCode <> 0 then
        fail "Self-check failed." "See the FAIL lines above. Deleting vendor/ and .work/ and rerunning setup.fsx rebuilds everything."
    ok "self-check passed"

// ---------------------------------------------------------------- main

let usage () =
    printfn "usage: dotnet fsi setup.fsx [--check | --hackable | --help]"

let main (args: string list) =
    match args with
    | [ "--help" ] | [ "-h" ] -> usage (); 0
    | [ "--check" ] ->
        checkSdk ()
        selfCheck ()
        0
    | [ "--hackable" ] ->
        fail "--hackable is not implemented yet." "Use the default mode for now: dotnet fsi setup.fsx"
    | [] ->
        info $"Fable.SageFs setup in {repoRoot}"
        info $"pinned: Fable.Compiler {versions.FableCompiler}, FCS fork {versions.FcsFork}, FSharp.Core {versions.FSharpCore}, SageFs {versions.TestedSageFs}"
        checkSdk ()
        checkSageFs ()
        checkDebugSdk false
        let lib, ast, deps = fetchFable ()
        let dlls = renameIntoVendor lib ast
        writeProps dlls deps
        generateSession ()
        selfCheck ()
        step "Done"
        info $"Open a SageFs session on {rel sessionProject} (working directory {rel sessionDir}) and evaluate:"
        info "    Playground.fcsIdentity ();;"
        info "    Playground.compileHello ();;"
        0
    | other ->
        usage ()
        fail $"""Unknown arguments: {String.concat " " other}""" "See usage above."

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
