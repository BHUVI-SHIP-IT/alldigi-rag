// Zero-dependency mock API for local UI development.
// Serves the 5 endpoints the admin/employee Angular apps call.
// Run: node mock/server.js   (listens on :4300; Angular proxy forwards /api here)

const http = require('http');
const { randomUUID } = require('crypto');

const PORT = process.env.MOCK_PORT ? Number(process.env.MOCK_PORT) : 4300;

// Any email containing "admin" logs in as Admin; everything else is an Employee.
// Password just has to be 6+ chars (matches the form validators). No real auth.
function roleForEmail(email) {
  return /admin/i.test(email) ? 'Admin' : 'Employee';
}

function b64url(obj) {
  return Buffer.from(JSON.stringify(obj))
    .toString('base64')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
}

function makeJwt(email, role) {
  const header = b64url({ alg: 'HS256', typ: 'JWT' });
  const payload = b64url({
    sub: email,
    email,
    role,
    iat: Math.floor(Date.now() / 1000),
    exp: Math.floor(Date.now() / 1000) + 60 * 60 * 8
  });
  return `${header}.${payload}.mock-signature`;
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks)));
  });
}

function sendJson(res, status, data) {
  const body = JSON.stringify(data);
  res.writeHead(status, {
    'Content-Type': 'application/json',
    'Content-Length': Buffer.byteLength(body)
  });
  res.end(body);
}

// Seed data so the admin pages have something to render.
const documents = [
  { id: randomUUID(), fileName: 'employee-handbook.pdf', uploader: 'admin@digitide.com', status: 'indexed', createdAt: new Date(Date.now() - 864e5 * 3).toISOString(), chunkCount: 142 },
  { id: randomUUID(), fileName: 'security-policy.docx', uploader: 'admin@digitide.com', status: 'indexed', createdAt: new Date(Date.now() - 864e5).toISOString(), chunkCount: 58 },
  { id: randomUUID(), fileName: 'q2-roadmap.pptx', uploader: 'admin@digitide.com', status: 'processing', createdAt: new Date().toISOString(), chunkCount: 0 }
];

const auditLogs = [
  { id: randomUUID(), userEmail: 'jane@digitide.com', query: 'What is the leave policy?', retrievedSources: ['employee-handbook.pdf'], createdAt: new Date(Date.now() - 3600e3).toISOString() },
  { id: randomUUID(), userEmail: 'raj@digitide.com', query: 'How do I report a security incident?', retrievedSources: ['security-policy.docx'], createdAt: new Date(Date.now() - 7200e3).toISOString() }
];

const server = http.createServer(async (req, res) => {
  const { method } = req;
  const url = (req.url || '').split('?')[0];

  // CORS / preflight (harmless; proxy makes it same-origin anyway)
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type, Authorization');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  if (method === 'OPTIONS') {
    res.writeHead(204);
    return res.end();
  }

  // POST /api/auth/login -> { token, user }
  if (method === 'POST' && url === '/api/auth/login') {
    const raw = await readBody(req);
    let email = 'user@digitide.com';
    let password = '';
    try {
      const parsed = JSON.parse(raw.toString() || '{}');
      email = parsed.email || email;
      password = parsed.password || '';
    } catch {}
    if (!password || password.length < 6) {
      return sendJson(res, 401, { message: 'Invalid credentials' });
    }
    const role = roleForEmail(email);
    return sendJson(res, 200, { token: makeJwt(email, role), user: { email, role } });
  }

  // GET /api/documents -> DocumentRecord[]
  if (method === 'GET' && url === '/api/documents') {
    return sendJson(res, 200, documents);
  }

  // POST /api/documents (multipart) -> { documentId }
  if (method === 'POST' && url === '/api/documents') {
    await readBody(req); // drain; we don't parse the multipart file
    const id = randomUUID();
    documents.unshift({
      id,
      fileName: `upload-${id.slice(0, 8)}.pdf`,
      uploader: 'admin@digitide.com',
      status: 'queued',
      createdAt: new Date().toISOString(),
      chunkCount: 0
    });
    return sendJson(res, 201, { documentId: id });
  }

  // GET /api/audit -> AuditLog[]
  if (method === 'GET' && url === '/api/audit') {
    return sendJson(res, 200, auditLogs);
  }

  // POST /api/query -> Server-Sent Events stream (sources + tokens)
  if (method === 'POST' && url === '/api/query') {
    const raw = await readBody(req);
    let question = 'your question';
    try {
      question = JSON.parse(raw.toString() || '{}').question || question;
    } catch {}

    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      Connection: 'keep-alive'
    });

    res.write(`event: sources\ndata: ${JSON.stringify(['employee-handbook.pdf', 'security-policy.docx'])}\n\n`);

    const answer = `This is a mock answer to: "${question}". Connect a real backend to get grounded responses from your indexed documents.`;
    const words = answer.split(' ');
    let i = 0;
    const timer = setInterval(() => {
      if (i >= words.length) {
        clearInterval(timer);
        res.write('data: [DONE]\n\n');
        return res.end();
      }
      res.write(`data: ${JSON.stringify({ token: words[i] + ' ' })}\n\n`);
      i += 1;
    }, 60);

    req.on('close', () => clearInterval(timer));
    return;
  }

  sendJson(res, 404, { message: `No mock route for ${method} ${url}` });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Mock API listening on http://127.0.0.1:${PORT}`);
  console.log('Login: any email + any 6+ char password.');
  console.log('  -> email containing "admin" = Admin role, otherwise Employee.');
});
