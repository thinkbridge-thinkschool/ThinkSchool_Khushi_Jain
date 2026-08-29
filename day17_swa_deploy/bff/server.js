const http = require('node:http');
const { DefaultAzureCredential } = require('@azure/identity');

const port = Number(process.env.PORT ?? 8080);
const apiBaseUrl = process.env.QUOTES_API_BASE_URL ?? '';
const apiScope = process.env.QUOTES_API_SCOPE ?? '';
const introspectionEnabled = (process.env.TOKEN_INTROSPECTION_ENABLED ?? '').toLowerCase() === 'true';

const credential = new DefaultAzureCredential();

const AUTH_ACTIONS = new Set(['register', 'login', 'refresh', 'logout']);

function send(res, status, body, headers = {}) {
  const payload = typeof body === 'string' ? body : JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json',
    'Cache-Control': 'no-store',
    ...headers,
  });
  res.end(payload);
}

function problem(res, status, detail) {
  send(res, status, { status, detail }, { 'Content-Type': 'application/problem+json' });
}

async function accessToken() {
  if (!apiScope) {
    return null;
  }

  try {
    const granted = await credential.getToken(apiScope);

    return granted?.token ?? null;
  } catch (error) {
    console.error('Managed-identity token request failed:', error.message);

    return null;
  }
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => resolve(Buffer.concat(chunks).toString()));
    req.on('error', reject);
  });
}

async function relay(req, res, path, search, token, body) {
  const target = new URL(path + search, apiBaseUrl);
  const headers = { Accept: 'application/json' };

  if (body !== undefined && body !== '') {
    headers['Content-Type'] = 'application/json';
  }

  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  let upstream;

  try {
    upstream = await fetch(target, { method: req.method, headers, body });
  } catch (error) {
    console.error(`Could not reach ${target.pathname}:`, error.message);

    return problem(res, 502, 'Could not reach the quotes service.');
  }

  const text = await upstream.text();

  console.log(
    `${req.method} ${target.pathname}${target.search} -> ${upstream.status} ` +
      `(managed-identity token ${token ? 'attached' : 'not attached'})`,
  );

  send(res, upstream.status, text, {
    'Content-Type': upstream.headers.get('content-type') ?? 'application/json',
    'x-managed-identity-token': token ? 'attached' : 'unavailable',
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host ?? 'localhost'}`);
  const path = url.pathname;

  if (path === '/health') {
    return send(res, 200, { status: 'ok' });
  }

  if (!apiBaseUrl) {
    return problem(res, 500, 'The site is not configured to reach the quotes service.');
  }

  if (req.method === 'GET' && path === '/api/token-check') {
    if (!introspectionEnabled) {
      return problem(res, 404, 'That was not found.');
    }

    const token = await accessToken();

    if (!token) {
      return problem(res, 503, 'No managed-identity token could be obtained.');
    }

    const claims = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString());

    return send(res, 200, {
      aud: claims.aud,
      iss: claims.iss,
      appid: claims.appid ?? claims.azp,
      oid: claims.oid,
      idtyp: claims.idtyp,
      roles: claims.roles ?? null,
      scp: claims.scp ?? null,
      exp: claims.exp,
    });
  }

  if (req.method === 'GET' && path === '/api/write-probe') {
    if (!introspectionEnabled) {
      return problem(res, 404, 'That was not found.');
    }

    const token = await accessToken();

    if (!token) {
      return problem(res, 503, 'No managed-identity token could be obtained.');
    }

    const probe = await fetch(new URL('/api/quotes', apiBaseUrl), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ author: 'managed-identity probe', text: 'managed-identity probe' }),
    });

    return send(res, 200, {
      upstreamStatus: probe.status,
      meaning:
        probe.status === 401
          ? 'token rejected - not authenticated'
          : probe.status === 403
            ? 'token accepted - authenticated but missing the quotes.write scope'
            : 'token accepted and authorized',
    });
  }

  if (req.method === 'GET' && (path === '/api/quotes' || /^\/api\/quotes\/\d+$/.test(path))) {
    return relay(req, res, path, url.search, await accessToken(), undefined);
  }

  const authMatch = /^\/api\/auth\/([a-z]+)$/.exec(path);

  if (req.method === 'POST' && authMatch && AUTH_ACTIONS.has(authMatch[1])) {
    return relay(req, res, path, '', null, await readBody(req));
  }

  return problem(res, 404, 'That was not found.');
});

server.listen(port, () => console.log(`quotes-bff listening on ${port}`));
