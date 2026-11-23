# syntax=docker/dockerfile:1

# Stage 1: build the static SPA (Node 20.19+ is required by Vite 7).
FROM node:20.19.5-alpine3.22 AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# Production build: base "/", the Gateway serves the SPA under /spa/**
# (prefix stripped) and the hashed assets under /assets/** (ADR 0011).
RUN npm run build

# Stage 2: nginx serves the build with an SPA fallback and sane cache rules.
FROM nginx:1.28.3-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
