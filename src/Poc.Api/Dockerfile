# Build context is the repository root:  docker build -f src/Poc.Api/Dockerfile .
# The image contains the API and the built client: the API serves the client's static files itself (PRD: Run-time defaults).
# NOTE: not yet run. Docker was unavailable when this was written (DECISIONS.md D2/D3); the same publish output and
# configuration are rehearsed natively by `npm run e2e:published`.

FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
# Served by the API, so the client calls the API on its own origin (an empty base URL means a relative one). The issuer is
# reached by the browser at its published address.
ARG VITE_ISSUER_URL=http://localhost:5090
ENV VITE_API_URL="" VITE_ISSUER_URL=${VITE_ISSUER_URL}
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/ src/
COPY authz/ authz/
RUN dotnet publish src/Poc.Api/Poc.Api.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
EXPOSE 5080
# The aspnet image ships a non-root `app` user. Every secret and address comes from the environment (see docker-compose.yml).
USER app
ENTRYPOINT ["dotnet", "Poc.Api.dll"]
