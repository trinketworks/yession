namespace Yession.Manager

/// The HTTP contract of the Manager's control channel: every path a Session may call, declared
/// once. The Manager's `Control` matches over this and the Session's `ControlClient` addresses
/// through it — the same role `ManagerRoute` plays for the management surface, and it lives
/// here rather than beside it because this is the contract the two bins share, and `Session`
/// references this project and not the App's.
///
/// Before this, the server spelled `"POST", "/control/secrets/set"` and the client
/// `sprintf "%s/control/secrets/%s"`, two copies of one contract that nothing tied together,
/// so a typo at either end was a 404 found at runtime.
///
/// Root-anchored, for the reason `ManagerRoute` is: the Manager lives at its origin root. The
/// METHOD is part of the route — `/control/connections` is a POST family and a GET stream — so
/// a path reached with the wrong method is no route at all. What each leg carries is the
/// codecs' business (`ControlWire`, `McpWire`); this says only where they go.
[<RequireQualifiedAccess>]
type ControlRoute =
    /// The session's self-assigned display name.
    | Name
    /// The one line a session says about itself, for the roster.
    | Summary
    /// Whether the session is in use (Plan 11).
    | Activity
    /// Dynamic client registration: bind this launch's OAuth client to its secret.
    | RegisterClient
    /// Declare what to forward from the Manager's hook endpoints.
    | SubscribeHook
    /// Stop one such declaration.
    | UnsubscribeHook
    | SetSecret
    | ListSecrets
    | DeleteSecret
    /// The one value-returning secrets route.
    | ResolveSecret
    | BeginConnection
    | CompleteConnection
    | PutConnection
    | PutConnectionGrant
    | DisconnectConnection
    | RejectConnection
    /// The one value-returning connections route.
    | ResolveConnection
    /// The reverse leg: notifications pushed down to the session, as SSE.
    | Notifications
    /// The second reverse leg: this session's resolved MCP server set, as SSE.
    | McpServers
    /// The third reverse leg: the caller's readable connection statuses, as SSE.
    | ConnectionStatuses

module ControlRoute =

    /// Every route, once. `parse` reads this rather than spelling each path a second time,
    /// so a route has exactly one method and one path; a case missing here is a route the
    /// Manager 404s, which the suite's ordinal check refuses to let pass.
    let all : ControlRoute list =
        [ ControlRoute.Name
          ControlRoute.Summary
          ControlRoute.Activity
          ControlRoute.RegisterClient
          ControlRoute.SubscribeHook
          ControlRoute.UnsubscribeHook
          ControlRoute.SetSecret
          ControlRoute.ListSecrets
          ControlRoute.DeleteSecret
          ControlRoute.ResolveSecret
          ControlRoute.BeginConnection
          ControlRoute.CompleteConnection
          ControlRoute.PutConnection
          ControlRoute.PutConnectionGrant
          ControlRoute.DisconnectConnection
          ControlRoute.RejectConnection
          ControlRoute.ResolveConnection
          ControlRoute.Notifications
          ControlRoute.McpServers
          ControlRoute.ConnectionStatuses ]

    /// The HTTP method a route is called with.
    let method (route: ControlRoute) : string =
        match route with
        | ControlRoute.Notifications
        | ControlRoute.McpServers
        | ControlRoute.ConnectionStatuses -> "GET"
        | ControlRoute.Name
        | ControlRoute.Summary
        | ControlRoute.Activity
        | ControlRoute.RegisterClient
        | ControlRoute.SubscribeHook
        | ControlRoute.UnsubscribeHook
        | ControlRoute.SetSecret
        | ControlRoute.ListSecrets
        | ControlRoute.DeleteSecret
        | ControlRoute.ResolveSecret
        | ControlRoute.BeginConnection
        | ControlRoute.CompleteConnection
        | ControlRoute.PutConnection
        | ControlRoute.PutConnectionGrant
        | ControlRoute.DisconnectConnection
        | ControlRoute.RejectConnection
        | ControlRoute.ResolveConnection -> "POST"

    /// A route as the root-anchored path the Manager serves it at: always begins with
    /// `/control/`, which is how `Control` knows a request is for it at all.
    let path (route: ControlRoute) : string =
        match route with
        | ControlRoute.Name -> "/control/name"
        | ControlRoute.Summary -> "/control/summary"
        | ControlRoute.Activity -> "/control/activity"
        | ControlRoute.RegisterClient -> "/control/register-client"
        | ControlRoute.SubscribeHook -> "/control/hooks/subscribe"
        | ControlRoute.UnsubscribeHook -> "/control/hooks/unsubscribe"
        | ControlRoute.SetSecret -> "/control/secrets/set"
        | ControlRoute.ListSecrets -> "/control/secrets/list"
        | ControlRoute.DeleteSecret -> "/control/secrets/delete"
        | ControlRoute.ResolveSecret -> "/control/secrets/resolve"
        | ControlRoute.BeginConnection -> "/control/connections/begin"
        | ControlRoute.CompleteConnection -> "/control/connections/complete"
        | ControlRoute.PutConnection -> "/control/connections/put"
        | ControlRoute.PutConnectionGrant -> "/control/connections/put-grant"
        | ControlRoute.DisconnectConnection -> "/control/connections/disconnect"
        | ControlRoute.RejectConnection -> "/control/connections/reject"
        | ControlRoute.ResolveConnection -> "/control/connections/resolve"
        | ControlRoute.Notifications -> "/control/notifications"
        | ControlRoute.McpServers -> "/control/mcp"
        | ControlRoute.ConnectionStatuses -> "/control/connections"

    /// A route as an absolute URL at a Manager's control endpoint. The join lives here, so a
    /// base given with or without its trailing slash reads the same.
    let at (baseUrl: string) (route: ControlRoute) : string =
        baseUrl.TrimEnd '/' + path route

    /// The route a request is for, or `None` when the control channel claims nothing there —
    /// an unknown path, or a known one reached with the wrong method. Exact: no trailing
    /// slash, no case folding, as the literal matches it replaces.
    let parse (httpMethod: string) (requestPath: string) : ControlRoute option =
        all |> List.tryFind (fun route -> method route = httpMethod && path route = requestPath)
