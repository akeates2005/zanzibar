import { defineConfig } from '@playwright/test';

/**
 * End-to-end journeys against the whole system running natively: SpiceDB, the stub issuer, the API and the built client.
 * Docker is not available in the development environment (DECISIONS.md D2/D3), so this stands in for "Playwright against
 * Compose"; the journeys themselves are identical. Playwright starts and stops every process, and never reuses one that is
 * already running, so a journey can never be run against somebody's half-used data.
 */
const SPICEDB_KEY = 'e2e-only-spicedb-key'; // test-only; never a real credential
const API = 'http://127.0.0.1:5080';
const ISSUER = 'http://127.0.0.1:5090';

/**
 * `dev` (default): the API from its project, the client from `vite preview` on 5173.
 * `published`: a rehearsal of the container images. The API is `dotnet publish` output in the Production environment with
 * the built client in wwwroot, serving everything on 5080 with every setting from the environment, and the issuer is reached
 * by a different name (127.0.0.1) than the one its tokens carry (localhost), exactly as the API container will reach it as
 * http://issuer:5090 while tokens name http://localhost:5090.
 */
const published = process.env.E2E_MODE === 'published';

export default defineConfig({
  testDir: './e2e',
  timeout: 45_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1, // every journey resets the one shared system, so they run one at a time
  retries: 0,
  forbidOnly: true,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: published ? 'http://localhost:5080' : 'http://localhost:5173',
    channel: 'chrome', // the installed Google Chrome; no browser download needed
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: [
    {
      command: `spicedb serve --datastore-engine=memory --grpc-addr=127.0.0.1:50051 --metrics-addr=127.0.0.1:9092 --telemetry-endpoint= --skip-release-check=true --log-level=warn`,
      env: { SPICEDB_GRPC_PRESHARED_KEY: SPICEDB_KEY },
      stdout: 'ignore',
      stderr: 'ignore',
      port: 50051,
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command: 'dotnet run --no-build -c Release --no-launch-profile --project ../src/Poc.DevIssuer',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: ISSUER,
        // Tokens carry the name a browser uses. In published mode that differs from the address the API fetches keys from.
        Issuer__Issuer: published ? 'http://localhost:5090' : ISSUER,
        Issuer__WebOrigin: published ? 'http://localhost:5080' : 'http://localhost:5173',
      },
      stdout: 'ignore',
      url: `${ISSUER}/.well-known/openid-configuration`,
      reuseExistingServer: false,
      timeout: 60_000,
    },
    published
      ? {
          command: 'bash ../scripts/run-published-api.sh',
          env: {
            ASPNETCORE_ENVIRONMENT: 'Production', // as in the image: nothing comes from appsettings.Development.json
            ASPNETCORE_URLS: API,
            Poc__SpiceDb__Endpoint: 'http://127.0.0.1:50051',
            Poc__SpiceDb__AllowInsecureTransport: 'true',
            Poc__SpiceDb__PresharedKey: SPICEDB_KEY,
            Authentication__Authority: 'http://localhost:5090',
            Authentication__MetadataAddress: `${ISSUER}/.well-known/openid-configuration`,
            Authentication__RequireHttpsMetadata: 'false',
            Authentication__Audience: 'poc-api',
            Cors__WebOrigin: 'http://localhost:5080',
            Logging__LogLevel__Default: 'Warning',
          },
          stdout: 'ignore',
          stderr: 'pipe',
          url: `${API}/health`,
          reuseExistingServer: false,
          timeout: 240_000, // publish + client build + start-up
        }
      : {
          command: 'dotnet run --no-build -c Release --no-launch-profile --project ../src/Poc.Api',
          env: {
            ASPNETCORE_ENVIRONMENT: 'Development',
            ASPNETCORE_URLS: API,
            Authentication__Authority: ISSUER,
            Poc__SpiceDb__Endpoint: 'http://127.0.0.1:50051',
            Poc__SpiceDb__PresharedKey: SPICEDB_KEY,
            Logging__LogLevel__Default: 'Warning',
          },
          stdout: 'ignore',
          stderr: 'pipe', // keep errors visible: a start-up failure should explain itself
          // /health answers 503 until start-up has seeded both stores, so this also waits for the data to exist.
          url: `${API}/health`,
          reuseExistingServer: false,
          timeout: 90_000,
        },
    ...(published
      ? []
      : [
          {
            command: 'npm run build && npm run preview',
            env: { VITE_API_URL: API, VITE_ISSUER_URL: ISSUER },
            url: 'http://localhost:5173',
            reuseExistingServer: false,
            timeout: 120_000,
          },
        ]),
  ],
});
