// Local development server: serves the website (src/) on http://localhost:3000.
// In Azure the same files are served by Static Web Apps.
import express from 'express';
import { fileURLToPath } from 'node:url';

const PORT = Number(process.env.PORT ?? 3000);
const root = fileURLToPath(new URL('./src', import.meta.url));

const app = express();
app.use(express.static(root, { extensions: ['html'] }));

app.listen(PORT, err => {
  if (err) {
    console.error(err.code === 'EADDRINUSE' ? `Port ${PORT} is already in use. Stop the other server or set PORT.` : err);
    process.exit(1);
  }
  console.log(`Serving ${root} on http://localhost:${PORT}`);
});
