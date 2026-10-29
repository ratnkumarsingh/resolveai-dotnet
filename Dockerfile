# ResolveAI as a single container: the API serves both /api and the built web app.

# --- build: install everything and build the web bundle ---
FROM node:24-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
COPY shared/package.json shared/
COPY mcp/package.json mcp/
COPY api/package.json api/
COPY web/package.json web/
RUN npm ci
COPY shared shared
COPY web web
RUN npm run build

# --- runtime: production dependencies for the API and the MCP servers ---
FROM node:24-slim
ENV NODE_ENV=production \
    HOST=0.0.0.0 \
    API_PORT=8787 \
    DB_PATH=/app/data/resolveai.db \
    WEB_DIST=/app/web/dist
WORKDIR /app
COPY package.json package-lock.json ./
COPY shared/package.json shared/
COPY mcp/package.json mcp/
COPY api/package.json api/
COPY web/package.json web/
# msnodesqlv8 (Windows sign-in) is optional and is skipped on Linux; the container uses tedious + a SQL login.
RUN npm ci --omit=dev -w api -w shared -w mcp && npm cache clean --force
COPY shared/src shared/src
COPY mcp/src mcp/src
COPY api/src api/src
# The demo systems the MCP servers read (log files, deploy feed, metric series) and their config.
COPY demo-systems demo-systems
COPY mcp.config.example.json ./
COPY --from=build /app/web/dist web/dist
# The SQLite file lives on a volume; the directory must be writable by the unprivileged user.
RUN mkdir -p /app/data && chown node:node /app/data
USER node
EXPOSE 8787
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:8787/api/config').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
WORKDIR /app/api
CMD ["/app/node_modules/.bin/tsx", "src/server.ts"]
