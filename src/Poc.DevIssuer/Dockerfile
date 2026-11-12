# Build context is the repository root:  docker build -f src/Poc.DevIssuer/Dockerfile .
# The stub token issuer. It refuses to start unless ASPNETCORE_ENVIRONMENT=Development, and docker-compose.yml publishes its
# port on localhost only. NOTE: not yet run (DECISIONS.md D2/D3).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Poc.DevIssuer/Poc.DevIssuer.csproj -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5090
EXPOSE 5090
USER app
ENTRYPOINT ["dotnet", "Poc.DevIssuer.dll"]
