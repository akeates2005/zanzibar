---
name: run-app
description: Start the Zanzibar PoC locally (SpiceDB, stub issuer, API, Vite client), drive it in Chrome as a seeded user with screenshots, and stop it cleanly by PID. Use when asked to run, start, demo or screenshot the app, or to see a change working in the real app.
---

# Run the app

Verified 2026-11-18 on this machine (WSL2, .NET 9, Node 22, SpiceDB v1.57.1, Google Chrome). On .NET 10 (2026-11-20),
steps 2 and 4 ran as written and `/health` reported ready; the step 3 drive script has not been rerun. This follows the README's
"Four terminals" section, run in the background. Set `R` to a folder in your scratchpad, and use the same value in every
step.

## 1. Preconditions

- Ports 5080, 5090, 5173 and 50051 must be free: `ss -ltn | grep -E ':(5080|5090|5173|50051)\b'` prints nothing.
- `verify.sh` and the e2e tests need the API and issuer ports (5080 and 5090). Never run the app at the same time as
  either; stop it first.

## 2. Start (from the repository root)

```bash
R=<scratchpad>/run; mkdir -p "$R"
dotnet build Poc.sln --nologo -v quiet 2>&1 | grep -E " error |Build succeeded"
scripts/spicedb-local.sh start
nohup dotnet run --no-build --project src/Poc.DevIssuer > "$R/issuer.log" 2>&1 & echo $! > "$R/issuer.pid"
Poc__SpiceDb__PresharedKey=zanzibar-dev-only-key nohup dotnet run --no-build --project src/Poc.Api > "$R/api.log" 2>&1 & echo $! > "$R/api.pid"
(cd web && nohup npm run dev > "$R/web.log" 2>&1 &)
```

Wait for readiness (about 15 seconds; the API reseeds both stores). Do not use `sleep`:

```bash
until curl -sf http://localhost:5080/health | grep -q '"ready":true'; do kill -0 "$(cat "$R/api.pid")" || { tail -30 "$R/api.log"; exit 1; }; done
curl -s http://localhost:5080/health; curl -s -o /dev/null -w "vite %{http_code}\n" http://localhost:5173/
```

## 3. Drive it

Write this to `$R/drive.cjs` and run `node "$R/drive.cjs" "$R"`. It uses the repository's own Playwright from
`web/node_modules` with installed Chrome. The client remembers only the chosen user id, in `localStorage` under
`poc.user`. Seed users: priya (platform administrator), alice (Acme administrator), dave, erin, grace and others (see
`web/e2e/support.ts`). Add a call to `as` for each page you need.

```js
const { chromium } = require('/home/akeat/projects/zanzibar/web/node_modules/playwright');
const out = process.argv[2];

async function as(browser, user, path, shot) {
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  await page.addInitScript((id) => window.localStorage.setItem('poc.user', id), user);
  await page.goto(`http://localhost:5173${path}`);
  await page.getByTestId('signed-in-name').waitFor();
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: `${out}/${shot}.png`, fullPage: true });
  console.log(`${shot}: ${user} at ${page.url()}`);
  await context.close();
}

(async () => {
  const browser = await chromium.launch({ channel: 'chrome' });
  try {
    await as(browser, 'priya', '/resources', '1-priya-resources');
    await as(browser, 'alice', '/resources/commerce/domain/acme-uk/access', '2-alice-domain-access');
  } finally {
    await browser.close();
  }
})().catch((e) => { console.error(e); process.exit(1); });
```

**Read every screenshot.** A blank page or an error banner means it failed, even when the script exits 0.

Useful paths:
- `/resources`: the tree;
- `/resources/<type>/<id>`: an object, with its `/access` tab;
- `/access?type=&id=`: the Access tab for any type;
- `/explorer`;
- `/system` (priya only).

## 4. Stop (by PID; never `pkill -f`)

`dotnet run` and `npm run dev` each start a child process: `Poc.Api`, `Poc.DevIssuer`, `sh -c vite` and `node .../vite`.
List them, kill every PID shown, then confirm the ports are free:

```bash
ps -eo pid,ppid,comm,args | grep -E "Poc\.Api|Poc\.DevIssuer|dotnet run|npm run dev|vite" | grep -v grep
kill <each pid listed>
scripts/spicedb-local.sh stop
ss -ltn | grep -E ':(5080|5090|5173|50051)\b' || echo "ports free"
```

All data is in memory and is lost on stop by design. The next start reseeds it.
