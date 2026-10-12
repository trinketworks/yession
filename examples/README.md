# Examples

Integrations, built the way somebody outside this repository would build them.

Everything here is **standalone**. An example references nothing from `Yession.Domain` or
`Yession.Host`. It has its own project and its own bundle. The npm package and the Nix
installable do not carry it. This separation matters because an example that reaches into the
product's internals demonstrates a path that only this repository can walk.

Build one from the repository root, whichever ecosystem it belongs to:

```bash
dotnet fsi tasks.fsx example serial        # Fable + esbuild
dotnet fsi tasks.fsx example jumpstarter   # uv, and its own pytest suite
dotnet fsi tasks.fsx example proxy         # plain Node; nothing to build, so it only parses it
dotnet fsi tasks.fsx example nix-store     # shell scripts; nothing to build, so it only parses them
```

| | |
|---|---|
| [serial](serial/) | An MCP server that lends the host's serial devices to an agent. Shows the shape of a provider that owns a **resource**: arbitration, a session-scoped claim, and a second transport for bytes that tool calls cannot carry. |
| [jumpstarter](jumpstarter/) | An MCP server that lends a [Jumpstarter](https://jumpstarter.dev) exporter to an agent. Shows a provider that owns a **service** and is written in the language of that service's SDK: Python here. |
| [proxy](proxy/) | The other direction: a session does not talk to it. It feeds a deployment's **reverse proxy**. It renders the registry stream into the proxy's own config through a template. It also shows one complete Caddy composition, behind `tailscale serve`, that translates the Tailscale identity headers. |
| [nix-store](nix-store/) | Not an integration but an operator's half of one. It has the `pin` that a session runs inside each sandbox holding a shared Nix store. It also has the `sweep` that collects the store without deleting the shell of any sandbox. |

## Why a provider rather than a feature

Yession declares MCP servers by URL, and a session's own MCP client does the rest. Nothing in
the product knows what serial is. A provider is therefore not a plugin with a blessed
interface to conform to. It is a server. If your integration can answer `initialize`,
`tools/list`, and `tools/call` over HTTP, you can already declare it.

The interesting design questions are therefore not about Yession. The serial example works
through them: who owns a resource that the OS hands out exactly once, what happens to that
claim when the client crashes, and how you carry a stream when your protocol is
request/response.
