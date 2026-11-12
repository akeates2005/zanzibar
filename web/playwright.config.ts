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
    baseURL: 'http://localhost:5173',
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
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: ISSUER, Issuer__Issuer: ISSUER },
      stdout: 'ignore',
      url: `${ISSUER}/.well-known/openid-configuration`,
      reuseExistingServer: false,
      timeout: 60_000,
    },
    {
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
    {
      command: 'npm run build && npm run preview',
      env: { VITE_API_URL: API, VITE_ISSUER_URL: ISSUER },
      url: 'http://localhost:5173',
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
});
