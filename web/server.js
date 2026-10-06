// Local development server: serves the website (src/) on http://localhost:3000.
// In Azure the same files are served by Static Web Apps.
//   node server.js            serve
//   import { start } from …   used by build.mjs --serve
import express from 'express';
import { fileURLToPath } from 'node:url';

const PORT = Number(process.env.PORT ?? 3000);
const root = fileURLToPath(new URL('./src', import.meta.url));

/** Starts serving; rejects (without side effects) if the port is taken. */
export function start() {
  const app = express();
  app.use(express.static(root, { extensions: ['html'] }));
  return new Promise((resolve, reject) => {
    app.listen(PORT, err => {
      if (err) {
        reject(err.code === 'EADDRINUSE' ? new Error(`Port ${PORT} is already in use (is another "npm run dev" running?). Stop it or set PORT.`) : err);
        return;
      }
      console.log(`Serving ${root} on http://localhost:${PORT}`);
      resolve();
    });
  });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  start().catch(err => { console.error(err.message ?? err); process.exit(1); });
}
