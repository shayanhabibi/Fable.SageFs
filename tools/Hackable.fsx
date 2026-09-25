// --hackable mode: build Fable's own compiler assemblies from source, unoptimized, against the
// renamed FCS fork in vendor/, and wire them into a SageFs session project (hackable/).
//
//   .work/fable/                         shallow clone of github.com/fable-compiler/Fable at the exact
//                                        commit the pinned Fable.Compiler package was built from
//                                        (read from the package's .nuspec <repository commit=...>)
//   hackable/Directory.Build.props       Optimize=false, fsc of the debug SDK, renamed FCS reference
//   hackable/fable/<Project>/*.fsproj    Fable.AST, Fable.Transforms, Fable.Transforms.Babel, Fable.Compiler:
//                                        same assembly names and source lists as upstream, sources linked
//                                        from .work/fable/src (edit them there)
//   hackable/Fable.SageFs.Hackable.props ProjectReferences + package refs, imported by the session project
//                                        and by tests/smoke (-p:FableSageFsMode=hackable)
//   hackable/Fable.SageFs.Hackable.fsproj + Playground.fs   the SageFs session project
//
// Why Optimize=false: SageFs hot reload detours methods, and fsc's cross-module inlining in an optimized
// build leaves no call sites for ~18% of Fable.Transforms' methods (lab spike transforms-detour-reach).
// Why the debug SDK's fsc: fsc from SDK 10.0.4xx emits invalid Debug IL for Fable
// (Fable2Babel.Util.transformCurriedApply@2454 -> InvalidProgramException); fsc 10.0.112 does not.
// The fsc is pinned through DotnetFscCompilerPath, so any SDK that builds these projects (including a
// SageFs 'hard_reset_fsi_session rebuild=true') compiles them with the known-good compiler.

#load "Common.fsx"

open System
open System.IO
open System.Xml.Linq
open Common

let hackableDir = repoRoot </> "hackable"
let fableDir = repoRoot </> ".work" </> "fable"
let hackableProps = hackableDir </> "Fable.SageFs.Hackable.props"
let hackableProject = hackableDir </> "Fable.SageFs.Hackable.fsproj"

let [<Literal>] FableRepoUrl = "https://github.com/fable-compiler/Fable.git"

/// Upstream projects to build from source, in dependency order (paths relative to the clone).
let upstreamProjects =
    [ "src/Fable.AST/Fable.AST.fsproj"
      "src/Fable.Transforms/Fable.Transforms.fsproj"
      "src/Fable.Transforms/Babel/Fable.Transforms.Babel.fsproj"
      "src/Fable.Compiler/Fable.Compiler.fsproj" ]

let private projectName (upstream: string) = Path.GetFileNameWithoutExtension upstream
let generatedProject (upstream: string) =
    let name = projectName upstream
    hackableDir </> "fable" </> name </> (name + ".fsproj")

let private git (dir: string) (args: string list) = runProcess dir "git" args false

// ---------------------------------------------------------------- source commit

/// The commit the pinned Fable.Compiler package was built from, from its .nuspec.
let sourceCommit (packageDir: string) =
    let nuspec = Directory.GetFiles(packageDir, "*.nuspec") |> Array.head
    let repo =
        XDocument.Load(nuspec).Descendants()
        |> Seq.tryFind (fun e -> e.Name.LocalName = "repository")
    let attr (name: string) = repo |> Option.bind (fun r -> r.Attribute(XName.Get name) |> Option.ofObj) |> Option.map _.Value
    match attr "commit" with
    | Some sha when sha.Length = 40 -> sha, (attr "url" |> Option.defaultValue FableRepoUrl)
    | _ ->
        fail $"{Path.GetFileName nuspec} has no <repository commit=...>, so the matching Fable source is unknown."
             "Pin a Fable.Compiler version whose package records its source commit (5.x does)."

// ---------------------------------------------------------------- clone

/// Shallow-fetches exactly `sha` into .work/fable. Keeps local edits: a checkout at the right commit is
/// never touched, and a dirty checkout at another commit is an error instead of being overwritten.
let ensureClone (sha: string) (url: string) =
    step $"Fable source {sha.Substring(0, 9)} (.work/fable)"
    if (git repoRoot [ "--version" ]).ExitCode <> 0 then
        fail "git is not installed or not on PATH." "Install git (https://git-scm.com/downloads) and rerun."
    let isRepo = Directory.Exists(fableDir </> ".git")
    let head () = (git fableDir [ "rev-parse"; "HEAD" ]).Output.Trim()
    let dirty () =
        (git fableDir [ "status"; "--porcelain"; "--untracked-files=no" ]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
    if isRepo && head () = sha then
        match dirty () with
        | [||] -> ok $"{rel fableDir} at {sha} (clean)"
        | files ->
            ok $"{rel fableDir} at {sha}"
            warn $"{files.Length} modified file(s) in {rel fableDir} (your edits are kept; 'git -C .work/fable checkout -- .' reverts them):"
            for f in files |> Array.truncate 10 do info $"    {f}"
    else
        if isRepo then
            let d = dirty ()
            if d.Length > 0 then
                fail $"{rel fableDir} is at {head ()} with {d.Length} modified file(s), but the pinned Fable.Compiler needs {sha}."
                     "Commit/stash or discard your edits in .work/fable (or delete the folder), then rerun."
        else
            Directory.CreateDirectory fableDir |> ignore
            let init = git fableDir [ "init"; "-q" ]
            if init.ExitCode <> 0 then fail $"git init in {rel fableDir} failed:\n{init.Output}" "Delete .work/fable and rerun."
            git fableDir [ "remote"; "add"; "origin"; url ] |> ignore
        // Some Fable test paths exceed Windows' 260-character limit; without this the checkout drops them.
        git fableDir [ "config"; "core.longpaths"; "true" ] |> ignore
        info $"fetching {url} {sha} (shallow)"
        let fetch = git fableDir [ "fetch"; "-q"; "--depth"; "1"; "origin"; sha ]
        if fetch.ExitCode <> 0 then
            fail $"git fetch of {sha} from {url} failed:\n{fetch.Output.Trim()}"
                 "Check your network connection. Deleting .work/fable and rerunning starts the clone over."
        let co = git fableDir [ "-c"; "advice.detachedHead=false"; "checkout"; "-q"; "--detach"; "FETCH_HEAD" ]
        if co.ExitCode <> 0 then
            fail $"git checkout {sha} failed:\n{co.Output.Trim()}" "Delete .work/fable and rerun."
        ok $"{rel fableDir} at {head ()}"

// ---------------------------------------------------------------- debug fsc

/// fsc.dll of the debug SDK (versions.json 'debugSdk'), located through 'dotnet --list-sdks'.
let debugFsc (debugSdk: string) =
    step $"fsc from SDK {debugSdk}"
    let r = dotnet repoRoot [ "--list-sdks" ] false
    let hint =
        $"Install .NET SDK {debugSdk} next to your current SDK: https://dotnet.microsoft.com/download/dotnet/10.0 "
        + $"(or: dotnet-install.sh --version {debugSdk} / dotnet-install.ps1 -Version {debugSdk}). "
        + "It is only used to compile the unoptimized Fable assemblies; fsc from SDK 10.0.4xx miscompiles them."
    let fsc =
        r.Output.Split('\n')
        |> Array.tryPick (fun line ->
            let line = line.Trim()
            let i = line.IndexOf " ["
            if i > 0 && line.Substring(0, i) = debugSdk && line.EndsWith "]" then
                Some(line.Substring(i + 2, line.Length - i - 3) </> debugSdk </> "FSharp" </> "fsc.dll")
            else None)
    match fsc with
    | Some p when File.Exists p -> ok p; p
    | Some p -> fail $"SDK {debugSdk} is listed but {p} does not exist." hint
    | None -> fail $"SDK {debugSdk} is not installed (dotnet --list-sdks)." hint

// ---------------------------------------------------------------- generated projects

type private Upstream =
    { Name: string
      Tfm: string
      Compile: (string * string) list // full path, link
      ProjectRefs: string list // upstream-relative paths
      Packages: (string * string) list }

let private readUpstream (upstream: string) =
    let path = fableDir </> upstream
    if not (File.Exists path) then
        fail $"{rel path} does not exist." "The Fable source layout changed; this setup knows Fable 5.x. Check versions.json 'fableCompiler'."
    let dir = Path.GetDirectoryName path
    let doc = XDocument.Load path
    let items (name: string) = doc.Descendants() |> Seq.filter (fun e -> e.Name.LocalName = name) |> List.ofSeq
    let attr (name: string) (e: XElement) = e.Attribute(XName.Get name) |> Option.ofObj |> Option.map _.Value
    let tfm =
        let fromFile (f: string) =
            if File.Exists f then
                XDocument.Load(f).Descendants() |> Seq.tryFind (fun e -> e.Name.LocalName = "TargetFramework") |> Option.map _.Value
            else None
        // The project itself, else the nearest Directory.Build.props that sets it (MSBuild's lookup order).
        let rec upwards (d: string) =
            if isNull d || not (d.StartsWith fableDir) then None
            else fromFile (d </> "Directory.Build.props") |> Option.orElse (upwards (Path.GetDirectoryName d))
        fromFile path |> Option.orElse (upwards dir)
        |> Option.defaultWith (fun () -> fail $"No TargetFramework for {rel path}." "The Fable source layout changed.")
    { Name = projectName upstream
      Tfm = tfm
      Compile =
        items "Compile" |> List.choose (attr "Include")
        |> List.map (fun inc -> Path.GetFullPath(dir </> inc), inc.Replace('\\', '/'))
      ProjectRefs =
        items "ProjectReference" |> List.choose (attr "Include")
        |> List.map (fun inc -> Path.GetRelativePath(fableDir, Path.GetFullPath(dir </> inc)).Replace('\\', '/'))
      Packages =
        items "PackageReference"
        |> List.choose (fun e -> match attr "Include" e, attr "Version" e with Some i, Some v -> Some(i, v) | _ -> None)
        |> List.filter (fun (id, _) -> not (id.StartsWith "EasyBuild")) }

let private write (path: string) (text: string) =
    if writeTextIfChanged path text then ok $"{rel path} written" else ok $"{rel path} unchanged"

/// Relative path from `fromDir` with forward slashes (MSBuild accepts them on every OS).
let private relFrom (fromDir: string) (path: string) = Path.GetRelativePath(fromDir, path).Replace('\\', '/')

let generate (versions: Versions) (fsc: string) (fableCompilerDeps: (string * string) list) (playgroundTemplate: string) =
    step "Generate hackable/ projects"
    let ups = upstreamProjects |> List.map readUpstream
    let vendor = repoRoot </> "vendor"
    let fscMsbuild = fsc.Replace('\\', '/')
    write (hackableDir </> "Directory.Build.props") (String.concat "\n" [
        "<Project>"
        "  <!-- Generated by setup.fsx (hackable mode). Do not edit; rerun setup. -->"
        "  <PropertyGroup>"
        "    <!-- Unoptimized, so fsc does not inline small helpers away and SageFs detours reach them. -->"
        "    <Optimize>false</Optimize>"
        "    <DebugSymbols>true</DebugSymbols>"
        "    <DebugType>embedded</DebugType>"
        "    <Tailcalls>false</Tailcalls>"
        "    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>"
        "    <GenerateDocumentationFile>false</GenerateDocumentationFile>"
        "    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>"
        "    <NoWarn>$(NoWarn);FS3370;FS0064;FS3536;NU1608;NU1603</NoWarn>"
        "    <!-- Same compiler flags as fable/src/Directory.Build.props and Fable.Transforms/Directory.Build.props. -->"
        "    <OtherFlags>$(OtherFlags) --test:GraphBasedChecking --test:ParallelOptimization --test:ParallelIlxGen --realsig+ --nowarn:3536</OtherFlags>"
        $"    <!-- fsc of SDK {versions.DebugSdk} (versions.json 'debugSdk'), whatever SDK runs the build: fsc 10.0.4xx emits invalid Debug IL for Fable. -->"
        $"    <DotnetFscCompilerPath>\"{fscMsbuild}\"</DotnetFscCompilerPath>"
        "  </PropertyGroup>"
        "  <ItemGroup>"
        $"    <PackageReference Include=\"FSharp.Core\" Version=\"{versions.FSharpCore}\" />"
        "    <!-- The Cecil-renamed FCS fork (tools/Rename.fsx), so these assemblies bind to it and never to the SDK's FCS. -->"
        $"    <Reference Include=\"Fable.FSharp.Compiler.Service\"><HintPath>$(MSBuildThisFileDirectory){relFrom hackableDir vendor}/Fable.FSharp.Compiler.Service.dll</HintPath></Reference>"
        "  </ItemGroup>"
        "</Project>"
        "" ])
    for up, upstream in List.zip ups upstreamProjects do
        let projFile = generatedProject upstream
        let projDir = Path.GetDirectoryName projFile
        let usesDepManager = up.Name = "Fable.Compiler"
        write projFile (String.concat "\n" [
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            $"  <!-- Generated by setup.fsx (hackable mode) from .work/fable/{upstream}. Edit the linked sources in .work/fable/src. -->"
            "  <PropertyGroup>"
            $"    <TargetFramework>{up.Tfm}</TargetFramework>"
            $"    <AssemblyName>{up.Name}</AssemblyName>"
            $"    <RootNamespace>{up.Name}</RootNamespace>"
            "  </PropertyGroup>"
            "  <ItemGroup>"
            for full, link in up.Compile do
                $"    <Compile Include=\"{relFrom projDir full}\" Link=\"{link}\" />"
            "  </ItemGroup>"
            "  <ItemGroup>"
            for r in up.ProjectRefs do
                $"    <ProjectReference Include=\"{relFrom projDir (generatedProject r)}\" />"
            for id, v in up.Packages do
                $"    <PackageReference Include=\"{id}\" Version=\"{v}\" />"
            if usesDepManager then
                $"    <Reference Include=\"FSharp.DependencyManager.Nuget\"><HintPath>{relFrom projDir vendor}/FSharp.DependencyManager.Nuget.dll</HintPath></Reference>"
            "  </ItemGroup>"
            "</Project>"
            "" ])
    // Props for consumers: the session project and tests/smoke in hackable mode.
    let packageRefs =
        fableCompilerDeps |> List.filter (fun (id, _) -> id <> "Fable.AST")
        |> List.map (fun (id, v) -> $"    <PackageReference Include=\"{id}\" Version=\"{v}\" />")
    write hackableProps (String.concat "\n" [
        "<Project>"
        $"  <!-- Generated by setup.fsx (hackable mode) (Fable.Compiler {versions.FableCompiler} sources). Do not edit; rerun setup. -->"
        "  <PropertyGroup>"
        "    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>"
        $"    <FableCompilerVersion>{versions.FableCompiler}</FableCompilerVersion>"
        "    <FableSageFsMode>hackable</FableSageFsMode>"
        "  </PropertyGroup>"
        "  <ItemGroup>"
        $"    <PackageReference Include=\"FSharp.Core\" Version=\"{versions.FSharpCore}\" />"
        yield! packageRefs
        "    <Reference Include=\"Fable.FSharp.Compiler.Service\"><HintPath>$(MSBuildThisFileDirectory)../vendor/Fable.FSharp.Compiler.Service.dll</HintPath></Reference>"
        "    <Reference Include=\"FSharp.DependencyManager.Nuget\"><HintPath>$(MSBuildThisFileDirectory)../vendor/FSharp.DependencyManager.Nuget.dll</HintPath></Reference>"
        "    <!-- Unoptimized Fable built from .work/fable/src: these are SageFs session projects, so SageFs can hot-patch them. -->"
        for upstream in upstreamProjects do
            $"    <ProjectReference Include=\"$(MSBuildThisFileDirectory){relFrom hackableDir (generatedProject upstream)}\" />"
        "  </ItemGroup>"
        "</Project>"
        "" ])
    write hackableProject (String.concat "\n" [
        "<Project Sdk=\"Microsoft.NET.Sdk\">"
        "  <!-- Generated by setup.fsx (hackable mode). Open a SageFs session on this project with working directory hackable/."
        "       Fable's sources are in .work/fable/src; the Fable projects under hackable/fable/ are session projects too. -->"
        "  <PropertyGroup>"
        "    <TargetFramework>net10.0</TargetFramework>"
        "    <OutputType>Library</OutputType>"
        "  </PropertyGroup>"
        "  <Import Project=\"Fable.SageFs.Hackable.props\" />"
        "  <ItemGroup>"
        "    <Compile Include=\"Playground.fs\" />"
        "  </ItemGroup>"
        "</Project>"
        "" ])
    let playground = hackableDir </> "Playground.fs"
    if not (File.Exists playground) then
        write playground playgroundTemplate
    elif File.ReadAllText(playground).Replace("\r\n", "\n") = playgroundTemplate.Replace("\r\n", "\n") then
        ok $"{rel playground} unchanged"
    else
        warn $"{rel playground} differs from templates/session/Playground.fs; keeping your edits (delete it to regenerate)."

/// Builds the session project (and with it the four unoptimized Fable projects).
let build () =
    step "Build hackable/ (unoptimized Fable from source; the first build takes a few minutes)"
    let sw = Diagnostics.Stopwatch.StartNew()
    let r = dotnet repoRoot [ "build"; hackableProject; "--nologo"; "-v:q"; "-clp:ErrorsOnly" ] false
    if r.ExitCode <> 0 then
        info (r.Output.Trim())
        fail $"{rel hackableProject} does not build."
             "See the errors above. If you edited Fable sources in .work/fable/src, fix or revert them ('git -C .work/fable checkout -- .')."
    ok $"{rel hackableProject} builds ({sw.Elapsed.TotalSeconds:F0} s)"
