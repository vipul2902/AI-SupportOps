# syntax=docker/dockerfile:1
# Stage 1: build the static SPA. Stage 2: serve it with unprivileged nginx (uid 101, port 8080).

FROM node:24-alpine AS build
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime
# Templates in /etc/nginx/templates are rendered with envsubst at startup (API_UPSTREAM).
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /web/dist /usr/share/nginx/html
ENV API_UPSTREAM=api:8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --retries=3 CMD wget -qO- http://127.0.0.1:8080/healthz >/dev/null || exit 1
