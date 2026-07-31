# Build context is the repository root, not this directory:
#   docker build -f deploy/Dockerfile -t calltree .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone so the layer survives source edits.
# The repo-local NuGet.config must come along: it maps all packages to nuget.org.
COPY NuGet.config ./
COPY CallTree.Core/CallTree.Core.slnx CallTree.Core/
COPY CallTree.Core/CallTree.Domain/CallTree.Domain.csproj CallTree.Core/CallTree.Domain/
COPY CallTree.Core/CallTree.Application/CallTree.Application.csproj CallTree.Core/CallTree.Application/
COPY CallTree.Core/CallTree.Infrastructure/CallTree.Infrastructure.csproj CallTree.Core/CallTree.Infrastructure/
COPY CallTree.Core/CallTree.Telephony/CallTree.Telephony.csproj CallTree.Core/CallTree.Telephony/
COPY CallTree.Core/CallTree.Api/CallTree.Api.csproj CallTree.Core/CallTree.Api/
RUN dotnet restore CallTree.Core/CallTree.Api/CallTree.Api.csproj

COPY CallTree.Core/ CallTree.Core/
RUN dotnet publish CallTree.Core/CallTree.Api/CallTree.Api.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SIP listens on 5060, which is privileged, so the process stays root. The intended deployment is an
# LXC dedicated to this service on host networking; if you would rather drop privileges, move
# Telephony:SipListenPort above 1024 and translate the port at the router instead.
USER root

COPY --from=build /app .

# Runtime data lives on volumes so it survives image upgrades. SQLite will not create these itself —
# Program.EnsureDatabase does, but the mount has to be writable.
VOLUME ["/data"]

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ConnectionStrings__CallTree="Data Source=/data/calltree.db" \
    Storage__RecordingsRoot=/data/recordings

# Documentation only — host networking ignores published ports.
EXPOSE 8080/tcp 5060/udp 5060/tcp

ENTRYPOINT ["dotnet", "CallTree.Api.dll"]
