/// Exit code 0 when every Playground.selfCheck passes, 1 otherwise.
module Smoke

[<EntryPoint>]
let main _ =
    printfn "%s" (Playground.fcsReport ())
    if Playground.printChecks (Playground.selfCheck ()) then 0 else 1
