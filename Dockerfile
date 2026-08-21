# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY PortainerMCPSharp.csproj ./
ARG TARGETARCH
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet restore PortainerMCPSharp.csproj \
    -r "$rid" \
    -p:PublishSingleFile=true \
    -p:SelfContained=false \
    -p:EnableCompressionInSingleFile=false

COPY . .
RUN arch="${TARGETARCH:-amd64}"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet publish PortainerMCPSharp.csproj \
    -c Release \
    --no-restore \
    -r "$rid" \
    --self-contained false \
    -o /app/publish \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:IsTransformWebConfigDisabled=true \
    -p:StaticWebAssetsEnabled=false \
    -p:DebugType=none \
    -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# The safe posture is baked in: read-only, and every destructive category off. An operator
# who wants this container to actually change anything has to say so explicitly.
ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PORTAINERMCP_Server__Host=0.0.0.0 \
    PORTAINERMCP_Server__Port=5716 \
    PORTAINERMCP_Server__Path=/mcp \
    PORTAINERMCP_Server__Password= \
    PORTAINERMCP_Portainer__ReadOnly=true \
    PORTAINERMCP_Portainer__AllowStackDelete=false \
    PORTAINERMCP_Portainer__AllowContainerCreate=false \
    PORTAINERMCP_Portainer__AllowContainerRemoval=false \
    PORTAINERMCP_Portainer__AllowImageRemoval=false \
    PORTAINERMCP_Portainer__AllowVolumeManagement=false \
    PORTAINERMCP_Portainer__AllowNetworkManagement=false \
    PORTAINERMCP_Portainer__AllowPrune=false \
    PORTAINERMCP_Portainer__AllowSwarmMutation=false \
    PORTAINERMCP_Portainer__ProtectPortainerItself=true \
    PORTAINERMCP_Portainer__RedactSecrets=true

RUN mkdir -p /app/logs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5716
VOLUME ["/app/logs"]

ENTRYPOINT ["./PortainerMCPSharp"]
