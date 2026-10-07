// Builds the website. The TypeScript in ts/ compiles 1:1 into src/js/ (native ES modules, no bundler);
// per-environment settings are not compiled in but read at startup from /app-config.json.
//   npm run build     build once
//   npm run watch     rebuild whenever a .ts file is saved
//   npm run dev       watch + serve on http://localhost:3000
//   npm start         build once + serve
//   node build.mjs --out <dir>   production package in <dir> (used by infra/deploy.ps1); leaves src/ alone
//
// app-config.json is made from:
//   app.config.json         committed defaults (local development)
//   app.config.local.json   optional, git-ignored: your local overrides (not used with --out)
//   APP_CONFIG              optional environment variable (JSON): overrides both (e.g. production values)
//   apikey.txt / GOOGLE_MAPS_API_KEY   the Google Maps key (git-ignored file or environment variable)
import { spawn } from 'node:child_process';
import { cpSync, copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const SITE = 'src';
const OUT_DIR = 'src/js';
const VENDOR = [['node_modules/@azure/msal-browser/lib/msal-browser.min.js', 'vendor/msal-browser.min.js']];

const readJson = file => (existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : {});

/** Writes <site>/app-config.json. */
function writeAppConfig(siteDir, { local }) {
  const config = {
    ...readJson('app.config.json'),
    ...(local ? readJson('app.config.local.json') : {}),
    ...(process.env.APP_CONFIG ? JSON.parse(process.env.APP_CONFIG) : {}),
  };
  const key = (process.env.GOOGLE_MAPS_API_KEY ?? (existsSync('apikey.txt') ? readFileSync('apikey.txt', 'utf8') : '')).trim();
  if (key && !/^[\w-]+$/.test(key)) throw new Error('apikey.txt should contain only the API key (letters, digits, - and _).');
  if (!key) console.warn('No Google Maps key (apikey.txt or GOOGLE_MAPS_API_KEY): the map will show a "no key" message.');
  if (!config.apiBaseUrl || !config.auth?.mode) throw new Error('The config needs "apiBaseUrl" and "auth.mode".');
  writeFileSync(join(siteDir, 'app-config.json'), JSON.stringify({ ...config, googleMapsApiKey: key }, null, 2) + '\n');
  console.log(`Config: API ${config.apiBaseUrl}, sign-in "${config.auth.mode}".`);
}

function copyVendor(siteDir) {
  mkdirSync(join(siteDir, 'vendor'), { recursive: true });
  for (const [from, to] of VENDOR) copyFileSync(from, join(siteDir, to));
}

/** Runs tsc; resolves with its exit code. In watch mode it keeps running. */
function tsc(args) {
  return new Promise(resolve => {
    const child = spawn('npx', ['tsc', '-p', '.', ...args], { stdio: 'inherit', shell: process.platform === 'win32' });
    child.on('exit', code => resolve(code ?? 1));
  });
}

const argv = process.argv.slice(2);
const watch = argv.includes('--watch');
const serve = argv.includes('--serve');
const outIndex = argv.indexOf('--out');

if (outIndex >= 0) {
  // Production package: a copy of the site with its own config and compiled scripts.
  const out = argv[outIndex + 1];
  if (!out) throw new Error('--out needs a directory.');
  rmSync(out, { recursive: true, force: true });
  cpSync(SITE, out, { recursive: true, filter: src => !/[\\/]js([\\/]|$)|app-config\.json$/.test(src.slice(SITE.length)) });
  writeAppConfig(out, { local: false });
  copyVendor(out);
  const code = await tsc(['--outDir', join(out, 'js'), '--sourceMap', 'false']);
  if (code !== 0) { console.error('Build failed.'); process.exit(code); }
  console.log(`Packaged the site in ${out}.`);
} else {
  // Claim the port before touching any files, so a second `npm run dev` stops cleanly
  // instead of wiping the output another one is serving.
  if (serve) {
    try { await (await import('./server.js')).start(); }
    catch (err) { console.error(err.message ?? err); process.exit(1); }
  }

  writeAppConfig(SITE, { local: true });
  copyVendor(SITE);
  rmSync(OUT_DIR, { recursive: true, force: true }); // no stale modules from renamed/deleted .ts files

  if (watch) {
    await tsc(['--watch', '--preserveWatchOutput']);
  } else {
    const code = await tsc([]);
    if (code !== 0) { console.error('Build failed.'); process.exitCode = code; }
    else console.log(`Built ${OUT_DIR}/.`);
  }
}
