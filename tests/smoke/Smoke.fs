/// Exit code 0 when every Playground.selfCheck passes, 1 otherwise.
/// With --expect-unoptimized (hackable mode) every Fable assembly must also be an unoptimized build.
module Smoke

[<EntryPoint>]
let main argv =
    printfn "%s" (Playground.fcsReport ())
    printfn "%s" (Playground.fableReport ())
    let flavour =
        if argv |> Array.contains "--expect-unoptimized" then
            let optimized = Playground.fableAssemblies () |> List.filter (fun a -> not a.Unoptimized)
            [ { Playground.Check.Name = "Fable assemblies are unoptimized (hackable build)"
                Playground.Check.Passed = optimized.IsEmpty
                Playground.Check.Detail =
                  if optimized.IsEmpty then "Fable.AST, Fable.Transforms, Fable.Transforms.Babel, Fable.Compiler: Optimize=false"
                  else "optimized: " + (optimized |> List.map (fun a -> $"{a.Name} ({a.Location})") |> String.concat ", ") } ]
        else []
    if Playground.printChecks (Playground.selfCheck () @ flavour) then 0 else 1
