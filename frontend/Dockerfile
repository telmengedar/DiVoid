# ── Stage 1: Build ──────────────────────────────
FROM node:20-alpine AS build

WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# All VITE_* vars are baked at build time.
# Pass them as --build-arg to docker build.
ARG VITE_KEYCLOAK_AUTHORITY=https://auth.mamgo.io/realms/master
ARG VITE_KEYCLOAK_CLIENT_ID=DiVoid
ARG VITE_API_BASE_URL=https://divoid.mamgo.io/api
ARG VITE_OIDC_REDIRECT_URI=https://divoid.mamgo.io/callback
ARG VITE_OIDC_POST_LOGOUT_REDIRECT_URI=https://divoid.mamgo.io/logout

ENV VITE_KEYCLOAK_AUTHORITY=$VITE_KEYCLOAK_AUTHORITY
ENV VITE_KEYCLOAK_CLIENT_ID=$VITE_KEYCLOAK_CLIENT_ID
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
ENV VITE_OIDC_REDIRECT_URI=$VITE_OIDC_REDIRECT_URI
ENV VITE_OIDC_POST_LOGOUT_REDIRECT_URI=$VITE_OIDC_POST_LOGOUT_REDIRECT_URI

RUN npm run build

# ── Stage 2: Serve ──────────────────────────────
FROM nginx:1.27-alpine

# SPA routing: all paths -> index.html
COPY nginx.conf /etc/nginx/conf.d/default.conf

COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
