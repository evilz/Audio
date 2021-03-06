module Giraffe.WebSocket

open System
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks.ContextInsensitive

/// WebSocket subprotocol type. For negotiation of subprotocols.
type WebSocketSubprotocol = {
    /// The subprotocol name.
    Name : string
}

/// Default WebSocket communication options.
let DefaultWebSocketOptions =
    let webSocketOptions = Microsoft.AspNetCore.Builder.WebSocketOptions()
    webSocketOptions.KeepAliveInterval <- TimeSpan.FromSeconds 120.
    webSocketOptions.ReceiveBufferSize <- 4 * 1024
    webSocketOptions

/// Internal reference to a WebSocket. Includes WebSocketID and Subprotocol.
type WebSocketReference = {
    /// A reference to the WebSocket.
    WebSocket : WebSocket
    /// The selected subprotocol.
    Subprotocol : WebSocketSubprotocol option
    /// The internal ID of the WebSocket.
    ID : string
    /// The key filter for broadcast actions
    KeyFilter : string option
    /// Task that will be started after websocket is closed
    OnClose : unit -> Task<unit>
}
    with
        /// Sends a UTF-8 encoded text message to the WebSocket client.
        member this.SendTextAsync(msg:string,?cancellationToken) = task {
            if not (isNull this.WebSocket) then
                try
                    if this.WebSocket.State = WebSocketState.Open then
                        let byteResponse = System.Text.Encoding.UTF8.GetBytes msg
                        let segment = ArraySegment<byte>(byteResponse, 0, byteResponse.Length)
                        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
                        do! this.WebSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken)
                with
                | _ ->
                    // TODO: Tracing
                    ()
        }


        /// Closes the connection to the WebSocket client.
        member this.CloseAsync(?reason,?cancellationToken) = task {
            if not (isNull this.WebSocket) then
                try
                    if this.WebSocket.State = WebSocketState.Open then
                        let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
                        let reason = reason |> Option.defaultValue "Closed by the WebSocket server"
                        do! this.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken)
                with
                | _ ->
                    // TODO: Tracing
                    ()
        }


        /// Creates a new reference to a WebSocket.
        static member FromWebSocket(websocket,onClose,?webSocketID,?keyFilter,?subProtocol) : WebSocketReference = {
            WebSocket  = websocket
            Subprotocol = subProtocol
            OnClose = onClose
            KeyFilter = keyFilter
            ID = webSocketID |> Option.defaultWith (fun _ -> Guid.NewGuid().ToString())
        }

/// A connection manager keeps track of all connections that are open at a specific endpoint.
type ConnectionManager(?messageSize) =
    let messageSize = defaultArg messageSize DefaultWebSocketOptions.ReceiveBufferSize

    let connections = new System.Collections.Concurrent.ConcurrentDictionary<string, WebSocketReference>()


    with

        /// Tries to find a WebSocket by ID. Returns None if the socket wasn't found.
        member __.TryGetWebSocket(websocketID:string) : WebSocketReference option =
            match connections.TryGetValue websocketID with
            | true, r -> Some r
            | _ -> None

        /// Returns the number of WebSocket connections.
        member __.Count = connections.Count

        /// Sends a UTF-8 encoded text message to all WebSocket connections.
        member __.BroadcastTextAsync(msg:string,?key,?cancellationToken:CancellationToken) = task {
            let byteResponse = System.Text.Encoding.UTF8.GetBytes msg

            let matchesKey (reference:WebSocketReference) =
                match key with
                | Some key ->
                    match reference.KeyFilter with
                    | Some referenceKey when referenceKey = key -> true
                    | None -> true
                    | _ -> false
                | _ -> true

            let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
            let toRemove = System.Collections.Concurrent.ConcurrentBag<_>()
            let! _ =
                connections
                |> Seq.map (fun kv -> task {
                    try
                        if not cancellationToken.IsCancellationRequested then
                            let webSocket = kv.Value.WebSocket
                            if webSocket.State = WebSocketState.Open && matchesKey kv.Value then
                                try
                                    let segment = ArraySegment<byte>(byteResponse, 0, byteResponse.Length)
                                    do! webSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken)
                                with
                                | _ ->
                                    // TODO: Tracing
                                    ()
                            else
                                toRemove.Add kv.Key
                    with
                    | _ -> () })
                |> Task.WhenAll

            for key in toRemove do
                match connections.TryRemove key with
                | true, reference ->
                    do! reference.OnClose()
                | _ -> ()

            return ()
        }

        /// Creates a new WebSocket connection and negotiates the subprotocol.
        member __.OpenSocket(ctx : Microsoft.AspNetCore.Http.HttpContext,onConnected:WebSocketReference ->Task<unit>,onMessage: WebSocketReference -> string -> Task<unit>,onClose:unit ->Task<unit>,?webSocketID,?keyFilter,?supportedProtocols:seq<WebSocketSubprotocol>,?cancellationToken) = task {
            try
                if not ctx.WebSockets.IsWebSocketRequest then return None else

                let requestedSubProtocols = ctx.WebSockets.WebSocketRequestedProtocols
                let! (websocket : WebSocket) =
                    match supportedProtocols with
                    | Some supportedProtocols when requestedSubProtocols |> Seq.isEmpty |> not ->
                        match supportedProtocols |> Seq.tryFind (fun supported -> requestedSubProtocols |> Seq.contains supported.Name) with
                        | Some subProtocol ->
                            ctx.WebSockets.AcceptWebSocketAsync(subProtocol.Name)
                        | None ->
                            failwithf "Unsupported protocol"
                    | _ ->
                        ctx.WebSockets.AcceptWebSocketAsync()

                let webSocketID = webSocketID |> Option.defaultWith (fun _ -> Guid.NewGuid().ToString())
                let reference = WebSocketReference.FromWebSocket(websocket,onClose,webSocketID=webSocketID)
                connections.AddOrUpdate(reference.ID, reference, fun _ _ -> reference) |> ignore
                let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
                do! onConnected (reference)

                let mutable finished = false

                while not finished && websocket.State = WebSocketState.Open do
                    try
                        let buffer = Array.zeroCreate messageSize
                        let! received = websocket.ReceiveAsync(ArraySegment<byte> buffer, cancellationToken)
                        finished <- received.CloseStatus.HasValue
                        if finished then
                            if websocket.State = WebSocketState.Open then
                                do! websocket.CloseAsync(received.CloseStatus.Value, received.CloseStatusDescription, cancellationToken)
                        else
                            if received.EndOfMessage then
                                match received.MessageType with
                                | WebSocketMessageType.Binary ->
                                    raise (NotImplementedException())
                                | WebSocketMessageType.Text ->
                                    let! _r =
                                        ArraySegment<byte>(buffer, 0, received.Count).Array
                                        |> System.Text.Encoding.UTF8.GetString
                                        |> fun s -> s.TrimEnd(char 0)
                                        |> onMessage reference
                                    ()
                                | _ ->
                                    raise (NotImplementedException())
                    with
                    | _ ->
                        //TODO: Use giraffe/aspnet logging
                        finished <- true

                match connections.TryRemove webSocketID with
                | true, reference ->
                    do! reference.OnClose()
                | _ -> ()

                return Some ctx
            with
            | _ -> return None
        }