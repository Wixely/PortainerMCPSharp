# PortainerMCPSharp

A standalone C# **MCP (Model Context Protocol) server** for **[Portainer](https://www.portainer.io/)** over Streamable HTTP. It speaks Portainer's own REST API and its Docker proxy, so an agent can deploy and redeploy stacks, then check the containers actually came up — and read their logs when they did not.

The centre of gravity is **stacks**. Everything else exists to support them.

Default port: **5716**. MCP endpoint: `http://localhost:5716/mcp`.

## Features

- **Full stack lifecycle** — create from inline compose or from a git repository, update, redeploy from git, start, stop, delete, migrate between environments, and set individual environment variables without restating the rest.
- **Deploy verification.** A 2xx from Portainer means *accepted*, not *running*. Pass `waitSeconds` to any deploy tool and it polls until the stack settles, then reports each container's state and points at the ones that failed.
- **Read-only by default** — nothing changes until you set `Portainer:ReadOnly=false`, and the destructive tail (delete, prune, volume removal) needs a second gate on top of that.
- **Self-protection.** The server refuses to stop or remove Portainer's own container, because doing so severs the connection it is using and cannot be undone remotely.
- **Secret redaction.** Portainer returns stack and container environments in cleartext. Secret-looking values are masked on the way out so they do not land in an agent's context, logs, and transcript.
- **Multiple instances.** Address several Portainers by alias, for estates that keep prod and dev separate.
- **Full Docker control** through Portainer's proxy: containers, images, volumes, networks, Swarm services and nodes.
- Configuration via `PortainerMCPSharp.json`, environment variables, or command line.
- Serilog logging to console and rolling files (daily + 50 MB rollover, 14-file retention).
- Runs as a console app, Windows Service, or Docker container.

## Quick start

Create an API token in Portainer under **My account → Access tokens**, then:

```sh
dotnet run -- \
  --Portainer:Instances:0:BaseUrl=https://portainer.example.com/ \
  --Portainer:Instances:0:ApiKey=ptr_your_token_here \
  --Portainer:ReadOnly=false
```

Point your MCP client at `http://localhost:5716/mcp`. `http://localhost:5716/healthz` confirms the server is up and lists the configured instances.

A typical first exchange: `portainer_list_environments` to find the environment id, `portainer_list_stacks` to see what is deployed, then `portainer_get_stack_file` to read a stack's compose.

Startup logs the operating posture so a misconfigured deployment is obvious:

```text
PortainerMCPSharp startup
  Endpoint: http://localhost:5716/mcp
  Transport: HTTP (Streamable)
  Mode: Console
  Read-only: False
  Instance: prod (default) https://portainer.example.com/ (ApiKey)
  Stack deploy: True   lifecycle: True   delete: False
  Container lifecycle: True   create: False   removal: False
  Image pull: True   image removal: False   prune: False
  Volume mgmt: False   network mgmt: False   swarm mutation: False
  Protect Portainer itself: True
  Redact secrets: True
  Content root: C:\Services\PortainerMCPSharp
```

### Docker

```sh
docker run --rm -p 5716:5716 \
  -e PORTAINERMCP_Portainer__Instances__0__Alias=prod \
  -e PORTAINERMCP_Portainer__Instances__0__BaseUrl=https://portainer.example.com/ \
  -e PORTAINERMCP_Portainer__Instances__0__ApiKey=ptr_your_token_here \
  -e PORTAINERMCP_Portainer__ReadOnly=false \
  -e PORTAINERMCP_Server__Password=change-me \
  ghcr.io/wixely/portainermcpsharp:latest
```

### Windows Service

```bat
sc.exe create PortainerMCPSharp binPath= "C:\Services\PortainerMCPSharp\PortainerMCPSharp.exe"
```

## Authentication

Portainer accepts two credentials, and they are **not** interchangeable:

| Credential | Header | Notes |
| --- | --- | --- |
| API token (`ptr_…`) | `X-API-KEY` | Preferred. Long-lived, revocable, cannot expire mid-run. |
| Username + password | `Authorization: Bearer <jwt>` | Exchanged for a short-lived JWT via `POST /api/auth`, refreshed automatically on a 401. |

Set `ApiKey` **or** `Username`+`Password` on each instance. The server picks the header from whichever is configured — putting an API token in a `Bearer` header returns 401 despite the token being valid, and making the choice structural means that cannot happen here.

`Password` accepts a `file:<path>` prefix so it need not sit in a JSON file.

## Configuration

Configure via `PortainerMCPSharp.json` or environment variables. Environment variables win over JSON; in Docker use the `PORTAINERMCP_` prefix and `__` for nested keys.

### Instances

| Setting | Default | Description |
| --- | --- | --- |
| `Portainer:Instances[]:Alias` | auto | Handle the agent passes as `alias`. Generated (`portainer-1`, …) when blank. |
| `Portainer:Instances[]:BaseUrl` | _(none)_ | Portainer base URL, e.g. `https://portainer.example.com/`. |
| `Portainer:Instances[]:ApiKey` | _(none)_ | API token, sent as `X-API-KEY`. Preferred. |
| `Portainer:Instances[]:Username` / `:Password` | _(none)_ | Used only when `ApiKey` is blank. `Password` supports `file:<path>`. |
| `Portainer:Instances[]:Description` | _(none)_ | Free text shown by `portainer_list_instances`. |
| `Portainer:Instances[]:AllowInvalidCertificate` | `false` | Skip TLS verification — for Portainer's default self-signed certificate. |
| `Portainer:DefaultAlias` | first entry | Instance used when a tool omits `alias`. |

### Safety

| Setting | Default | Description |
| --- | --- | --- |
| `Portainer:ReadOnly` | `true` | Master switch. When `true` nothing can be changed. **Set `false` to deploy.** |
| `Portainer:AllowStackDeploy` | `true` | Create / update / redeploy stacks, set env vars, change git config. |
| `Portainer:AllowStackLifecycle` | `true` | Start / stop stacks. |
| `Portainer:AllowStackDelete` | `false` | Delete a stack. |
| `Portainer:AllowContainerLifecycle` | `true` | Start / stop / restart / kill / rename containers. |
| `Portainer:AllowContainerCreate` | `false` | Create containers outside a stack. |
| `Portainer:AllowContainerRemoval` | `false` | Remove containers. |
| `Portainer:AllowImagePull` | `true` | Pull images. |
| `Portainer:AllowImageRemoval` | `false` | Remove images. |
| `Portainer:AllowVolumeManagement` | `false` | Create / remove volumes, and `removeVolumes` on stack delete. **Destroys data.** |
| `Portainer:AllowNetworkManagement` | `false` | Create / remove networks, connect / disconnect containers. |
| `Portainer:AllowPrune` | `false` | Every `*_prune_*` tool. |
| `Portainer:AllowSwarmMutation` | `false` | Service scale/update and node drain. |
| `Portainer:AllowWebhookTrigger` | `true` | Fire a stack's redeploy webhook. |
| `Portainer:ProtectPortainerItself` | `true` | Refuse to mutate Portainer's own container or agent. |
| `Portainer:RedactSecrets` | `true` | Mask secret-looking environment values in output. |
| `Portainer:SecretKeyPattern` | see JSON | Regex matched against variable names to decide what to mask. |

Every mutating tool requires `ReadOnly=false` **and** its own gate. Refusals name the exact keys:

```text
MCP tool 'portainer_delete_stack' requires Portainer:AllowStackDelete=true
(in addition to Portainer:ReadOnly=false). Deleting a stack removes its containers.
```

### Scoping and limits

| Setting | Default | Description |
| --- | --- | --- |
| `Portainer:AllowedEnvironmentIds` / `BlockedEnvironmentIds` | _(empty)_ | Restrict which environments are reachable. Empty allow-list = no restriction. |
| `Portainer:AllowedStacks` / `BlockedStacks` | _(empty)_ | Restrict by stack name, case-insensitive. |
| `Portainer:BlockedContainers` | _(empty)_ | Container names that must never be mutated. |
| `Portainer:DefaultEnvironmentId` | _(none)_ | Environment used when a tool omits `environmentId`. |
| `Portainer:EnableStacks` / `EnableEnvironments` / `EnableContainers` / `EnableImages` / `EnableVolumes` / `EnableNetworks` / `EnableSwarm` / `EnableRegistries` / `EnableSystem` | `true` | Remove whole tool categories. |
| `Portainer:MaxItems` | `1000` | Cap on items per list call. |
| `Portainer:MaxLogBytes` | `200000` | Cap on log bytes per call. |
| `Portainer:MaxJsonChars` | `60000` | Cap on any single JSON blob. |
| `Portainer:MaxStackFileChars` | `200000` | Cap on compose file content. |
| `Portainer:DefaultWaitSeconds` / `MaxWaitSeconds` | `60` / `600` | Deploy-verification wait, and its ceiling. |
| `Portainer:PollIntervalMs` | `1500` | Poll interval while waiting for a stack to settle. |
| `Portainer:RequestTimeoutSeconds` | `120` | HTTP timeout for calls to Portainer. |
| `Server:Host` / `Port` / `Path` | `localhost` / `5716` / `/mcp` | HTTP bind and MCP route. |
| `Server:Password` | `""` | Optional MCP endpoint password (`Authorization: Bearer`, Basic, or `X-MCP-Password`). |
| `Server:WindowsServiceName` | `PortainerMCPSharp` | SCM service name. |

## Tools

67 tools. `portainer_*` talks to Portainer's own API; `docker_*` is forwarded through Portainer's Docker proxy to the Docker Engine.

### Instances and system
`portainer_list_instances` · `portainer_system_version` · `portainer_system_status` · `portainer_system_info`

### Environments
`portainer_list_environments` · `portainer_get_environment` · `portainer_get_environment_dashboard` · `portainer_refresh_environment_snapshot`

### Stacks — read
`portainer_list_stacks` · `portainer_find_stack` · `portainer_get_stack` · `portainer_get_stack_file` · `portainer_get_stack_images_status`

### Stacks — write
`portainer_create_stack_from_content` · `portainer_create_stack_from_git` · `portainer_update_stack` · `portainer_redeploy_stack_from_git` · `portainer_update_stack_git_config` · `portainer_set_stack_env` · `portainer_start_stack` · `portainer_stop_stack` · `portainer_delete_stack` · `portainer_migrate_stack` · `portainer_trigger_stack_webhook`

### Registries
`portainer_list_registries` · `portainer_get_registry`

### Containers
`docker_list_containers` · `docker_inspect_container` · `docker_get_container_logs` · `docker_get_container_stats` · `docker_top_container` · `docker_start_container` · `docker_stop_container` · `docker_restart_container` · `docker_kill_container` · `docker_rename_container` · `docker_remove_container` · `docker_create_container`

### Images
`docker_list_images` · `docker_inspect_image` · `docker_pull_image` · `docker_remove_image` · `docker_prune_images`

### Volumes
`docker_list_volumes` · `docker_inspect_volume` · `docker_create_volume` · `docker_remove_volume` · `docker_prune_volumes`

### Networks
`docker_list_networks` · `docker_inspect_network` · `docker_create_network` · `docker_remove_network` · `docker_connect_container` · `docker_disconnect_container` · `docker_prune_networks`

### Swarm
`docker_get_swarm_info` · `docker_list_services` · `docker_inspect_service` · `docker_get_service_logs` · `docker_update_service` · `docker_list_tasks` · `docker_list_nodes` · `docker_update_node`

### Docker system
`docker_info` · `docker_version` · `docker_disk_usage` · `docker_events`

## Notes on behaviour

**File-based vs git-backed stacks.** Portainer only accepts direct compose updates for file-based stacks. Aim `portainer_update_stack` at a git stack and this server refuses with a message naming `portainer_redeploy_stack_from_git`, rather than letting Portainer return an opaque failure.

**Environment ids.** Most stack mutations need one. It is inferred from the stack being acted on, so you rarely have to pass it; `Portainer:DefaultEnvironmentId` covers the rest.

**Swarm stacks.** The cluster id a Swarm deploy requires is fetched automatically from the target environment. Deploying `platform=swarm` to a non-Swarm host fails with a message saying so.

**Edge environments.** Portainer's `Status` field is meaningless for edge agents — they report `0` forever, and real health is the separate `Heartbeat` boolean. `portainer_list_environments` returns a single synthesised `health` field alongside both raw values.

**Logs and stats are always bounded.** Log tails are capped at `MaxLogBytes`, `follow` is never set, stats are always a single sample, and `docker_events` requires an explicit time window. Docker's 8-byte multiplexed frame headers are stripped from non-TTY log output.

**No `docker_exec`.** Running arbitrary commands inside containers needs a hijacked bidirectional stream and has a far larger blast radius than everything else here. `RemoteAdminMCPSharp` covers arbitrary command execution with its own double-gated design.

**Kubernetes and Edge stacks are out of scope.** This server handles Docker standalone and Swarm stacks. Kubernetes stack creation and Edge stack fleet deployment use different payload shapes and are not implemented.
