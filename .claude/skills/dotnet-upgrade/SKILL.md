---
name: dotnet-upgrade
description: Assess or carry out a move of the Zanzibar PoC to a new .NET major version (SDK, target framework, Microsoft packages, OpenAPI, Dockerfiles, docs). Use when asked about the impact of a new .NET version, to install or pin an SDK, or to do the upgrade.
---

# .NET major-version upgrade

Written from the .NET 9 to 10 upgrade (commit `0ed7165`, 2026-11-20) and the .NET 11 assessment the same day. Follow
`CLAUDE.md` for the gate and the commit; this skill covers only what is specific to an upgrade.

## 1. Assess before touching anything

- **Support first.** Check the dates on the
  [releases and support page](https://learn.microsoft.com/dotnet/core/releases-and-support). Even releases are LTS
  and odd releases are STS (24 months). If the new release ends support no later than the current one, say so up
  front: the upgrade buys features, not support time.
- Read the breaking-changes pages through the Microsoft Learn MCP: `/dotnet/core/compatibility/<N>`, ASP.NET Core's
  `/aspnet/core/breaking-changes/<N>/overview` and EF Core's `/ef/core/what-is-new/ef-core-<N>.0/breaking-changes`.
  For each entry, grep the code before calling it harmless, and cite the `file:line` that shows it.
- These parts of the stack have broken or nearly broken before:
  - analyzers under `TreatWarningsAsErrors` and `AnalysisLevel latest-recommended`;
  - the OpenAPI object model (Microsoft.OpenApi major versions) and the committed `web/openapi.json`;
  - JSON number handling;
  - `BackgroundService` behaviour;
  - the generated `Program` class;
  - OpenTelemetry instrumentation.
- Package versions and dependency ranges come from NuGet (`v3-flatcontainer`; see `CLAUDE.md`, "Claims"). Never
  recall them. Check that `Authzed.Net` still targets a framework the new TFM can consume (1.6.0 targets `net8.0`).
- Report what was read but not built. A docs-only assessment proves nothing about the analyzers or the OpenAPI output.

## 2. Install and pin the SDK

- `sudo` needs a password here, so ask Andrew to run `! sudo apt-get install -y dotnet-sdk-<N>.0`.
- With no `global.json`, the new SDK builds the old target at once, with its own analyzers (SDK 10 broke net9 with
  12 CA1873 errors). To keep the current target until the upgrade, pin the current band:
  `{"sdk": {"version": "<old>.0.<band>", "rollForward": "latestFeature"}}`. Delete the pin in the upgrade commit.

## 3. Upgrade checklist

1. `Directory.Build.props`: `<TargetFramework>`.
2. Microsoft package references (EF Core Sqlite, JwtBearer, AspNetCore.OpenApi, Mvc.Testing, any
   `Microsoft.Extensions.*`) moved to the new GA version. Drop references that the shared framework now provides (NU1510).
3. Analyzer errors fixed at the cause (`CLAUDE.md` lists the recurring ones).
4. OpenAPI: rebuild the transformer in `src/Poc.Api/Program.cs` against the new Microsoft.OpenApi. Keep
   `OpenApiVersion = OpenApi3_0` unless Andrew decides otherwise. Regenerate `web/openapi.json` and `schema.ts`
   (see `CLAUDE.md`, "Commands"), then review the document diff: the meaning must not change.
5. Dockerfiles: the `sdk:<N>.0` and `aspnet:<N>.0` tags. These can't be tested here (D2, D3, D7), so say so.
6. Stale references: `git grep -n -E "net<old>|\.NET <old>|<old>\.0\." -- '*.md' .claude scripts`. Fix them **before**
   the final `verify.sh`. In the .NET 10 upgrade they were missed and needed a second commit.
7. `DECISIONS.md`: a section for the upgrade, listing each behaviour change accepted and what was not verified.
8. The full `CLAUDE.md` closing checklist (repeat runs, a run under load, a clean clone). Run the clean clone in the
   background, and don't start the app until it finishes (see the run-app skill).

## 4. .NET 11 findings (assessed 2026-11-20 from the docs; not built)

- Support: 11 (STS) and 10 (LTS) both end on 2028-11-14, so there's no support gain. The recommendation was to stay
  on 10. The next LTS is .NET 12.
- Changes needed:
  - `Poc.Infrastructure.csproj`: drop the DependencyInjection/Hosting/Logging `.Abstractions` references (NU1510,
    which becomes an error under `TreatWarningsAsErrors`; not tested).
  - `Program.cs:170`: `Dictionary<string, OpenApiMediaType>` becomes `IOpenApiMediaType` (Microsoft.OpenApi 3.x,
    `[3.11.0, 4.0.0)`).
  - OpenTelemetry 1.15.x to 1.21.0, the first version that supports net11. It needs core 1.21.0, and since 1.16.0 it
    skips the HTTP tags ASP.NET Core now adds itself.
- Biggest risk: runtime async changes `AsyncLocal` and `Activity.Current` flow, and the trace-id logging and
  shared-static test lookups depend on it. Only repeated suite runs can settle it.
- Checked and not affecting us: the `BackgroundService` failure exit code (all three services catch errors), empty
  strings in chained configuration (no `AddConfiguration` calls), VSTest dropping Newtonsoft.Json, and the x86-64-v2
  baseline (this CPU has AVX2).
