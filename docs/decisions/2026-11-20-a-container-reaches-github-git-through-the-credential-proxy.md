# A container's git reaches github.com through the credential proxy

> Decided 2026-11-20 · Related: `GitGateway.Door`, `GitGateway.proxiedGitConfig` and
> `Gateway.Serve` in `app/GitGateway.fs`, `HandledRoute` in `app/CredentialProxy.fs`,
> `GitHubAccess.gitRoute` and `GitHubAccess.gitDoor` in `app/GitHubAccess.fs`, invariant 21

## Context

A sandbox's git reached the git gateway by one `url.http://<door>/git/<cap>/github.com/.insteadOf
https://github.com/`, given through `GIT_CONFIG_*`. apm (microsoft/apm) since 0.29.1 refuses
any `insteadOf` that moves a github.com URL to another host or origin, HTTP or HTTPS, and has
no setting to allow it (`validate_resolved_git_url_rewrite` in `src/apm_cli/utils/git_env.py`).
So `apm install` failed in every container sandbox that `uses: [github]`, for public
dependencies too, with "HTTPS Git remote must not rewrite to insecure HTTP". An HTTPS door
would not help: the host still changes. apm keeps proxy configuration (its `git_env.py`
module docstring), and does not treat a proxy as a rewrite.

## Decision

The credential proxy also terminates TLS for `github.com`, as a host a provider HANDLES
(`HandledRoute`), not as a host it declares a stand-in for. On github.com:

- git's three smart-HTTP requests (`GET …/info/refs?service=git-upload-pack|git-receive-pack`,
  `POST …/git-upload-pack`, `POST …/git-receive-pack`) go to the git gateway's handler
  (`Gateway.Serve`). Its semantics are unchanged: the credential of the request's live loan,
  resolved per request; no credential without a loan; refusals as `ERR` packets in words.
- Every other path goes up as it came, with NO credential, no stand-in swapped, and without
  the loan header (the web, archive and asset redirects, LFS).
- The sandbox is named by its proxy admission (`Admit`), which the cap in the listener's URL
  names on the other door.

A sandbox the proxy admits (docker, and the unconfined host) gets no `insteadOf`. Its git keeps
`https://github.com/` and is told, through `GIT_CONFIG_*` and scoped to github.com alone,
`http.https://github.com/.proxy` (its proxy URL, the admission in the credentials) and
`http.https://github.com/.sslCAInfo` (the proxy's bundle, mounted at
`/run/yession/proxy/bundle.pem` in a container). A block's loan is
`http.https://github.com/.extraheader`.

srt and seatbelt sandboxes (never admitted to the proxy: their egress is their policy) and the
session's own git sandbox keep the `insteadOf` door to the gateway's own listener. Two doors,
one handler.

## Why

- **The loan is added only on git's three paths.** A github.com route in the proxy that
  swapped the `GH_TOKEN` stand-in would make the stand-in a whole-token credential for the
  web, archives and LFS. A handled host swaps nothing; the credential reaches exactly what the
  gateway reached before.
- **Git config, not `HTTPS_PROXY`.** A container sees `HTTPS_PROXY` only when its declaration
  asks for `${proxy.https}`. A sandbox that uses github's git route and not its api route
  would otherwise lose every credential. Scoped to github.com, the config changes nothing
  about git's other hosts, and `http.<url>.proxy` wins over the environment's.
- **The trust file named to git.** libcurl as Ubuntu builds it (and Debian's, in the Docker
  tier's `node:24`) reads its compiled-in bundle and ignores `SSL_CERT_FILE`. Measured: git
  2.43 / libcurl 8.5 through a TLS-terminating proxy failed verification with `SSL_CERT_FILE`
  alone, and passed with `GIT_SSL_CAINFO` or `http.<url>.sslCAInfo`.
- **The proxy URL's credentials work.** libcurl sends no `Proxy-Authorization` until it is
  answered `407` with `Proxy-Authenticate: Basic`, which the proxy's TCP door answers, and then
  reconnects with Basic. Measured with git 2.43 / libcurl 8.5.
- **Through apm's sanitizer.** apm rebuilds a git child's `GIT_CONFIG_*` from the effective
  config, and keeps `http.<url>.proxy` and `http.<url>.sslCAInfo` (read from its source:
  `_materialize_git_config_snapshot` drops only includes, local scope and credential-bearing
  `extraheader`s). Its rule for a credential-bearing header does not match `X-Yession-Loan` by
  name, so a block's loan may reach apm's git too. Whether it does was not exercised: a public
  dependency reads with no loan either way.

## The checks made before building

1. git (libcurl) through the proxy with the admission in the URL's credentials, trusting the
   proxy through `http.<url>.sslCAInfo`: works (above). `SSL_CERT_FILE` alone does not.
2. apm's own HTTP (Python requests) through the proxy: apm injects `truststore` and keeps a
   `SSL_CERT_FILE` it did not set itself; `truststore` on Linux calls
   `set_default_verify_paths()`, which honours it. Measured with requests and truststore 0.10:
   through a TLS-terminating proxy, plain requests failed with `SSL_CERT_FILE` alone and passed
   with truststore injected. So no `REQUESTS_CA_BUNDLE` is needed while truststore is in use;
   under `APM_DISABLE_TRUSTSTORE` apm would need one. This matters only where a declaration
   sends apm's HTTP through the proxy (`HTTPS_PROXY: ${proxy.https}`), and that declaration
   already sets `SSL_CERT_FILE: ${proxy.ca-file}` (`ConfigAnalysis`).
3. libgit2 clients (cargo's git fetch, pygit2): not measured. libgit2 does not read
   `GIT_CONFIG_*`, so neither the old `insteadOf` nor the new proxy config reached it; it goes
   to github.com directly with no credential, as before.

## Consequences

- An environment `GIT_SSL_CAINFO` wins over `http.sslCAInfo` in git. A container image or a
  declaration that sets one must include the proxy's authority (`${proxy.ca-file}` is the
  whole bundle), or git refuses github.com.
- A `NO_PROXY` that names github.com sends git around the proxy, to github.com with no
  credential.
- A private apm dependency reads only if the block's loan survives apm's auth fence (above),
  or apm's own token handling supplies a credential. Before, apm refused the rewrite first.
