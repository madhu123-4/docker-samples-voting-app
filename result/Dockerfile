# ════════════════════════════════════════════════════════════════════
#  result/Dockerfile
#
#  PROBLEMS IN ORIGINAL:
#  ─────────────────────
#  FROM node:5.11.0-slim
#    → Node 5 EOL June 2016. Has critical CVEs (OpenSSL). No async/await.
#      Incompatible with every modern npm package.
#
#  RUN npm install -g nodemon
#    → nodemon is a dev-only file-watcher. Shipping it to production
#      adds 15 MB and a security surface for no benefit.
#
#  RUN npm config set registry http://registry.npmjs.org
#    → Forces plain HTTP (not HTTPS) for the npm registry.
#      This is a supply-chain security risk (MITM attacks on packages).
#
#  ADD package.json → RUN npm install → ADD . /app  (all one stage)
#    → No layer caching: every source change re-runs npm install.
#    → No multi-stage: devDependencies + npm cache ship to production.
#
#  RUN mv /app/node_modules /node_modules
#    → Moves node_modules to root. Non-standard, confusing, fragile.
#
#  CMD ["node", "server.js"]  (kept — exec form is correct)
# ════════════════════════════════════════════════════════════════════

# ── Stage 1: BUILD ────────────────────────────────────────────────
FROM node:22-slim AS build

WORKDIR /app

# Copy manifests FIRST → Docker caches this layer.
# If only server.js / views/ change, npm ci is NOT re-run on rebuild.
COPY package*.json ./

# `npm ci` = clean install from package-lock.json.
# Faster, stricter, reproducible. Does not modify lock file.
RUN npm ci

# Strip devDependencies before copying to runtime stage
RUN npm prune --omit=dev

# Copy rest of the source
COPY . .


# ── Stage 2: RUNTIME ──────────────────────────────────────────────
# Lean image: no SDK, no npm cache, no devDependencies, no nodemon.
FROM node:22-slim AS runtime

WORKDIR /app

# Copy only what is needed to run
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/server.js    ./server.js
COPY --from=build /app/views        ./views
COPY --from=build /app/package.json ./package.json

# Run as the built-in non-root 'node' user.
# Original ran as root — if the container is ever compromised,
# root inside = much easier path to host damage.
USER node

ENV PORT=80
EXPOSE 80

# Exec form: the node process receives SIGTERM directly.
# Shell form (CMD node server.js) would send SIGTERM to /bin/sh,
# which ignores it, so docker stop would always wait the full 10s timeout.
CMD ["node", "server.js"]