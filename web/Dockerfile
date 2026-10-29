FROM node:22-alpine AS build
RUN corepack enable && corepack prepare pnpm@10.18.0 --activate
WORKDIR /web
COPY . .
RUN pnpm install --frozen-lockfile --filter @urbanfix/portal...
RUN pnpm --filter @urbanfix/portal exec vite build

# One public origin: nginx serves the portal and proxies /api (REST and SignalR) to the core service,
# the same shape as the Vite dev proxy, so there is no CORS to configure.
FROM nginx:1.27-alpine
COPY --from=build /web/apps/portal/dist /usr/share/nginx/html
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
# CORE_HOSTPORT is the core service's private address, e.g. urbanfix-core:10000.
ENV PORT=10000
