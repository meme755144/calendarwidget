import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';

const logFile = process.argv[2];
const PORT = 18766;
let tokenCounter = 0;
const registered = {};   // code -> challenge
const registeredScope = {};     // code -> 要求的授權範圍
const registeredInclude = {};   // code -> 是否 include_granted_scopes
let granted = new Set();        // 目前已經同意的授權範圍（重新登入時重置）
let lastCreated = null;
let lastDeleted = null;
let lastPatched = null;         // 最近一次新增行程送來的內容
const BASE_SCOPES = ['https://www.googleapis.com/auth/calendar.readonly', 'https://www.googleapis.com/auth/tasks.readonly'];
const WRITE_SCOPE = 'https://www.googleapis.com/auth/calendar.events';

function log(s) {
  // 測試程式正在讀 log 時檔案會暫時被鎖住（EBUSY），重試幾次，還是不行就略過這一行
  for (let i = 0; i < 100; i++) {
    try { fs.appendFileSync(logFile, s + '\n'); return; } catch (e) { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 10); }
  }
}

const p2 = (n) => String(n).padStart(2, '0');
function dstr(offset) {
  const t = new Date(); t.setDate(t.getDate() + offset);
  return `${t.getFullYear()}-${p2(t.getMonth() + 1)}-${p2(t.getDate())}`;
}
// 台北時間（+08:00）的時間字串
function dt(offset, hh, mm) { return `${dstr(offset)}T${p2(hh)}:${p2(mm)}:00+08:00`; }

function json(res, status, obj) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(obj));
}

function readBody(req) {
  return new Promise((resolve) => { let b = ''; req.on('data', (c) => (b += c)); req.on('end', () => resolve(b)); });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${PORT}`);
  const auth = req.headers['authorization'] || '';
  log(`${req.method} ${url.pathname}${url.search ? '?' + url.search.slice(1, 90) : ''} auth=${auth ? auth.slice(0, 14) : '-'}`);

  if (req.method === 'POST' && url.pathname === '/register') {
    const code = url.searchParams.get('code');
    registered[code] = url.searchParams.get('challenge');
    registeredScope[code] = url.searchParams.get('scope') || BASE_SCOPES.join(' ');
    registeredInclude[code] = url.searchParams.get('include') === 'true';
    return json(res, 200, { ok: true });
  }

  if (req.method === 'GET' && url.pathname === '/last-created') return json(res, 200, lastCreated || {});
  if (req.method === 'GET' && url.pathname === '/last-patched') return json(res, 200, lastPatched || {});
  if (req.method === 'GET' && url.pathname === '/last-deleted') return json(res, 200, lastDeleted || {});
  if (req.method === 'POST' && url.pathname === '/reset-scopes') { granted = new Set(BASE_SCOPES); lastCreated = null; lastDeleted = null; lastPatched = null; return json(res, 200, {}); }

  if (req.method === 'POST' && url.pathname === '/token') {
    const form = new URLSearchParams(await readBody(req));
    const grant = form.get('grant_type');
    if (grant === 'authorization_code') {
      const challenge = registered[form.get('code')];
      const verifierHash = crypto.createHash('sha256').update(form.get('code_verifier') || '').digest('base64url');
      if (!challenge || challenge !== verifierHash) return json(res, 400, { error: 'invalid_grant', error_description: 'bad verifier' });
      if (form.get('client_secret') !== 'test-secret') return json(res, 401, { error: 'invalid_client' });
      // 授權範圍：有帶 include_granted_scopes 就保留之前同意過的，否則只有這次要求的
      const requested = (registeredScope[form.get('code')] || '').split(' ').filter(Boolean);
      granted = registeredInclude[form.get('code')] ? new Set([...granted, ...requested]) : new Set(requested);
      return json(res, 200, { access_token: 'AT-auth', refresh_token: 'RT-1', expires_in: 3600, token_type: 'Bearer', scope: [...granted].join(' ') });
    }
    if (grant === 'refresh_token') {
      if (form.get('refresh_token') !== 'RT-1') return json(res, 400, { error: 'invalid_grant' });
      tokenCounter++;
      if (granted.size === 0) granted = new Set(BASE_SCOPES);
      return json(res, 200, { access_token: 'AT-' + tokenCounter, expires_in: 3600, token_type: 'Bearer', scope: [...granted].join(' ') });
    }
    return json(res, 400, { error: 'unsupported_grant_type' });
  }

  if (req.method === 'POST' && url.pathname === '/revoke') return json(res, 200, {});

  // 以下都需要 Bearer 權杖
  if (!auth.startsWith('Bearer AT-')) return json(res, 401, { error: { code: 401, message: 'Invalid Credentials' } });

  if (url.pathname === '/calendar/v3/users/me/calendarList') {
    return json(res, 200, { items: [
      { id: 'me@example.com', summary: 'me@example.com', primary: true, selected: true, backgroundColor: '#9fe1e7', accessRole: 'owner' },
      { id: 'zh-tw.taiwan#holiday@group.v.calendar.google.com', summary: '台灣的節日', selected: true, backgroundColor: '#16a765', accessRole: 'reader' },
      { id: 'team@group.calendar.google.com', summary: '團隊', summaryOverride: '我的團隊', selected: false, backgroundColor: '#f83a22', accessRole: 'writer' },
      { id: 'forbidden@group.calendar.google.com', summary: '沒權限', selected: false, accessRole: 'reader' }
    ] });
  }

  // 新增行程（POST）：需要有「寫入」的授權範圍
  const created = url.pathname.match(/^\/calendar\/v3\/calendars\/(.+)\/events$/);
  if (created && req.method === 'POST') {
    const id = decodeURIComponent(created[1]);
    if (!granted.has(WRITE_SCOPE)) {
      return json(res, 403, { error: { code: 403, message: 'Request had insufficient authentication scopes.', status: 'PERMISSION_DENIED', errors: [{ reason: 'insufficientPermissions' }] } });
    }
    if (id.startsWith('readonly') || id.startsWith('forbidden') || id.includes('#holiday@')) {
      return json(res, 403, { error: { code: 403, message: 'You need to have writer access to this calendar.' } });
    }
    lastCreated = { calendarId: id, body: JSON.parse(await readBody(req)) };
    return json(res, 200, { id: 'new-1' });
  }

  // 修改行程（PATCH）：同樣需要寫入授權
  const patched = url.pathname.match(/^\/calendar\/v3\/calendars\/(.+)\/events\/(.+)$/);
  if (patched && req.method === 'PATCH') {
    const id = decodeURIComponent(patched[1]);
    const evId = decodeURIComponent(patched[2]);
    if (!granted.has(WRITE_SCOPE)) {
      return json(res, 403, { error: { code: 403, message: 'Request had insufficient authentication scopes.', status: 'PERMISSION_DENIED', errors: [{ reason: 'insufficientPermissions' }] } });
    }
    if (id.startsWith('readonly') || id.startsWith('forbidden') || id.includes('#holiday@')) {
      return json(res, 403, { error: { code: 403, message: 'You need to have writer access to this calendar.' } });
    }
    if (evId === 'gone-1') return json(res, 404, { error: { code: 404, message: 'Not Found' } });
    lastPatched = { calendarId: id, eventId: evId, body: JSON.parse(await readBody(req)) };
    return json(res, 200, { id: evId });
  }

  // 刪除行程（DELETE）：同樣需要寫入授權
  const deleted = url.pathname.match(/^\/calendar\/v3\/calendars\/(.+)\/events\/(.+)$/);
  if (deleted && req.method === 'DELETE') {
    const id = decodeURIComponent(deleted[1]);
    const evId = decodeURIComponent(deleted[2]);
    if (!granted.has(WRITE_SCOPE)) {
      return json(res, 403, { error: { code: 403, message: 'Request had insufficient authentication scopes.', status: 'PERMISSION_DENIED', errors: [{ reason: 'insufficientPermissions' }] } });
    }
    if (id.startsWith('readonly') || id.startsWith('forbidden') || id.includes('#holiday@')) {
      return json(res, 403, { error: { code: 403, message: 'You need to have writer access to this calendar.' } });
    }
    lastDeleted = { calendarId: id, eventId: evId };
    if (evId === 'gone-1') return json(res, 410, { error: { code: 410, message: 'Resource has been deleted' } });
    res.writeHead(204); return res.end();
  }

  const ev = url.pathname.match(/^\/calendar\/v3\/calendars\/(.+)\/events$/);
  if (ev) {
    const id = decodeURIComponent(ev[1]);
    if (id.startsWith('forbidden')) return json(res, 403, { error: { code: 403, message: 'Forbidden' } });
    if (id === 'me@example.com') {
      const defaultReminders = [ { method: 'popup', minutes: 10 }, { method: 'email', minutes: 30 } ];
      if (!url.searchParams.get('pageToken')) {
        return json(res, 200, { nextPageToken: 'p2', defaultReminders, items: [
          // 用日曆的預設提醒（popup 10 分鐘；email 30 分鐘不算）
          { id: 'ev-visit', summary: '軟體園區主任來訪', status: 'confirmed', start: { dateTime: dt(0, 14, 0) }, end: { dateTime: dt(0, 15, 0) }, reminders: { useDefault: true } },
          // 自己的提醒：popup 5 分鐘、email 60 分鐘（不算）、popup 1 天
          { id: 'ev-standup_20261027T013000Z', recurringEventId: 'ev-standup', summary: '晨會', status: 'confirmed', start: { dateTime: dt(0, 9, 30), timeZone: 'Asia/Taipei' }, end: { dateTime: dt(0, 10, 0) },
            reminders: { useDefault: false, overrides: [ { method: 'popup', minutes: 5 }, { method: 'email', minutes: 60 }, { method: 'popup', minutes: 1440 } ] } }
        ] });
      }
      return json(res, 200, { defaultReminders, items: [
        // 整天事件：前一天 9:00（900 分鐘）
        { summary: '連休三天', status: 'confirmed', start: { date: dstr(3) }, end: { date: dstr(6) }, reminders: { useDefault: false, overrides: [ { method: 'popup', minutes: 900 } ] } },
        { summary: '已取消的會議', status: 'cancelled', start: { dateTime: dt(1, 10, 0) }, end: { dateTime: dt(1, 11, 0) } },
        { status: 'confirmed', start: { date: dstr(1) }, end: { date: dstr(2) } },
        { summary: 'UTC 時間的行程', status: 'confirmed', start: { dateTime: `${dstr(2)}T02:00:00Z` }, end: { dateTime: `${dstr(2)}T03:00:00Z` } }
      ] });
    }
    if (id.startsWith('zh-tw.taiwan')) {
      return json(res, 200, { items: [ { summary: '光復節', status: 'confirmed', start: { date: dstr(1) }, end: { date: dstr(2) } } ] });
    }
    return json(res, 200, { items: [ { summary: '團隊站會', status: 'confirmed', start: { dateTime: dt(4, 11, 0) }, end: { dateTime: dt(4, 11, 30) } } ] });
  }

  if (url.pathname === '/tasks/v1/users/@me/lists') return json(res, 200, { items: [ { id: 'L1' } ] });

  if (url.pathname === '/tasks/v1/lists/L1/tasks') {
    return json(res, 200, { items: [
      { title: '報帳', status: 'needsAction', due: `${dstr(2)}T00:00:00.000Z` },
      { title: '已完成的待辦', status: 'completed', due: `${dstr(0)}T00:00:00.000Z` },
      { title: '沒有到期日', status: 'needsAction' },
      { title: '', status: 'needsAction', due: `${dstr(1)}T00:00:00.000Z` }
    ] });
  }

  json(res, 404, { error: { code: 404, message: 'not found ' + url.pathname } });
});

server.listen(PORT, '127.0.0.1', () => log('listening'));
