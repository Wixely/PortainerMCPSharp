# Third-Party Notices

PortainerMCPSharp is licensed under the MIT License (see `LICENSE`). It depends
on the third-party components listed below. Each remains under its own license;
this file is provided for attribution.

## NuGet packages

| Package | License |
| --- | --- |
| Microsoft.AspNetCore.App | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | MIT |
| ModelContextProtocol.AspNetCore | Apache-2.0 |
| Serilog.AspNetCore | Apache-2.0 |
| Serilog.Enrichers.Environment | Apache-2.0 |
| Serilog.Enrichers.Process | Apache-2.0 |
| Serilog.Enrichers.Thread | Apache-2.0 |
| Serilog.Settings.Configuration | Apache-2.0 |
| Serilog.Sinks.Console | Apache-2.0 |
| Serilog.Sinks.File | Apache-2.0 |

The full text of the MIT and Apache-2.0 licenses is available at
<https://opensource.org/license/mit> and
<https://www.apache.org/licenses/LICENSE-2.0> respectively.

## Portainer and Docker API clients

No Portainer or Docker client library is taken as a dependency.

Portainer's REST API (`/api/auth`, `/api/stacks/*`, `/api/endpoints/*`,
`/api/registries`, `/api/system/*`) and its Docker proxy
(`/api/endpoints/{id}/docker/*`, which forwards the Docker Engine API verbatim)
are both spoken directly with `System.Net.Http.HttpClient` and
`System.Text.Json` from the base class library, in `Services/PortainerClient.cs`.

`Docker.DotNet` was considered and rejected: it expects to own the transport,
which conflicts with routing every call through Portainer's proxy path and its
`X-API-KEY` header. Speaking the wire protocol directly also keeps the entire
dependency set MIT/Apache-2.0 and avoids taking a dependency on Portainer's own
zlib-licensed and EE-licensed code.

The API surface is implemented against Portainer's published OpenAPI
specification and the Docker Engine API reference. It is original work covered by
this project's MIT license, not derived from either project's source.
