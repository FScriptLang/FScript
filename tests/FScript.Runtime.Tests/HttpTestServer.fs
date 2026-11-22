namespace FScript.Runtime.Tests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open NUnit.Framework

/// One-request HTTP server with an ephemeral loopback port.
type HttpTestServer(respond: Stream -> CancellationToken -> System.Threading.Tasks.Task) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let cancellation = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
    do listener.Start()
    let exchange = task {
        use! client = listener.AcceptTcpClientAsync(cancellation.Token)
        use stream = client.GetStream()
        use reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true)
        let! request = reader.ReadLineAsync(cancellation.Token)
        let requestHeaders = ResizeArray<string>()
        let mutable reading = true
        while reading do
            let! line = reader.ReadLineAsync(cancellation.Token)
            if String.IsNullOrEmpty line then reading <- false
            else requestHeaders.Add(nonNull line)
        let length =
            requestHeaders
            |> Seq.tryPick (fun line ->
                if line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                    Some(Int32.Parse(line.Substring(15)))
                else None)
            |> Option.defaultValue 0
        let body = Array.zeroCreate<char> length
        if length > 0 then
            let! read = reader.ReadBlockAsync(body.AsMemory(), cancellation.Token)
            Assert.That(read, Is.EqualTo(length))
        do! respond stream cancellation.Token
        return request, List.ofSeq requestHeaders, String body
    }
    static member Reply(wire: string) =
        fun (stream: Stream) token -> stream.WriteAsync(Encoding.UTF8.GetBytes(wire).AsMemory(), token).AsTask()

    member _.Url = $"http://127.0.0.1:{(listener.LocalEndpoint :?> IPEndPoint).Port}/test?q=1"
    member _.Request = exchange
    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            listener.Stop()
            cancellation.Dispose()
