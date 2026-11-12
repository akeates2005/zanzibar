#!/usr/bin/env bash
# Builds and runs the API the way its container image will: `dotnet publish` output with the built client in wwwroot, in the
# Production environment, with every setting from the environment. Used by `npm run e2e:published` to rehearse everything
# about `docker compose up` except the container runtime itself. Runs in the foreground (exec) so a supervisor can stop it.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/web/.published/api"

rm -rf "$out" && mkdir -p "$out"
dotnet publish "$root/src/Poc.Api/Poc.Api.csproj" -c Release -o "$out" --no-self-contained -v quiet >&2

# The client is served by the API, so it calls the API on its own origin; the browser reaches the issuer at its public address.
(cd "$root/web" && VITE_API_URL="" VITE_ISSUER_URL="${PUBLISHED_ISSUER_PUBLIC_URL:-http://localhost:5090}" npm run build --silent >&2)
cp -r "$root/web/dist" "$out/wwwroot"

cd "$out"
exec dotnet Poc.Api.dll
