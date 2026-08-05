# Build context is the repository root, not this directory:
#   docker build -f deploy/Dockerfile -t calltree .
#
# One image serves both halves. The SvelteKit UI is built to static files here and copied into the
# API's wwwroot, so the browser talks to a single origin on a single port and there is no CORS to
# configure and no second container to deploy. See deploy/README.md.

FROM node:22-alpine AS ui
WORKDIR /ui

# pnpm pinned rather than taken from corepack's default: the lockfile is v9 and a different major
# would rewrite it or refuse --frozen-lockfile.
RUN npm install --global pnpm@10

# Manifests first so the dependency layer survives edits to the UI source.
COPY CallTree.UI/package.json CallTree.UI/pnpm-lock.yaml CallTree.UI/pnpm-workspace.yaml CallTree.UI/.npmrc ./
RUN pnpm install --frozen-lockfile

COPY CallTree.UI/ ./
RUN pnpm build

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

# The aspnet image is Debian slim, which ships no HTTP client at all. The Compose healthcheck needs one,
# and a healthcheck that cannot run does not fail loudly - the container simply sits permanently
# "unhealthy", which reads as a broken deployment. Install it rather than depend on what the base carries.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# The UI. ASP.NET serves wwwroot as static files and falls back to index.html for unmatched paths,
# which is what makes deep links like /calls work on a first load rather than only from inside the app.
COPY --from=ui /ui/build ./wwwroot

# Runtime data lives on volumes so it survives image upgrades. SQLite will not create these itself —
# Program.EnsureDatabase does, but the mount has to be writable.
VOLUME ["/data"]

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ConnectionStrings__CallTree="Data Source=/data/calltree.db" \
    Storage__RecordingsRoot=/data/recordings

# Documentation only — host networking ignores published ports.
EXPOSE 8080/tcp 5060/udp 5060/tcp

ENTRYPOINT ["dotnet", "CallTree.Api.dll"]
