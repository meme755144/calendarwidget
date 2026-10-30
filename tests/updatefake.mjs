// 假的 GitHub「最新發行版本」API，給 updatetest 用：node updatefake.mjs
// 路徑決定回什麼：/new /same /old /evil /none /limit /boom /garbage
import http from 'node:http';

const release = (tag, url, body = '') => JSON.stringify({ tag_name: tag, html_url: url, body });
const official = (tag) => `https://github.com/meme755144/calendarwidget/releases/tag/${tag}`;

const routes = {
  '/new': [200, release('v0.9.1', official('v0.9.1'), '## 新功能\r\n- 修正月曆顯示\r\n- 新增設定項目\r\n\r\n**完整說明**請看 CHANGELOG')],
  '/same': [200, release('v0.9.0', official('v0.9.0'))],
  '/old': [200, release('v0.8.0', official('v0.8.0'))],
  '/evil': [200, release('v1.0.0', 'https://evil.example/download.exe', 'x')],
  '/none': [404, '{"message":"Not Found"}'],
  '/limit': [403, '{"message":"API rate limit exceeded"}'],
  '/boom': [500, 'oops'],
  '/garbage': [200, 'this is not json']
};

http.createServer((req, res) => {
  const hit = routes[req.url];
  const ua = req.headers['user-agent'] || '';
  if (!hit || !ua.startsWith('CalendarWidget/')) {   // GitHub 的 API 沒有 User-Agent 會拒絕
    res.writeHead(hit ? 403 : 404).end('no');
    return;
  }
  res.writeHead(hit[0], { 'Content-Type': 'application/json; charset=utf-8' }).end(hit[1]);
}).listen(18769, '127.0.0.1', () => console.log('update fake on 18769'));
