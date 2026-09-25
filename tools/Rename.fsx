// Renames Fable's FSharp.Compiler.Service fork to 'Fable.FSharp.Compiler.Service' and retargets
// every AssemblyRef to it, so the fork can load next to the SDK's FCS inside a SageFs FsiHost
// (both have the same simple name and public key token, so a naive load silently yields the SDK copy).
//
// Library use (setup.fsx):  #load "tools/Rename.fsx"  then  Rename.run ...
// Standalone:               dotnet fsi tools/Rename.fsx <inputDir> <outDir> [expectedFcsVersion]
//   <inputDir> holds FSharp.Compiler.Service.dll plus the Fable.* / FSharp.DependencyManager.Nuget dlls.
//
// What it does, per input assembly:
//   * FSharp.Compiler.Service.dll -> Fable.FSharp.Compiler.Service.dll: assembly + module renamed,
//     public key and StrongNameSigned flag cleared (strong names are ignored on .NET Core).
//   * every AssemblyRef named FSharp.Compiler.Service -> Fable.FSharp.Compiler.Service (token cleared).
//   * InternalsVisibleTo naming FSharp.Compiler.Service in a *referencing* assembly is rewritten.
// The F# metadata resources (FSharpSignatureCompressedData.FSharp.Compiler.Service etc.) keep their
// names on purpose: Fable.Transforms' pickled signatures refer to the FCS CCU by that name.
//
// Output is deterministic (reruns are byte-identical) and only written when it changed.
// After writing, the result is re-read from disk and verified; any violation throws.

#r "nuget: Mono.Cecil, 0.11.6"
#load "Common.fsx"

open System
open System.IO
open Mono.Cecil
open Mono.Cecil.Cil
open Common

[<Literal>]
let OldName = "FSharp.Compiler.Service"

[<Literal>]
let NewName = "Fable.FSharp.Compiler.Service"

type Input = { Source: string; OutputName: string }

type Outcome =
    { Written: string list
      Unchanged: string list
      /// Output name -> assembly refs that were retargeted in it.
      Retargeted: (string * string) list
      /// Full name of the renamed fork as read back from disk.
      RenamedFcs: string
      Report: string }

let rec private allTypes (t: TypeDefinition) =
    seq {
        yield t
        for n in t.NestedTypes do
            yield! allTypes n
    }

/// Every string in the assembly that mentions OldName: ldstr operands, custom-attribute strings,
/// resource names. "qualified" = contains "FSharp.Compiler.Service," (an assembly-qualified
/// name that reflection could try to load by the old identity).
let private scanStrings (asm: AssemblyDefinition) =
    let found = Collections.Generic.List<bool * string>()
    let check where (s: string) =
        if not (isNull s) && s.Contains OldName then
            found.Add((s.Contains(OldName + ","), sprintf "%s | %s | %A" asm.Name.Name where s))
    let scanAttrs where (attrs: Collections.Generic.IEnumerable<CustomAttribute>) =
        for a in attrs do
            for arg in a.ConstructorArguments do
                match arg.Value with
                | :? string as s -> check (sprintf "%s [%s]" where a.AttributeType.Name) s
                | _ -> ()
    scanAttrs "assembly" asm.CustomAttributes
    for m in asm.Modules do
        for r in m.Resources do
            check "resource-name" r.Name
        for t in m.Types |> Seq.collect allTypes do
            scanAttrs t.FullName t.CustomAttributes
            for md in t.Methods do
                if md.HasBody then
                    for i in md.Body.Instructions do
                        if i.OpCode = OpCodes.Ldstr then check md.FullName (i.Operand :?> string)
    List.ofSeq found

let private readBytes (bytes: byte[]) (resolver: IAssemblyResolver) =
    AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), ReaderParameters(AssemblyResolver = resolver, ReadSymbols = false))

/// Rewrites `inputs` into `outDir`. `expectedFcsVersion` (e.g. "43.11.200") is checked against the
/// fork's assembly version before anything is written.
let run (inputs: Input list) (outDir: string) (expectedFcsVersion: string) : Outcome =
    let report = Text.StringBuilder()
    let log (s: string) =
        info s
        report.AppendLine s |> ignore

    for i in inputs do
        if not (File.Exists i.Source) then
            fail $"Rename input not found: {i.Source}" "Delete .work/packages and rerun setup.fsx to re-download."

    let fcsInput =
        inputs
        |> List.tryFind (fun i -> Path.GetFileName i.Source = OldName + ".dll")
        |> Option.defaultWith (fun () -> fail "No FSharp.Compiler.Service.dll among the rename inputs." "This is a bug in setup.fsx.")

    use resolver = new DefaultAssemblyResolver()
    for d in inputs |> List.map (fun i -> Path.GetDirectoryName i.Source) |> List.distinct do
        resolver.AddSearchDirectory d

    // ---- guard: the fork must be the version we pinned
    let expected = Version(expectedFcsVersion + ".0")
    do
        use fcs = readBytes (File.ReadAllBytes fcsInput.Source) resolver
        if fcs.Name.Name <> OldName || fcs.Name.Version <> expected then
            fail
                $"Expected the Fable FCS fork {OldName} {expected} in {fcsInput.Source}, found {fcs.FullName}."
                "versions.json 'fcsFork' does not match the FCS shipped in the pinned Fable.Compiler. Update one of them."

    Directory.CreateDirectory outDir |> ignore
    let written = ResizeArray()
    let unchanged = ResizeArray()
    let retargeted = ResizeArray()

    log $"rename {OldName} -> {NewName}"
    for input in inputs do
        // Read from memory so nothing in the NuGet cache stays locked.
        use asm = readBytes (File.ReadAllBytes input.Source) resolver
        let m = asm.MainModule
        if asm.Name.Name = OldName then
            asm.Name.Name <- NewName
            asm.Name.PublicKey <- Array.empty
            asm.Name.PublicKeyToken <- Array.empty
            asm.Name.HasPublicKey <- false
            m.Name <- input.OutputName
            m.Attributes <- m.Attributes &&& ~~~ModuleAttributes.StrongNameSigned
        for r in m.AssemblyReferences do
            if r.Name = OldName then
                log $"  [{input.OutputName}] AssemblyRef {r.FullName} -> {NewName} (token cleared)"
                retargeted.Add((input.OutputName, r.FullName))
                r.Name <- NewName
                r.PublicKeyToken <- Array.empty
                r.PublicKey <- Array.empty
        if asm.Name.Name <> NewName then
            for a in asm.CustomAttributes do
                if a.AttributeType.Name = "InternalsVisibleToAttribute" then
                    match a.ConstructorArguments.[0].Value with
                    | :? string as s when s.Split(',').[0].Trim() = OldName ->
                        log $"  [{input.OutputName}] InternalsVisibleTo {s} -> {NewName}"
                        a.ConstructorArguments.[0] <- CustomAttributeArgument(a.ConstructorArguments.[0].Type, NewName)
                    | _ -> ()
        use ms = new MemoryStream()
        asm.Write ms
        let dst = outDir </> input.OutputName
        if writeIfChanged dst (ms.ToArray()) then written.Add input.OutputName else unchanged.Add input.OutputName

    // ---- verify what is on disk now
    log ""
    log "verify (re-read from disk)"
    let problems = ResizeArray<string>()
    let mutable renamedFcs = ""
    let mentions = ResizeArray<string>()
    for input in inputs do
        let dst = outDir </> input.OutputName
        use a = readBytes (File.ReadAllBytes dst) resolver
        log $"  {input.OutputName}: {a.FullName}"
        if a.Name.Name = OldName then problems.Add $"{input.OutputName} is still named {OldName}"
        if input.OutputName = NewName + ".dll" then
            renamedFcs <- a.FullName
            if a.Name.Name <> NewName then problems.Add $"{input.OutputName} is named {a.Name.Name}"
            if a.Name.Version <> expected then problems.Add $"{input.OutputName} has version {a.Name.Version}"
            if a.Name.HasPublicKey || (not (isNull a.Name.PublicKeyToken) && a.Name.PublicKeyToken.Length > 0) then
                problems.Add $"{input.OutputName} still has a public key"
        for r in a.MainModule.AssemblyReferences do
            if r.Name = OldName then problems.Add $"{input.OutputName} still references {r.FullName}"
            if r.Name = NewName && r.Version <> expected then
                problems.Add $"{input.OutputName} references {NewName} {r.Version}, expected {expected}"
        for (qualified, s) in scanStrings a do
            if qualified then problems.Add $"assembly-qualified old name in {s}" else mentions.Add s
    log $"  benign mentions of '{OldName}' (attributes, F# metadata resource names, messages): {mentions.Count}"
    for s in mentions do
        report.AppendLine("    " + s) |> ignore

    if retargeted.Count = 0 then problems.Add "no AssemblyRef was retargeted (expected Fable.Compiler, Fable.Transforms, ...)"
    if problems.Count > 0 then
        for p in problems do
            error p
        File.WriteAllText(outDir </> "rename-report.txt", report.ToString())
        fail $"Rename verification failed ({problems.Count} problem(s))." $"""See {outDir </> "rename-report.txt"}."""

    File.WriteAllText(outDir </> "rename-report.txt", report.ToString())
    { Written = List.ofSeq written
      Unchanged = List.ofSeq unchanged
      Retargeted = List.ofSeq retargeted
      RenamedFcs = renamedFcs
      Report = report.ToString() }

/// The standard input set from an extracted Fable.Compiler package's lib dir plus Fable.AST.
let fableInputs (fableCompilerLib: string) (fableAstDll: string) =
    [ { Source = fableCompilerLib </> "FSharp.Compiler.Service.dll"; OutputName = NewName + ".dll" }
      { Source = fableCompilerLib </> "Fable.Compiler.dll"; OutputName = "Fable.Compiler.dll" }
      { Source = fableCompilerLib </> "Fable.Transforms.dll"; OutputName = "Fable.Transforms.dll" }
      { Source = fableCompilerLib </> "Fable.Transforms.Babel.dll"; OutputName = "Fable.Transforms.Babel.dll" }
      { Source = fableCompilerLib </> "FSharp.DependencyManager.Nuget.dll"; OutputName = "FSharp.DependencyManager.Nuget.dll" }
      { Source = fableAstDll; OutputName = "Fable.AST.dll" } ]

// ---------------------------------------------------------------- standalone entry point
let private isMainScript =
    fsi.CommandLineArgs.Length > 0
    && Path.GetFileName(fsi.CommandLineArgs.[0]).Equals("Rename.fsx", StringComparison.OrdinalIgnoreCase)

if isMainScript then
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| inputDir; outDir |]
    | [| inputDir; outDir; _ |] as args ->
        let expected = if args.Length = 3 then args.[2] else (readVersions ()).FcsFork
        let inputs =
            Directory.GetFiles(inputDir, "*.dll")
            |> Array.filter (fun f -> Path.GetFileName f <> "FSharp.Core.dll")
            |> Array.map (fun f ->
                let name = Path.GetFileName f
                { Source = f; OutputName = if name = OldName + ".dll" then NewName + ".dll" else name })
            |> List.ofArray
        try
            let o = run inputs (Path.GetFullPath outDir) expected
            ok $"{o.RenamedFcs}: {o.Written.Length} written, {o.Unchanged.Length} unchanged"
        with SetupFailure(msg, hint) ->
            error msg
            info hint
            exit 1
    | _ ->
        eprintfn "usage: dotnet fsi tools/Rename.fsx <inputDir> <outDir> [expectedFcsVersion]"
        exit 2
