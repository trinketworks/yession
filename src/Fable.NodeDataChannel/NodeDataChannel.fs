module Fable.NodeDataChannel

// Fable bindings to the `node-datachannel` npm package (libdatachannel's Node addon). The
// binding layer only: the slice of its surface the session's WebRTC transport uses.
//
// SHAPES ONLY, and no `[<Import>]` anywhere, deliberately. The addon is native: a static
// `import` loads its `.node` binary at module-eval, which would force the cheap test tier —
// pure/model/protocol tests that never open a WebRTC connection — to build and ship that
// binary just to LOAD the bundle. So what `require('node-datachannel')` answers with is
// declared here as `Exports`, and the Host loads the module lazily, on the first real
// connection, and views it through that.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core

type [<AllowNullLiteral>] LocalDescription =
    abstract ``type`` : string
    abstract sdp : string

type [<AllowNullLiteral>] DataChannel =
    abstract sendMessage : string -> bool
    abstract close : unit -> unit
    abstract isOpen : unit -> bool
    abstract getLabel : unit -> string
    abstract onOpen : (unit -> unit) -> unit
    abstract onClosed : (unit -> unit) -> unit
    abstract onError : (string -> unit) -> unit
    abstract onMessage : (string -> unit) -> unit

type [<AllowNullLiteral>] PeerConnection =
    abstract close : unit -> unit
    abstract setLocalDescription : unit -> unit
    abstract setRemoteDescription : string * string -> unit
    abstract localDescription : unit -> LocalDescription
    abstract createDataChannel : string -> DataChannel
    abstract state : unit -> string
    abstract gatheringState : unit -> string
    abstract onLocalDescription : (string -> string -> unit) -> unit
    abstract onStateChange : (string -> unit) -> unit
    abstract onGatheringStateChange : (string -> unit) -> unit
    abstract onDataChannel : (DataChannel -> unit) -> unit

/// A peer connection's configuration — the one field this repository sets. Empty
/// `iceServers` means no STUN and no TURN: gathering stops at host candidates.
[<RequireQualifiedAccess>]
type PeerConnectionConfig =
    { iceServers : string array }

/// The `PeerConnection` class as the module exports it: `new PeerConnection(name, config)`.
///
/// The receiver is parenthesised because Fable pastes the caller's TEXT for `$0`, and
/// `new f().Class(x)` is `(new f()).Class(x)` to JavaScript — the `new` binds to the call,
/// and the class is then invoked without one. That is how a lazily required module's
/// class threw "Class constructors cannot be invoked without 'new'" on every real
/// connection while the cheap tier, which opens none, stayed green.
type [<AllowNullLiteral>] PeerConnectionClass =
    [<Emit("new ($0)($1, $2)")>]
    abstract Create : name: string * config: PeerConnectionConfig -> PeerConnection

/// What `require('node-datachannel')` answers with.
type [<AllowNullLiteral>] Exports =
    abstract PeerConnection : PeerConnectionClass
    /// libdatachannel's global teardown.
    abstract cleanup : unit -> unit
