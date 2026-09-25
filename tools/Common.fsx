// Shared helpers for setup.fsx and tools/*.fsx: logging, failures with hints, processes,
// versions.json, NuGet package download/extraction and "write only if changed" file output.
// Cross-platform: only System.IO paths, no shell.

open System
open System.IO
open System.Diagnostics
open System.Text.Json

// ---------------------------------------------------------------- logging

/// Raised for expected, user-fixable failures. `Hint` tells the user what to do next.
exception SetupFailure of Message: string * Hint: string

let fail message hint = raise (SetupFailure(message, hint))

let private write (color: ConsoleColor option) (text: string) =
    match color with
    | Some c ->
        let old = Console.ForegroundColor
        Console.ForegroundColor <- c
        Console.WriteLine text
        Console.ForegroundColor <- old
    | None -> Console.WriteLine text

let step (text: string) = write (Some ConsoleColor.Cyan) ("\n==> " + text)
let info (text: string) = write None ("    " + text)
let ok (text: string) = write (Some ConsoleColor.Green) ("    OK   " + text)
let warn (text: string) = write (Some ConsoleColor.Yellow) ("    WARN " + text)
let error (text: string) = write (Some ConsoleColor.Red) ("    FAIL " + text)

// ---------------------------------------------------------------- paths

/// Repository root: the directory holding versions.json, found by walking up from tools/.
let repoRoot =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            fail "Could not find versions.json above tools/." "Run the scripts from a Fable.SageFs checkout."
        elif File.Exists(Path.Combine(dir.FullName, "versions.json")) then dir.FullName
        else up dir.Parent
    up (DirectoryInfo __SOURCE_DIRECTORY__)

let (</>) (a: string) (b: string) = Path.Combine(a, b)

/// Path relative to the repo root with forward slashes, for log output.
let rel (path: string) =
    Path.GetRelativePath(repoRoot, path).Replace('\\', '/')

// ---------------------------------------------------------------- versions.json

type Versions =
    { FableCompiler: string
      FcsFork: string
      FSharpCore: string
      TestedSageFs: string
      DebugSdk: string }

let readVersions () =
    let path = repoRoot </> "versions.json"
    use doc = JsonDocument.Parse(File.ReadAllText path)
    let get (key: string) =
        match doc.RootElement.TryGetProperty key with
        | true, v when v.ValueKind = JsonValueKind.String && v.GetString() <> "" -> v.GetString()
        | _ -> fail $"versions.json has no string property '{key}'." $"Add \"{key}\": \"<version>\" to {path}."
    { FableCompiler = get "fableCompiler"
      FcsFork = get "fcsFork"
      FSharpCore = get "fsharpCore"
      TestedSageFs = get "testedSageFs"
      DebugSdk = get "debugSdk" }

// ---------------------------------------------------------------- processes

type ProcessResult = { ExitCode: int; Output: string }

/// Runs a process and captures stdout+stderr. MSBuild* variables inherited from the
/// `dotnet fsi` host are removed so a child `dotnet` picks its own SDK (global.json).
let runProcess (workDir: string) (exe: string) (args: string list) (echo: bool) =
    let psi = ProcessStartInfo(exe, WorkingDirectory = workDir, UseShellExecute = false,
                               RedirectStandardOutput = true, RedirectStandardError = true)
    for a in args do psi.ArgumentList.Add a
    for key in psi.Environment.Keys |> Seq.toList do
        if key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)
           || key.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase) then
            psi.Environment.Remove key |> ignore
    let output = Text.StringBuilder()
    let gate = obj ()
    let onLine (line: string) =
        if not (isNull line) then
            lock gate (fun () ->
                output.AppendLine line |> ignore
                if echo then Console.WriteLine("      | " + line))
    use p =
        try Process.Start psi
        with ex ->
            fail $"Could not start '{exe}': {ex.Message}" $"Make sure '{exe}' is installed and on PATH."
    p.OutputDataReceived.Add(fun e -> onLine e.Data)
    p.ErrorDataReceived.Add(fun e -> onLine e.Data)
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()
    p.WaitForExit()
    { ExitCode = p.ExitCode; Output = output.ToString() }

let dotnet workDir args echo = runProcess workDir "dotnet" args echo

// ---------------------------------------------------------------- files

/// Writes bytes only when they differ from what is on disk. Returns true when written.
/// A locked target (e.g. vendor/*.dll held by a running SageFs session) fails with a hint.
let writeIfChanged (path: string) (bytes: byte[]) =
    if File.Exists path && ReadOnlySpan<byte>(File.ReadAllBytes path).SequenceEqual(ReadOnlySpan<byte> bytes) then
        false
    else
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        try
            File.WriteAllBytes(path, bytes)
            true
        with :? IOException as ex ->
            fail
                $"Cannot overwrite {rel path}: {ex.Message}"
                "It is probably locked by a running SageFs session (or another process) using vendor/. Stop that session (stop_session) and rerun."

let writeTextIfChanged (path: string) (text: string) =
    writeIfChanged path (Text.UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n")))

// ---------------------------------------------------------------- NuGet packages

/// Global packages folder (honours NUGET_PACKAGES).
let nugetGlobalPackages =
    match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
    | null | "" -> Environment.GetFolderPath Environment.SpecialFolder.UserProfile </> ".nuget" </> "packages"
    | p -> p

/// Returns an extracted package directory for id/version. Uses the NuGet global packages
/// folder when the package is already there, otherwise downloads from nuget.org into
/// .work/packages/<id>/<version>/ (extracted atomically; reruns reuse it).
let ensurePackage (id: string) (version: string) =
    let lower = id.ToLowerInvariant()
    let cached = nugetGlobalPackages </> lower </> version
    let local = repoRoot </> ".work" </> "packages" </> lower </> version
    if File.Exists(cached </> $"{lower}.nuspec") then
        info $"{id} {version}: NuGet cache {cached}"
        cached
    elif File.Exists(local </> ".complete") then
        info $"{id} {version}: {rel local} (already downloaded)"
        local
    else
        let url = $"https://api.nuget.org/v3-flatcontainer/{lower}/{version}/{lower}.{version}.nupkg"
        info $"{id} {version}: downloading {url}"
        let bytes =
            try
                use http = new Net.Http.HttpClient(Timeout = TimeSpan.FromMinutes 5.0)
                http.GetByteArrayAsync(url).GetAwaiter().GetResult()
            with ex ->
                fail $"Download of {id} {version} failed: {ex.Message}"
                     $"Check your network connection, or restore it into the NuGet cache ({nugetGlobalPackages}) and rerun."
        let tmp = local + ".tmp-" + string (Diagnostics.Process.GetCurrentProcess().Id)
        if Directory.Exists tmp then Directory.Delete(tmp, true)
        Directory.CreateDirectory tmp |> ignore
        use ms = new MemoryStream(bytes)
        IO.Compression.ZipFile.ExtractToDirectory(ms, tmp)
        File.WriteAllText(tmp </> ".complete", url)
        if Directory.Exists local then Directory.Delete(local, true)
        Directory.CreateDirectory(Path.GetDirectoryName local) |> ignore
        Directory.Move(tmp, local)
        local

/// (id, version) of each dependency of a package for the given target framework, from its .nuspec.
let nuspecDependencies (packageDir: string) (tfm: string) =
    let nuspec =
        Directory.GetFiles(packageDir, "*.nuspec") |> Array.tryHead
        |> Option.defaultWith (fun () -> fail $"No .nuspec in {packageDir}." "Delete that folder and rerun.")
    let doc = Xml.Linq.XDocument.Load nuspec
    let local (name: string) (e: Xml.Linq.XElement) = e.Name.LocalName = name
    doc.Descendants()
    |> Seq.filter (local "group")
    |> Seq.filter (fun g -> (g.Attribute(Xml.Linq.XName.Get "targetFramework") |> Option.ofObj |> Option.map _.Value) = Some tfm)
    |> Seq.collect (fun g -> g.Elements() |> Seq.filter (local "dependency"))
    |> Seq.map (fun d -> d.Attribute(Xml.Linq.XName.Get "id").Value, d.Attribute(Xml.Linq.XName.Get "version").Value)
    |> Seq.toList
