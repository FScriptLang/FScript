namespace FScript.Runtime.Tests

open System
open System.Threading
open NUnit.Framework
open FScript.Language
open FScript.Runtime

[<TestFixture>]
type HttpTests() =
    let registration permissions = Registry.registration { HostContext.create "." with Permissions = permissions }
    let run permissions source = ScriptHost.loadSourceWithRegistration (registration permissions) source |> _.LastValue
    let grants = HostPermissions.Network ||| HostPermissions.Threading
    let get url options = $"Http.get \"{url}\" {options} |> Task.await"
    let result expectedCase = function
        | VUnionCase("HttpResult", name, Some(VRecord fields)) when name = expectedCase -> fields
        | other -> failwithf "Expected HttpResult.%s, got %s" expectedCase (Pretty.valueToString other)

    [<Test>]
    member _.``Permission is checked before request dispatch`` () =
        let source = "Http.get \"file:///tmp/file\" Http.defaults"
        for permissions in [HostPermissions.None; HostPermissions.IO; HostPermissions.Native; HostPermissions.Threading] do
            Assert.Throws<TypeException>(Action(fun () -> run permissions source |> ignore)) |> ignore
        run HostPermissions.Network (source + " |> ignore") |> ignore
        Assert.Throws<TypeException>(Action(fun () -> run HostPermissions.Network (source + " |> Task.await") |> ignore)) |> ignore
        let original = (registration grants).Functions |> List.find (fun fn -> fn.Name = "Http.get")
        let safe = registration HostPermissions.None
        let renamed = { original with Name = "Host.get"; RequiredPermissions = HostPermissions.None }
        let forged = { safe with Functions = renamed :: safe.Functions }
        let error = Assert.Throws<EvalException>(Action(fun () ->
            ScriptHost.loadSourceWithRegistration forged (source.Replace("Http.get", "Host.get")) |> ignore)) |> nonNull
        Assert.That(error.Message, Does.Contain("Network"))

    [<TestCase("post", "POST")>]
    [<TestCase("put", "PUT")>]
    [<TestCase("patch", "PATCH")>]
    [<TestCase("delete", "DELETE")>]
    [<TestCase("send \"OPTIONS\"", "OPTIONS")>]
    member _.``Methods send headers and bodies and preserve HTTP error responses`` (call: string, httpMethod: string) = task {
        use server = new HttpTestServer(HttpTestServer.Reply("HTTP/1.1 422 Unprocessable Entity\r\nContent-Length: 2\r\nSet-Cookie: a=1\r\nSet-Cookie: b=2\r\nConnection: close\r\n\r\n{}"))
        let source = $"""
type Payload = {{ value: int }}
let body = Json.serialize {{ value = 42 }} |> Option.defaultValue ""
let request = {{ Http.defaults with headers = [("Content-Type", "application/json"); ("X-Example", "yes")]; body = Some (HttpBody.Text body) }}
Http.{call} "{server.Url}" request |> Task.await
"""
        let response = run grants source |> result "Received"
        Assert.That(Pretty.valueToString response.["statusCode"], Is.EqualTo(Pretty.valueToString (VInt 422L)))
        Assert.That(Pretty.valueToString response.["body"], Is.EqualTo(Pretty.valueToString (VList [VInt 123L; VInt 125L])))
        let responseHeaders =
            match response.["headers"] with
            | VList values -> values
            | _ -> failwith "Expected response headers"
        for value in ["a=1"; "b=2"] do
            Assert.That(responseHeaders |> List.exists (function
                | VTuple [VString key; VString actual] -> key = "Set-Cookie" && actual = value
                | _ -> false), Is.True)
        let! (requestLine, headers, sent) = server.Request
        Assert.That(requestLine, Is.EqualTo(httpMethod + " /test?q=1 HTTP/1.1"))
        Assert.That(headers, Does.Contain("Content-Type: application/json").And.Contain("X-Example: yes"))
        Assert.That(sent, Is.EqualTo("{\"value\":42}"))
    }

    [<Test>]
    member _.``HEAD returns redirect and an empty body`` () = task {
        use server = new HttpTestServer(HttpTestServer.Reply("HTTP/1.1 302 Found\r\nContent-Length: 1200\r\nLocation: /other\r\nConnection: close\r\n\r\n"))
        let response = run grants $"Http.head \"{server.Url}\" Http.defaults |> Task.await" |> result "Received"
        Assert.That(Pretty.valueToString response.["statusCode"], Is.EqualTo(Pretty.valueToString (VInt 302L)))
        Assert.That(Pretty.valueToString response.["body"], Is.EqualTo(Pretty.valueToString (VList [])))
        let! (requestLine, _, _) = server.Request
        Assert.That(requestLine, Does.StartWith("HEAD "))
    }

    [<Test>]
    member _.``Response limits apply while streaming chunked bodies`` () =
        use server = new HttpTestServer(HttpTestServer.Reply("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n3\r\nabc\r\n0\r\n\r\n"))
        let response = get server.Url "{ Http.defaults with maxResponseBytes = 2 }" |> run grants |> result "Failed"
        Assert.That(Pretty.valueToString response.["kind"], Is.EqualTo(Pretty.valueToString (VString "ResponseTooLarge")))

    [<Test>]
    member _.``Timeout covers a stalled response body`` () =
        use server = new HttpTestServer(fun stream token -> task {
            do! HttpTestServer.Reply("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nx") stream token
            do! System.Threading.Tasks.Task.Delay(Timeout.Infinite, token)
        })
        let response = get server.Url "{ Http.defaults with timeoutMilliseconds = 500 }" |> run grants |> result "Failed"
        Assert.That(Pretty.valueToString response.["kind"], Is.EqualTo(Pretty.valueToString (VString "Timeout")))

    [<TestCase("file:///tmp/test", "Http.defaults")>]
    [<TestCase("http://127.0.0.1", "{ Http.defaults with body = Some (HttpBody.Bytes [256]) }")>]
    [<TestCase("http://127.0.0.1", "{ Http.defaults with headers = [(\"X-Test\", \"invalid\\nvalue\")] }")>]
    member _.``Invalid inputs fail before connecting`` (url: string, options: string) =
        let response = get url options |> run grants |> result "Failed"
        Assert.That(Pretty.valueToString response.["kind"], Is.EqualTo(Pretty.valueToString (VString "InvalidRequest")))

    [<Test>]
    member _.``Debugger launch uses selected network permissions`` () =
        HostTestHelpers.withTempRoot "http-debug" (fun directory ->
            let script = System.IO.Path.Combine(directory, "main.fss")
            let config = System.IO.Path.Combine(directory, "fscript.toml")
            System.IO.File.WriteAllText(script, "match Http.get \"file:///tmp/test\" Http.defaults |> Task.await with | HttpResult.Failed error -> error.kind | _ -> \"unexpected\"")
            Assert.Throws<TypeException>(Action(fun () ->
                use _session = DebugLaunch.createForCli script None true None [||] false
                ())) |> ignore
            System.IO.File.WriteAllText(config, "[runtime]\npermissions = ['Network', 'Threading']")
            use session = DebugLaunch.createForCli script (Some config) false None [||] false
            session.Run()
            Assert.That(session.ResultSummary, Is.EqualTo("\"InvalidRequest\"")))

    [<Test>]
    member _.``Debug cancellation interrupts the HTTP body read`` () =
        use writing = new ManualResetEventSlim(false)
        use server = new HttpTestServer(fun stream token -> task {
            do! HttpTestServer.Reply("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nx") stream token
            writing.Set()
            do! System.Threading.Tasks.Task.Delay(Timeout.Infinite, token)
        })
        let program = FScript.parseWithSourceName None (get server.Url "Http.defaults")
        use session = new DebugSession(registration grants, program, false)
        let running = System.Threading.Tasks.Task.Run(Action session.Run)
        Assert.That(writing.Wait(TimeSpan.FromSeconds 5.0), Is.True)
        session.Cancel()
        Assert.Catch<OperationCanceledException>(Action(fun () ->
            running.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())) |> ignore
