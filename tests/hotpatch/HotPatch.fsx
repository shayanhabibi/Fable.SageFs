// End-to-end hot-patch test: a real SageFs session on hackable/Fable.SageFs.Hackable.fsproj
// redefines a function inside Fable.Transforms.Babel and the next compile reflects it.
//
//   dotnet fsi tests/hotpatch/HotPatch.fsx
//
// Talks to the running SageFs daemon over its MCP HTTP endpoint (port from ~/.SageFs/daemon-info.json).
// It never starts, stops or restarts the daemon, and never touches sessions it did not create:
// it creates its own session and always stops it again.
//
// Steps (the hackable-mode gate):
//   1. create a session on hackable/Fable.SageFs.Hackable.fsproj and wait until Ready
//   2. in the session: Fable assemblies are the unoptimized source build, bound to the renamed FCS fork
//   3. baseline: Playground.compileHello () equals samples/Hello/Hello.expected.js          -> A.js
//   4. redefine Fable.Transforms.Fable2Babel.Util.getUnionCaseName with send_fsharp_code;
//      the recompiled JS changes exactly on the union-case-names line, no rebuild/reset      -> B.js
//   5. redefine it back to the original body; the JS equals the baseline again               -> A2.js
// Output files go to .work/hotpatch/ (gitignored). No source file is modified.
//
// Exit codes: 0 pass, 1 fail, 3 skipped (no reachable SageFs daemon, or hackable/ is already in use by
// another session; the steps are printed so they can be run by hand).

#load "../../tools/Common.fsx"

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Common

let hackableDir = repoRoot </> "hackable"
let hackableProject = hackableDir </> "Fable.SageFs.Hackable.fsproj"
let outDir = repoRoot </> ".work" </> "hotpatch"
/// SageFs keys sessions by working directory; it reports them with forward slashes.
let sessionDir = hackableDir.Replace('\\', '/')
let sessionProject = hackableProject.Replace('\\', '/')

exception Skip of string

// ---------------------------------------------------------------- MCP over HTTP

let daemonPort () =
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    let infoFile = home </> ".SageFs" </> "daemon-info.json"
    if not (File.Exists infoFile) then raise (Skip $"{infoFile} not found: the SageFs daemon is not running.")
    use doc = JsonDocument.Parse(File.ReadAllText infoFile)
    doc.RootElement.GetProperty("McpPort").GetInt32()

type Mcp(port: int) =
    let http = new HttpClient(Timeout = TimeSpan.FromMinutes 15.0)
    let url = $"http://localhost:{port}/"
    let mutable sessionId: string = null
    let mutable nextId = 0

    let post (body: JsonObject) =
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        req.Headers.Accept.ParseAdd "application/json"
        req.Headers.Accept.ParseAdd "text/event-stream"
        if not (isNull sessionId) then req.Headers.Add("Mcp-Session-Id", sessionId)
        use resp = http.Send req
        match resp.Headers.TryGetValues "Mcp-Session-Id" with
        | true, v -> sessionId <- Seq.head v
        | _ -> ()
        let text = resp.Content.ReadAsStringAsync().Result
        if not resp.IsSuccessStatusCode then failwith $"MCP HTTP {int resp.StatusCode}: {text}"
        text

    /// The JSON-RPC response with `id`, from a plain JSON body or an SSE stream.
    let responseFor (id: int) (text: string) =
        let candidates =
            if text.TrimStart().StartsWith "{" then [ text ]
            else
                text.Split('\n')
                |> Array.filter (fun l -> l.StartsWith "data:")
                |> Array.map (fun l -> l.Substring(5).Trim())
                |> List.ofArray
        candidates
        |> List.map (fun c -> JsonNode.Parse c)
        |> List.tryFind (fun n -> not (isNull n) && not (isNull n.["id"]) && n.["id"].GetValue<int>() = id)
        |> Option.defaultWith (fun () -> failwith $"no JSON-RPC response with id {id} in: {text}")

    let request (method': string) (parameters: JsonObject) =
        nextId <- nextId + 1
        let id = nextId
        let body = JsonObject()
        body["jsonrpc"] <- "2.0"
        body["id"] <- id
        body["method"] <- method'
        body["params"] <- parameters
        let response = responseFor id (post body)
        if not (isNull response.["error"]) then failwith ($"MCP {method'} error: " + response.["error"].ToJsonString())
        response.["result"]

    member _.Initialize() =
        let p = JsonObject()
        p["protocolVersion"] <- "2025-03-26"
        p["capabilities"] <- JsonObject()
        let client = JsonObject()
        client["name"] <- "fable-sagefs-hotpatch-test"
        client["version"] <- "1"
        p["clientInfo"] <- client
        let result = request "initialize" p
        let n = JsonObject()
        n["jsonrpc"] <- "2.0"
        n["method"] <- "notifications/initialized"
        post n |> ignore
        result.["serverInfo"].["version"].GetValue<string>()

    /// Calls an MCP tool; returns (isError, concatenated text content).
    member _.Call(tool: string, args: (string * JsonNode) list) =
        let p = JsonObject()
        p["name"] <- tool
        let a = JsonObject()
        for k, v in args do a[k] <- v
        p["arguments"] <- a
        let result = request "tools/call" p
        let isError = not (isNull result.["isError"]) && result.["isError"].GetValue<bool>()
        let text =
            result.["content"].AsArray()
            |> Seq.choose (fun c -> if isNull c.["text"] then None else Some(c.["text"].GetValue<string>()))
            |> String.concat "\n"
        isError, text

    interface IDisposable with
        member _.Dispose() = http.Dispose()

let str (s: string) : JsonNode = JsonValue.Create s

// ---------------------------------------------------------------- session helpers

let agent = "fable-sagefs-hotpatch-test"

/// send_fsharp_code in our session; fails on an MCP error or an eval error.
let eval (mcp: Mcp) (label: string) (code: string) =
    let isError, text =
        mcp.Call("send_fsharp_code", [ "agentName", str agent; "code", str code; "working_directory", str sessionDir ])
    // Over stdio the result is JSON {"success": ...}; over HTTP it is plain text ("Result: ..." per statement,
    // "Error: ..."/"Evaluation failed" on failure). Every check below also reads files the evals write, and those
    // are deleted up front, so an eval failure missed here still fails the test.
    let failed =
        isError
        || text.Contains "\"success\":false"
        || text.Contains "Evaluation failed"
        || RegularExpressions.Regex.IsMatch(text, @"(?m)^\s*(Error\b|\[error\])")
    if failed then
        error $"{label}: send_fsharp_code failed"
        info $"input:\n{code}"
        info $"output:\n{text}"
        failwith $"{label}: eval failed"
    text

let readOut (name: string) = File.ReadAllText(outDir </> name)

/// Lines that differ between two texts of equal line count, as (1-based line, a, b).
let lineDiff (a: string) (b: string) =
    let la = a.Replace("\r\n", "\n").Split('\n')
    let lb = b.Replace("\r\n", "\n").Split('\n')
    if la.Length <> lb.Length then None
    else Some [ for i in 0 .. la.Length - 1 do if la.[i] <> lb.[i] then yield i + 1, la.[i], lb.[i] ]

// ---------------------------------------------------------------- the code sent to the session

let originalBody =
    """module Fable =
    module Transforms =
        module Fable2Babel =
            module Util =
                let getUnionCaseName (uci: global.Fable.AST.Fable.UnionCase) =
                    match uci.CompiledName with
                    | Some cname -> cname
                    | None -> uci.Name;;"""

let patchedBody =
    """module Fable =
    module Transforms =
        module Fable2Babel =
            module Util =
                let getUnionCaseName (uci: global.Fable.AST.Fable.UnionCase) =
                    "HOTPATCH:" + (match uci.CompiledName with
                                   | Some cname -> cname
                                   | None -> uci.Name);;"""

/// Compiles Hello in the session, writes the JS and the elapsed ms to .work/hotpatch/<name>.js / .ms.
let compileTo (name: string) =
    let o = outDir.Replace("\\", "\\\\")
    $"""let hotpatchCompile () =
    let sw = System.Diagnostics.Stopwatch.StartNew ()
    let js = Playground.compileHello ()
    let ms = sw.ElapsedMilliseconds
    System.IO.File.WriteAllText(System.IO.Path.Combine("{o}", "{name}.js"), js)
    System.IO.File.WriteAllText(System.IO.Path.Combine("{o}", "{name}.ms"), string ms)
    ms;;
hotpatchCompile ();;"""

let manualSteps () =
    info "To run the test by hand, in a SageFs session on hackable/Fable.SageFs.Hackable.fsproj"
    info "(working directory hackable/):"
    info "    Playground.fableReport ();;              // all four unoptimized, FCS: Fable.FSharp.Compiler.Service"
    info "    let jsA = Playground.compileHello ();;   // equals samples/Hello/Hello.expected.js"
    info "    (send the patched getUnionCaseName from tests/hotpatch/HotPatch.fsx)"
    info "    Playground.compileHello () <> jsA;;      // true: union case names now start with HOTPATCH:"
    info "    (send the original getUnionCaseName)"
    info "    Playground.compileHello () = jsA;;       // true"

// ---------------------------------------------------------------- the test

let run () =
    if not (File.Exists hackableProject) then
        fail $"{rel hackableProject} does not exist." "Run 'dotnet fsi setup.fsx --hackable' first."
    Directory.CreateDirectory outDir |> ignore
    for f in Directory.GetFiles(outDir, "*.*") do
        if [ ".js"; ".ms"; ".txt" ] |> List.contains (Path.GetExtension f) then File.Delete f

    let port = daemonPort ()
    use mcp = new Mcp(port)
    let version =
        try mcp.Initialize()
        with ex -> raise (Skip $"SageFs daemon on port {port} is not reachable: {ex.Message}")
    info $"SageFs {version} on http://localhost:{port}/"

    // Never share a session: if something already uses hackable/, leave it alone.
    let _, sessions = mcp.Call("list_sessions", [])
    if sessions.Contains sessionDir then
        raise (Skip $"a SageFs session already uses {sessionDir}; this test only uses sessions it creates. Stop that session, or run the steps in it by hand.")

    step "1. Create a SageFs session on hackable/Fable.SageFs.Hackable.fsproj"
    let sw = Diagnostics.Stopwatch.StartNew()
    let isError, created =
        mcp.Call("create_session",
                 [ "agentName", str agent
                   "projects", str $"[\"{sessionProject}\"]"
                   "working_directory", str sessionDir ])
    let id =
        let m = RegularExpressions.Regex.Match(created, @"\b[0-9a-f]{8}\b")
        if isError || not m.Success then
            failwith $"create_session did not return a session id:\n{created}"
        m.Value
    info $"session {id}"
    try
        let deadline = DateTime.UtcNow.AddMinutes 10.0
        let rec waitReady () =
            // While warming up the tool may answer with an error ("still warming up"): keep polling.
            let status =
                try snd (mcp.Call("get_fsi_status", [ ("working_directory", str sessionDir) ]))
                with ex -> ex.Message
            if status.Contains "State: Faulted" then failwith $"session faulted:\n{status}"
            elif status.Contains "State: Ready" then status
            elif DateTime.UtcNow > deadline then failwith $"session not Ready after 10 minutes:\n{status}"
            else
                Threading.Thread.Sleep 2000
                waitReady ()
        waitReady () |> ignore
        ok $"session {id} Ready after {sw.Elapsed.TotalSeconds:F0} s"

        step "2. Fable in the session: unoptimized source build on the renamed FCS fork"
        eval mcp "fableAssemblies" """let hotpatchFable =
    let expected = Playground.expectedFcsIdentity ()
    let asms = Playground.fableAssemblies ()
    let okAll = asms |> List.forall (fun a -> a.Unoptimized && (a.Fcs = "-" || a.Fcs = expected))
    Playground.fcsIdentity () = expected && okAll;;
System.IO.File.WriteAllText(System.IO.Path.Combine(Playground.repoRoot, ".work", "hotpatch", "fable.txt"), Playground.fableReport () + "\n" + string hotpatchFable);;"""
        |> ignore
        let report = readOut "fable.txt"
        for line in report.Split('\n') do info line
        if not (report.TrimEnd().EndsWith "True") then
            failwith "Fable assemblies in the session are not the unoptimized build bound to the renamed fork (see above)."
        ok "4 Fable assemblies unoptimized, bound to the renamed fork"

        step "3. Baseline compile of samples/Hello"
        eval mcp "baseline" (compileTo "A") |> ignore
        let a = readOut "A.js"
        let golden = File.ReadAllText(repoRoot </> "samples" </> "Hello" </> "Hello.expected.js")
        if a.Replace("\r\n", "\n").Trim() <> golden.Replace("\r\n", "\n").Trim() then
            failwith "baseline JS (.work/hotpatch/A.js) differs from samples/Hello/Hello.expected.js"
        ok $"""A.js equals the golden JS ({readOut "A.ms"} ms)"""

        step "4. Hot-patch Fable2Babel.Util.getUnionCaseName with send_fsharp_code"
        let swPatch = Diagnostics.Stopwatch.StartNew()
        eval mcp "patch" patchedBody |> ignore
        let patchMs = swPatch.ElapsedMilliseconds
        eval mcp "patched compile" (compileTo "B") |> ignore
        let editToJs = swPatch.ElapsedMilliseconds
        let b = readOut "B.js"
        match lineDiff a b with
        | Some [ line, before, after ] when after.Contains "HOTPATCH:" && not (before.Contains "HOTPATCH:") ->
            info $"line {line}: {before.Trim()}"
            info $"      -> {after.Trim()}"
            ok $"""B.js differs from A.js exactly on the union case names (patch eval {patchMs} ms, compile {readOut "B.ms"} ms, edit to JS {editToJs} ms incl. MCP round-trips)"""
        | Some diffs ->
            for l, x, y in diffs do info $"line {l}: {x.Trim()} -> {y.Trim()}"
            failwith $"expected exactly one changed line containing HOTPATCH:, got {diffs.Length} (see .work/hotpatch/A.js and B.js)"
        | None -> failwith "A.js and B.js have different line counts (see .work/hotpatch/)"

        step "5. Revert the patch"
        let swRevert = Diagnostics.Stopwatch.StartNew()
        eval mcp "revert" originalBody |> ignore
        eval mcp "reverted compile" (compileTo "A2") |> ignore
        if readOut "A2.js" <> a then failwith "after the revert the JS (.work/hotpatch/A2.js) differs from A.js"
        ok $"""A2.js equals A.js again ({swRevert.ElapsedMilliseconds} ms incl. MCP round-trips; compile {readOut "A2.ms"} ms)"""
    finally
        let isError, stopped = mcp.Call("stop_session", [ ("session_id", str id) ])
        if isError then warn $"stop_session {id} failed: {stopped}" else info $"stopped session {id}"

let exitCode =
    try
        run ()
        ok "hot-patch test passed"
        0
    with
    | Skip reason ->
        warn $"hot-patch test skipped: {reason}"
        manualSteps ()
        3
    | SetupFailure(message, hint) ->
        error message
        info hint
        1
    | ex ->
        error $"hot-patch test failed: {ex.Message}"
        info "See docs/hackable.md."
        1

exit exitCode
