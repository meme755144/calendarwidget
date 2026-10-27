import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const dir = process.argv[2];
http.createServer((req, res) => {
  const m = req.url.match(/^\/(\d{4})\.json$/);
  const file = m ? path.join(dir, m[1] + '.json') : null;
  if (!file || !fs.existsSync(file)) { res.writeHead(404); return res.end('not found'); }
  res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(fs.readFileSync(file));
}).listen(18767, '127.0.0.1');
