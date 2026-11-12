#!/usr/bin/env python3
"""Static checks on docker-compose.yml and the Dockerfiles. Docker is not available in the development environment, so
these invariants (the ones a reviewer would look for) are enforced without running a container."""
import pathlib
import re
import sys

try:
    import yaml
except ImportError:
    print("SKIP: PyYAML is not installed")
    sys.exit(3)

root = pathlib.Path(__file__).resolve().parent.parent
compose = yaml.safe_load((root / "docker-compose.yml").read_text())
services = compose["services"]
errors: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


check(set(services) == {"spicedb", "issuer", "api"}, f"unexpected services: {sorted(services)} (the API serves the client, so there is no web service)")

for name, service in services.items():
    for port in service.get("ports", []):
        check(str(port).startswith("127.0.0.1:"), f"{name}: port {port} is not bound to localhost (PRD: localhost only)")

spicedb = services["spicedb"]
check(re.fullmatch(r"authzed/spicedb:v\d+\.\d+\.\d+", spicedb["image"]) is not None, f"spicedb image must be a pinned release tag, got {spicedb['image']}")
check("--datastore-engine=memory" in spicedb["command"], "spicedb must use the memory datastore")
check("--telemetry-endpoint=" in spicedb["command"], "spicedb telemetry must be off")

# The only credential allowed in the file is the documented development default, and only as an overridable default.
text = (root / "docker-compose.yml").read_text()
keys = re.findall(r"PresharedKey|PRESHARED_KEY", text)
check(len(keys) >= 2, "the preshared key should be wired into both spicedb and the api")
for line in text.splitlines():
    if re.search(r"(?i)(key|secret|password|token)\s*:", line) and not line.lstrip().startswith("#"):
        check("${SPICEDB_PRESHARED_KEY:-zanzibar-dev-only-key}" in line, f"credential-looking value is not the overridable development default: {line.strip()}")

api_env = services["api"]["environment"]
check(api_env["Poc__SpiceDb__Endpoint"].startswith("http://spicedb:"), "api must reach spicedb by its service name")
check("Authentication__MetadataAddress" in api_env and "issuer:" in api_env["Authentication__MetadataAddress"], "api must fetch keys from the issuer's service name")
check(api_env["Authentication__Authority"] == services["issuer"]["environment"]["Issuer__Issuer"], "the api's authority must equal the name tokens carry as their issuer")
check(services["issuer"]["environment"]["ASPNETCORE_ENVIRONMENT"] == "Development", "the stub issuer only runs in Development")
check("ASPNETCORE_ENVIRONMENT" not in api_env or api_env["ASPNETCORE_ENVIRONMENT"] != "Development", "the api image should not run in Development")

for dockerfile in ["src/Poc.Api/Dockerfile", "src/Poc.DevIssuer/Dockerfile"]:
    full = (root / dockerfile).read_text()
    body = "\n".join(line for line in full.splitlines() if not line.lstrip().startswith("#"))  # instructions, not comments
    froms = re.findall(r"^FROM\s+(\S+)", body, flags=re.M)
    check(all(not f.endswith(":latest") and ":" in f for f in froms), f"{dockerfile}: every base image needs an explicit tag ({froms})")
    check(re.search(r"^USER\s+(app|\d+)", body, flags=re.M) is not None, f"{dockerfile}: the final image must not run as root")
    check(not re.search(r"(?i)(PRESHARED|SECRET|PASSWORD)", body), f"{dockerfile}: no secret may be baked into an image")
    check("ENTRYPOINT" in body, f"{dockerfile}: missing ENTRYPOINT")

dockerignore = (root / ".dockerignore").read_text()
for must in [".env", "node_modules", "bin/", "obj/", ".git/"]:
    check(must in dockerignore, f".dockerignore must exclude {must}")

if errors:
    print("Compose/Dockerfile checks failed:")
    for e in errors:
        print(" -", e)
    sys.exit(1)
print(f"ok: {len(services)} services, localhost-only ports, pinned spicedb, no baked secrets, non-root images")
