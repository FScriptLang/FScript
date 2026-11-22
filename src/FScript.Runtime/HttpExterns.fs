namespace FScript.Runtime

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Reflection
open System.Text
open System.Threading
open FScript.Extensibility

module private HttpTransport =
    let methods = [ "GET"; "HEAD"; "POST"; "PUT"; "PATCH"; "DELETE" ]
    let name (methodName: string) = "Http." + methodName.ToLowerInvariant()

    let defaults =
        VRecord(Map.ofList [
            "headers", VList []
            "body", VOption None
            "timeoutMilliseconds", VInt 30000L
            "maxResponseBytes", VInt 1048576L ])

    let private client =
        let handler = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false)
        new HttpClient(handler, Timeout = Timeout.InfiniteTimeSpan)

    let private result case fields =
        VUnionCase("HttpResult", case, Some(VRecord(Map.ofList fields)))

    let private failed kind message =
        result "Failed" [ "kind", VString kind; "message", VString message ]

    let private invalid message = invalidArg "request" message

    let private bodyContent = function
        | VOption None -> None
        | VOption(Some(VUnionCase("HttpBody", "Text", Some(VString text)))) ->
            let bytes = UTF8Encoding(false, true).GetBytes(text)
            Some(new ByteArrayContent(bytes))
        | VOption(Some(VUnionCase("HttpBody", "Bytes", Some(VList values)))) ->
            let bytes =
                values |> List.map (function
                    | VInt value when value >= 0L && value <= 255L -> byte value
                    | _ -> invalid "HTTP body bytes must be between 0 and 255.")
                |> List.toArray
            Some(new ByteArrayContent(bytes))
        | _ -> invalid "Expected an optional HTTP body."

    let private addHeader (request: HttpRequestMessage) = function
        | VTuple [VString key; VString value] ->
            if value.Contains('\r') || value.Contains('\n') then
                invalid "HTTP header values cannot contain line breaks."
            if not (request.Headers.TryAddWithoutValidation(key, value)) then
                if isNull request.Content then
                    request.Content <- new ByteArrayContent(Array.empty)
                if not (request.Content.Headers.TryAddWithoutValidation(key, value)) then
                    invalid "Invalid HTTP request header."
        | _ -> invalid "Expected an HTTP header name and value."

    let private headers (source: Headers.HttpHeaders) =
        [ for entry in source do
            for value in entry.Value do
                yield VTuple [VString entry.Key; VString value] ]

    let private readBody (content: HttpContent) limit (cancellation: CancellationToken) = task {
        use! stream = content.ReadAsStreamAsync()
        use buffer = new MemoryStream()
        let chunk = Array.zeroCreate<byte> 8192
        let mutable finished = false
        while not finished do
            let countToRead = int (min (int64 chunk.Length) (limit - buffer.Length + 1L))
            let! count = stream.ReadAsync(chunk, 0, countToRead, cancellation)
            if count > 0 then buffer.Write(chunk, 0, count)
            finished <- count = 0 || buffer.Length > limit
        return
            if buffer.Length > limit then None
            else buffer.ToArray() |> Array.map (int64 >> VInt) |> Array.toList |> Some
    }

    let send (methodName: string) (url: string) options (cancellation: CancellationToken) = task {
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)
        try
            let uri =
                match Uri.TryCreate(url, UriKind.Absolute) with
                | true, uri when (uri.Scheme = "http" || uri.Scheme = "https") && uri.UserInfo = "" -> uri
                | _ -> invalid "Expected an absolute HTTP or HTTPS URL without user information."
            let fields = match options with VRecord fields -> fields | _ -> invalid "Expected an HTTP request record."
            let positiveInt field =
                match Map.tryFind field fields with
                | Some(VInt value) when value > 0L && value <= int64 Int32.MaxValue -> value
                | _ -> invalid (field + " must be a positive 32-bit integer.")
            let timeout = positiveInt "timeoutMilliseconds"
            let limit = positiveInt "maxResponseBytes"
            use request = new HttpRequestMessage(HttpMethod(methodName), uri)
            bodyContent fields.["body"] |> Option.iter (fun content -> request.Content <- content)
            match fields.["headers"] with
            | VList values -> List.iter (addHeader request) values
            | _ -> invalid "Expected a list of HTTP headers."
            deadline.CancelAfter(int timeout)
            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            let! bytes = readBody response.Content limit deadline.Token
            return
                match bytes with
                | None -> failed "ResponseTooLarge" "HTTP response exceeded maxResponseBytes."
                | Some values ->
                    result "Received" [
                        "statusCode", VInt(int64 response.StatusCode)
                        "headers", VList(headers response.Headers @ headers response.Content.Headers)
                        "body", VList values ]
        with
        | :? OperationCanceledException when cancellation.IsCancellationRequested ->
            return raise (OperationCanceledException(cancellation))
        | :? OperationCanceledException -> return failed "Timeout" "HTTP request timed out."
        | :? ArgumentException as error -> return failed "InvalidRequest" error.Message
        | :? FormatException as error -> return failed "InvalidRequest" error.Message
        | :? HttpRequestException as error -> return failed "Transport" error.Message
        | :? IOException as error -> return failed "Transport" error.Message
    }

/// CLR linkage for the .NET compiler backends.
[<AbstractClass; Sealed>]
type CompiledHttp private () =
    static member Defaults() = HttpTransport.defaults
    static member Send(methodName: string, url: string, options: ScriptValue) = HttpTransport.send methodName url options CancellationToken.None
    static member Get(url: string, options: ScriptValue) = CompiledHttp.Send("GET", url, options)
    static member Head(url: string, options: ScriptValue) = CompiledHttp.Send("HEAD", url, options)
    static member Post(url: string, options: ScriptValue) = CompiledHttp.Send("POST", url, options)
    static member Put(url: string, options: ScriptValue) = CompiledHttp.Send("PUT", url, options)
    static member Patch(url: string, options: ScriptValue) = CompiledHttp.Send("PATCH", url, options)
    static member Delete(url: string, options: ScriptValue) = CompiledHttp.Send("DELETE", url, options)

    static member FScriptCompiledFunctions() : IEnumerable<KeyValuePair<string,MethodInfo>> =
        [ for name in ["Defaults"; "Send"; "Get"; "Head"; "Post"; "Put"; "Patch"; "Delete"] do
            yield KeyValuePair(HttpTransport.name name, typeof<CompiledHttp>.GetMethod(name) |> nonNull) ]

module internal HttpExterns =
    let private headers = TList(TTuple [TString; TString])
    let private bytes = TList TInt
    let private requestFields =
        Map.ofList [ "headers", headers; "body", TOption(TNamed "HttpBody")
                     "timeoutMilliseconds", TInt; "maxResponseBytes", TInt ]
    let private request = TRecord requestFields
    let private received = TRecord(Map.ofList ["statusCode", TInt; "headers", headers; "body", bytes])
    let private failed = TRecord(Map.ofList ["kind", TString; "message", TString])
    let private methodType = TFun(TString, TFun(request, TTask(TNamed "HttpResult")))

    let private descriptor methodName =
        let name, scheme, arity =
            match methodName with
            | Some methodName -> HttpTransport.name methodName, methodType, 2
            | None -> "Http.send", TFun(TString, methodType), 3
        { Name = name
          Scheme = Forall([], scheme)
          Arity = arity
          Impl = fun context arguments ->
              let send name url options = context.ExposeAsync(HttpTransport.send name url options)
              match methodName, arguments with
              | None, [VString name; VString url; options] -> send name url options
              | Some name, [VString url; options] -> send name url options
              | _ -> raise (ExtensionException "Expected HTTP request arguments.") }

    let registration =
        let metadata: ExtensionRegistration =
            { HostInterop.empty with
                Types = [{ Name = "HttpRequest"; Fields = requestFields; HostManaged = false; TypeParameters = [] }]
                Unions = [
                    { Name = "HttpBody"; Cases = Map.ofList ["Text", Some TString; "Bytes", Some bytes] }
                    { Name = "HttpResult"; Cases = Map.ofList ["Received", Some received; "Failed", Some failed] } ]
                Functions =
                    { Name = "Http.defaults"; Scheme = Forall([], request); Arity = 0
                      Impl = fun _ _ -> HttpTransport.defaults }
                    :: descriptor None :: (HttpTransport.methods |> List.map (Some >> descriptor)) }
        let registration = ExtensionBridge.toLanguageRegistration metadata
        let functions =
            registration.Functions |> List.map (fun fn ->
                if fn.Name = "Http.defaults" then
                    { fn with RequiredPermissions = FScript.Language.HostPermissions.None }
                else
                    let required = FScript.Language.HostPermissions.Network
                    { fn with RequiredPermissions = required
                              Impl = fun context arguments ->
                                  context.CheckPermissions required
                                  fn.Impl context arguments })
        { registration with Functions = functions }
