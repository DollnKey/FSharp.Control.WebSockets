namespace FSharp.Control.Websockets

open System.Threading
open System.Threading.Tasks
open System.Runtime.ExceptionServices

type Async =
    static member AwaitTaskWithCancellation (f: CancellationToken -> Task) : Async<unit> = async {
        let! ct = Async.CancellationToken
        return! f ct |> Async.AwaitTask
    }

    static member AwaitTaskWithCancellation (f: CancellationToken -> Task<'a>) : Async<'a> = async {
        let! ct = Async.CancellationToken
        return! f ct |> Async.AwaitTask
    }

module Stream =
    open System
    type System.IO.MemoryStream with
        static member UTF8toMemoryStream (text : string) =
            new IO.MemoryStream(Text.Encoding.UTF8.GetBytes text)

        static member ToUTF8String (stream : IO.MemoryStream) =
            stream.Seek(0L,IO.SeekOrigin.Begin) |> ignore //ensure start of stream
            stream.ToArray()
            |> Text.Encoding.UTF8.GetString
            |> fun s -> s.TrimEnd(char 0) // remove null teriminating characters

        member stream.ToUTF8String () =
            stream |> System.IO.MemoryStream.ToUTF8String

module WebSocket =
    open Stream
    open System
    open System.Net.WebSockets

    /// **Description**
    /// (16 * 1024) = 16384
    /// https://referencesource.microsoft.com/#System/net/System/Net/WebSockets/WebSocketHelpers.cs,285b8b64a4da6851
    /// **Output Type**
    ///   * `int`
    [<Literal>]
    let defaultBufferSize  : int = 16384 // (16 * 1024)

    let asyncReceive  (buffer : ArraySegment<byte>) (websocket : #WebSocket) =
        fun ct ->  websocket.ReceiveAsync(buffer,ct)
        |> Async.AwaitTaskWithCancellation

    let asyncSend (buffer : ArraySegment<byte>) messageType endOfMessage (websocket : #WebSocket) =
        fun ct ->  websocket.SendAsync(buffer, messageType, endOfMessage, ct)
        |> Async.AwaitTaskWithCancellation

    let asyncClose status message (websocket : #WebSocket) =
        fun ct -> websocket.CloseAsync(status,message,ct)
        |> Async.AwaitTaskWithCancellation

    let asyncCloseOutput status message (websocket : #WebSocket) =
        fun ct -> websocket.CloseOutputAsync(status,message,ct)
        |> Async.AwaitTaskWithCancellation

    let isWebsocketOpen (socket : #WebSocket) =
        socket.State = WebSocketState.Open

    /// Sends a whole message to the websocket read from the given stream
    let sendMessage bufferSize messageType (readableStream : #IO.Stream) (socket : #WebSocket) = async {
        let buffer = Array.create (bufferSize) Byte.MinValue

        let rec sendMessage' () = async {
            if isWebsocketOpen socket then
                let! read =
                    readableStream.AsyncRead(buffer,0,buffer.Length)
                if read > 0 then
                    do! (socket |> asyncSend (ArraySegment(buffer |> Array.take read))  messageType false)
                    return! sendMessage'()
                else
                    do! (socket |> asyncSend (ArraySegment(Array.empty))  messageType true)
        }
        do! sendMessage'()
        }

    let sendMessageAsUTF8 text socket = async {
        use stream = IO.MemoryStream.UTF8toMemoryStream text
        do! sendMessage defaultBufferSize WebSocketMessageType.Text stream socket
    }



    type ReceiveStreamResult =
        | Stream of IO.Stream
        | StreamClosed of closeStatus: WebSocketCloseStatus * closeStatusDescription:string

    let receiveMessage bufferSize messageType (writeableStream : IO.Stream) (socket : WebSocket) = async {
        let buffer = new ArraySegment<Byte>( Array.create (bufferSize) Byte.MinValue)

        let rec readTillEnd' () = async {
            let! result  = socket |> asyncReceive buffer
            match result with
            | result when result.MessageType = WebSocketMessageType.Close || socket.State = WebSocketState.CloseReceived || socket.State = WebSocketState.CloseSent ->
                // printfn "Close received! %A - %A" socket.CloseStatus socket.CloseStatusDescription
                do! asyncCloseOutput WebSocketCloseStatus.NormalClosure "Close received by client" socket
                return StreamClosed(socket.CloseStatus.Value, socket.CloseStatusDescription)
            | result ->
                // printfn "result.MessageType -> %A" result.MessageType
                if result.MessageType <> messageType then return ()
                do! writeableStream.AsyncWrite(buffer.Array, buffer.Offset, result.Count)
                if result.EndOfMessage then
                    return Stream writeableStream
                else
                    return! readTillEnd' ()
        }
        return! readTillEnd' ()
    }

    type ReceiveUTF8Result =
        | String of string
        | StreamClosed of closeStatus: WebSocketCloseStatus * closeStatusDescription:string

    let receiveMessageAsUTF8 socket = async {
        use stream =  new IO.MemoryStream()
        let! result = receiveMessage defaultBufferSize WebSocketMessageType.Text stream socket
        match result with
        | ReceiveStreamResult.Stream s ->
            return stream |> IO.MemoryStream.ToUTF8String |> String
        | ReceiveStreamResult.StreamClosed(status, reason) ->
            return ReceiveUTF8Result.StreamClosed(status, reason)
    }

module ThreadSafeWebSocket =
    open System
    open System.Threading
    open System.Net.WebSockets
    open Stream

    type MessageSendResult = Result<unit, ExceptionDispatchInfo>
    type MessageReceiveResult = Result<WebSocket.ReceiveStreamResult, ExceptionDispatchInfo>

    type SendMessages =
    | Send of  bufferSize : int * WebSocketMessageType *  IO.Stream * AsyncReplyChannel<MessageSendResult>
    | Close of  WebSocketCloseStatus * string * AsyncReplyChannel<MessageSendResult>
    | CloseOutput of  WebSocketCloseStatus * string * AsyncReplyChannel<MessageSendResult>

    type ReceiveMessage =  int * WebSocketMessageType * IO.Stream  * AsyncReplyChannel<MessageReceiveResult>

    type ThreadSafeWebSocket =
        { websocket : WebSocket
          sendChannel : MailboxProcessor<SendMessages>
          receiveChannel : MailboxProcessor<ReceiveMessage>
        }
        interface IDisposable with
            member x.Dispose() =
                x.websocket.Dispose()
        member x.State =
            x.websocket.State
        member x.CloseStatus =
            x.websocket.CloseStatus |> Option.ofNullable
        member x.CloseStatusDescription =
            x.websocket.CloseStatusDescription

    let createFromWebSocket (webSocket : WebSocket) =
        /// handle executing a task in a try/catch and wrapping up the callstack info for later
        let inline wrap (action: Async<'a>) (reply: AsyncReplyChannel<_>) = async {
            try
                let! result = action
                reply.Reply(Ok result)
            with
            | ex ->
                let dispatch = ExceptionDispatchInfo.Capture ex
                reply.Reply(Error dispatch)
        }

        let sendAgent = MailboxProcessor<SendMessages>.Start(fun inbox ->
            let rec loop () = async {
                let! message = inbox.Receive()
                if webSocket |> WebSocket.isWebsocketOpen then
                    match message with
                    | Send (buffer, messageType, stream, replyChannel) ->
                        do! wrap (WebSocket.sendMessage buffer messageType stream webSocket) replyChannel
                        return! loop ()
                    | Close (status, message, replyChannel) ->
                        do! wrap (WebSocket.asyncClose status message webSocket) replyChannel
                    | CloseOutput (status, message, replyChannel) ->
                        do! wrap (WebSocket.asyncCloseOutput status message webSocket) replyChannel
            }
            loop ()
        )
        let receiveAgent = MailboxProcessor<ReceiveMessage>.Start(fun inbox ->
            let rec loop () = async {
                let! (buffer, messageType, stream, replyChannel) = inbox.Receive()
                if webSocket |> WebSocket.isWebsocketOpen then
                    let! result = WebSocket.receiveMessage buffer messageType stream webSocket
                    replyChannel.Reply (Ok result)
                    do! loop ()
            }
            loop ()
        )
        {
            websocket = webSocket
            sendChannel = sendAgent
            receiveChannel = receiveAgent
        }

    let sendMessage (wsts : ThreadSafeWebSocket) bufferSize messageType stream =
        wsts.sendChannel.PostAndAsyncReply(fun reply -> Send(bufferSize, messageType, stream, reply))

    let sendMessageAsUTF8(wsts : ThreadSafeWebSocket) (text : string) = async {
        use stream = IO.MemoryStream.UTF8toMemoryStream text
        return! sendMessage wsts  WebSocket.defaultBufferSize  WebSocketMessageType.Text stream
    }

    let receiveMessage (wsts : ThreadSafeWebSocket) bufferSize messageType stream =
        wsts.receiveChannel.PostAndAsyncReply(fun reply -> bufferSize, messageType, stream, reply)

    let receiveMessageAsUTF8 (wsts : ThreadSafeWebSocket) = async {
        use stream = new IO.MemoryStream()
        let! response = receiveMessage wsts WebSocket.defaultBufferSize  WebSocketMessageType.Text stream
        match response with
        | Ok (WebSocket.ReceiveStreamResult.Stream s) -> return stream |> IO.MemoryStream.ToUTF8String |> WebSocket.ReceiveUTF8Result.String |> Ok
        | Ok (WebSocket.ReceiveStreamResult.StreamClosed(status, reason)) ->
            return Ok (WebSocket.ReceiveUTF8Result.StreamClosed(status, reason))
        | Error ex -> return Error ex

    }

    let close (wsts : ThreadSafeWebSocket) status message =
        wsts.sendChannel.PostAndAsyncReply(fun reply -> Close(status, message, reply))

    let closeOutput (wsts : ThreadSafeWebSocket) status message =
        wsts.sendChannel.PostAndAsyncReply(fun reply -> CloseOutput(status, message, reply))
