# ResolveAI as a single container: the app serves /api and the Blazor UI, and starts the MCP servers as child processes.

# --- build: restore and publish the app, which brings the MCP server executable with it ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
# Restore must see the source: it fetches Blazor's client script only when the project contains .razor files.
COPY src src
RUN dotnet publish src/ResolveAI.App -c Release -o /app

# --- test: optional, runs the tests on Linux: docker build --target test . ---
FROM build AS test
COPY tests/ResolveAI.Tests tests/ResolveAI.Tests
COPY demo-systems demo-systems
COPY mcp.config.example.json ./
RUN dotnet test tests/ResolveAI.Tests

# --- runtime: only the published app and the demo systems its MCP servers read ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV HOST=0.0.0.0 \
    API_PORT=8787 \
    DB_PATH=/app/data/resolveai.db
WORKDIR /app
COPY --from=build /app .
COPY demo-systems demo-systems
COPY mcp.config.example.json ./
# The SQLite file lives on a volume; the directory must be writable by the unprivileged user.
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
USER $APP_UID
EXPOSE 8787
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["/app/ResolveAI.App", "--healthcheck"]
ENTRYPOINT ["/app/ResolveAI.App"]
