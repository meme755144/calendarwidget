// 假的停班停課資料伺服器（取代 taiwan-typhoon-days 的 latest.json）。日期一律用「今天」「明天」現算，測試才不會過期。
//   GET /latest.json                   回傳目前情境的資料；支援 ETag／If-None-Match（沒變回 304）
//   GET /_mode?name=announced&ago=0    切換情境；ago 是 checkedAt 在幾個小時前（模擬資料過期）
//   GET /_hits                         回傳 { total, notModified, ifNoneMatch }（最近一次請求帶的 If-None-Match）
import http from 'node:http';
import crypto from 'node:crypto';

const pad = (n) => String(n).padStart(2, '0');
const ymd = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const day = (offset) => new Date(Date.now() + offset * 86400000);

function item(offset, over = {}) {
  const d = day(offset);
  return {
    date: ymd(d), scope: '', scopeKind: 'city', work: true, school: true, period: '', from: '',
    text: `${d.getMonth() + 1}/${d.getDate()}停止上班、停止上課。`, ...over
  };
}

function base(checkedAt) {
  return {
    schema: 2,
    source: 'https://www.dgpa.gov.tw/typh/daily/nds.html',
    checkedAt: checkedAt.toISOString(),
    pageDate: ymd(day(0)),
    pageUpdatedAt: `${ymd(day(0))}T10:00:00+08:00`,
    state: 'none', cities: {}, schools: {}, unparsedText: '', validators: {}, error: null
  };
}

const scenarios = {
  none: (b) => b,
  // 高雄市明天整個縣市停班停課、那瑪夏區今天停班停課（山區）、單一學校；臺北市今天整個縣市
  announced: (b) => ({
    ...b, state: 'announced',
    cities: {
      臺北市: [item(0)],
      高雄市: [item(0, { scope: '那瑪夏區', scopeKind: 'area' }), item(1)]
    },
    schools: { 高雄市: [item(1, { scope: '高雄市某某國民中學', scopeKind: 'school', work: false })] }
  }),
  // 只有高雄市前鎮區明天下午停班停課
  area: (b) => ({
    ...b, state: 'announced',
    cities: { 高雄市: [item(1, { scope: '前鎮區', scopeKind: 'area', period: 'afternoon', from: '13:00', text: `${day(1).getMonth() + 1}/${day(1).getDate()}下午01:00起停止上班、停止上課。` })] }
  }),
  unparsed: (b) => ({ ...b, state: 'unparsed', unparsedText: '颱風來襲，高雄市明天停班停課' }),
  unknown: (b) => ({ ...b, state: 'unparsed', cities: { 高雄市: [{ unknown: true, text: '明天全市停班停課，請注意安全' }] } }),
  error: (b) => ({ ...b, error: { since: b.checkedAt, lastAt: b.checkedAt, status: 403, message: '來源網站拒絕連線（HTTP 403），可能是被擋了', count: 1, backoffUntil: b.checkedAt } })
};

const state = { name: 'none', ago: 0, at: new Date(), total: 0, notModified: 0, ifNoneMatch: '', raw: null, status: 200 };

function body() {
  if (state.raw !== null) return state.raw;
  const build = scenarios[state.name];
  return JSON.stringify(build(base(state.at)), null, 2) + '\n';
}

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/_mode') {
    state.name = url.searchParams.get('name') || 'none';
    state.ago = Number(url.searchParams.get('ago') || 0);
    state.at = new Date(Date.now() - state.ago * 3600000 - (Number(url.searchParams.get('bump') || 0) * 1000));
    state.raw = url.searchParams.has('raw') ? url.searchParams.get('raw') : null;
    state.status = Number(url.searchParams.get('status') || 200);
    res.writeHead(200);
    return res.end('ok');
  }
  if (url.pathname === '/_hits') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({ total: state.total, notModified: state.notModified, ifNoneMatch: state.ifNoneMatch }));
  }
  if (url.pathname !== '/latest.json') { res.writeHead(404); return res.end('not found'); }

  state.total++;
  state.ifNoneMatch = req.headers['if-none-match'] || '';
  if (state.status !== 200) { res.writeHead(state.status); return res.end('error'); }

  const text = body();
  const etag = '"' + crypto.createHash('sha1').update(text).digest('hex').slice(0, 16) + '"';
  if (state.ifNoneMatch === etag) {
    state.notModified++;
    res.writeHead(304, { ETag: etag });
    return res.end();
  }
  res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', ETag: etag });
  res.end(text);
}).listen(18768, '127.0.0.1');
