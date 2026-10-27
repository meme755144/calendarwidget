import http from 'node:http';
import fs from 'node:fs';

const logFile = process.argv[2];
const mode = { fail: false };
let count = 0;

function d(offset) {
  const t = new Date();
  t.setDate(t.getDate() + offset);
  const p = (n) => String(n).padStart(2, '0');
  return `${t.getFullYear()}${p(t.getMonth() + 1)}${p(t.getDate())}`;
}

function ics() {
  return [
    'BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//test//EN',
    'BEGIN:VEVENT', 'UID:a1', `DTSTART;TZID=Asia/Taipei:${d(0)}T140000`, `DTEND;TZID=Asia/Taipei:${d(0)}T150000`, 'SUMMARY:軟體園區主任來訪\\, 大會議室', 'END:VEVENT',
    'BEGIN:VEVENT', 'UID:a2', `DTSTART;TZID=Asia/Taipei:${d(0)}T093000`, 'SUMMARY:晨會', 'END:VEVENT',
    'BEGIN:VEVENT', 'UID:a3', `DTSTART;VALUE=DATE:${d(1)}`, `DTEND;VALUE=DATE:${d(2)}`, 'SUMMARY:測試節日', 'END:VEVENT',
    'BEGIN:VEVENT', 'UID:a4', `DTSTART;VALUE=DATE:${d(3)}`, 'RRULE:FREQ=WEEKLY;COUNT=4', 'SUMMARY:每週例會', 'END:VEVENT',
    'END:VCALENDAR', ''
  ].join('\r\n');
}

http.createServer((req, res) => {
  count++;
  fs.appendFileSync(logFile, `${new Date().toISOString()} ${req.method} ${req.url} ua=${req.headers['user-agent']}\n`);
  if (req.url.startsWith('/range')) {
    const m = (months) => { const x = new Date(); x.setMonth(x.getMonth() + months); const p = (n) => String(n).padStart(2, '0'); return `${x.getFullYear()}${p(x.getMonth() + 1)}${p(x.getDate())}`; };
    const ev = (uid, day, title) => ['BEGIN:VEVENT', `UID:${uid}`, `DTSTART;VALUE=DATE:${day}`, `SUMMARY:${title}`, 'END:VEVENT'];
    const body = ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//test//EN', ...ev('r1', m(-4), 'old'), ...ev('r2', d(10), 'near'), ...ev('r3', m(14), 'far'), 'END:VCALENDAR', ''].join('\r\n');
    res.writeHead(200, { 'Content-Type': 'text/calendar; charset=utf-8' });
    res.end(body);
    return;
  }
  if (req.url.startsWith('/fail')) { res.writeHead(500); res.end('boom'); return; }
  res.writeHead(200, { 'Content-Type': 'text/calendar; charset=utf-8' });
  res.end(ics());
}).listen(18765, '127.0.0.1', () => fs.appendFileSync(logFile, 'listening\n'));
