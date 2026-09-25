// Self-check inside plain `dotnet fsi`, without SageFs.
//
// dotnet fsi runs on the SDK's own FSharp.Compiler.Service, like the SageFs FsiHost does. This script loads
// the renamed Fable fork from vendor/ into the same process and checks that:
//   - the SDK FCS and the renamed fork are both loaded, under different names;
//   - Fable's InteractiveChecker is bound to the renamed fork, not the SDK FCS;
//   - samples/Hello compiles to the golden JS (cold and warm).
// This is the same collision SageFs hits, so it runs in CI where SageFs is not available.
//
//   dotnet fsi setup.fsx                   (once, to fill vendor/)
//   dotnet fsi tests/fsi/FsiHost.fsx       exit code 0 = passed
//
// FSharp.DependencyManager.Nuget from vendor/ is not referenced: fsi has already loaded its own copy, and
// compiling to JS does not need it.

#r "nuget: FSharp.SystemTextJson, 1.4.36"
#r "nuget: Microsoft.Extensions.Logging.Abstractions, 10.0.12"
#r "nuget: Thoth.Json.Core.Auto, 0.1.1"
#r "nuget: Thoth.Json.System.Text.Json, 0.4.0"
#r "../../vendor/Fable.FSharp.Compiler.Service.dll"
#r "../../vendor/Fable.AST.dll"
#r "../../vendor/Fable.Transforms.dll"
#r "../../vendor/Fable.Transforms.Babel.dll"
#r "../../vendor/Fable.Compiler.dll"
#load "../../templates/session/Playground.fs"

open System

let sdkFcs =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.tryFind (fun a -> a.GetName().Name = "FSharp.Compiler.Service")

printfn "%s" (Playground.fcsReport ())

let hostCheck =
    { Playground.Check.Name = "SDK FCS loaded in the same process (fsi host)"
      Playground.Check.Passed = sdkFcs.IsSome
      Playground.Check.Detail =
        match sdkFcs with
        | Some a -> a.FullName
        | None -> "FSharp.Compiler.Service is not loaded; this check did not exercise the collision" }

let passed = Playground.printChecks (hostCheck :: Playground.selfCheck ())
exit (if passed then 0 else 1)
