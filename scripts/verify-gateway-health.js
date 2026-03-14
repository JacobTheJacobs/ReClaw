#!/usr/bin/env node
const http = require('http');
const https = require('https');

function parseArgs(argv) {
  const out = {
    url: process.env.RECLAW_HEALTH_URL || 'http://127.0.0.1:18789/healthz',
    timeoutMs: Number(process.env.RECLAW_HEALTH_TIMEOUT_MS || 10000),
  };

  for (let i = 0; i < argv.length; i += 1) {
    const token = argv[i];
    if (token === '--url' && argv[i + 1]) {
      out.url = argv[i + 1];
      i += 1;
    } else if (token === '--timeout' && argv[i + 1]) {
      const parsed = Number(argv[i + 1]);
      if (Number.isFinite(parsed) && parsed > 0) {
        out.timeoutMs = parsed;
      }
      i += 1;
    }
  }

  return out;
}

function requestHealth(url, timeoutMs) {
  return new Promise((resolve, reject) => {
    const requestLib = url.startsWith('https://') ? https : http;
    const req = requestLib.get(url, { timeout: timeoutMs }, (res) => {
      const { statusCode } = res;
      let body = '';

      res.on('data', (chunk) => {
        body += chunk.toString();
      });

      res.on('end', () => {
        if (statusCode >= 200 && statusCode < 300) {
          resolve({ statusCode, body });
          return;
        }
        reject(new Error(`Health check failed with status ${statusCode}. Body: ${body}`));
      });
    });

    req.on('timeout', () => {
      req.destroy(new Error(`Health check timed out after ${timeoutMs} ms.`));
    });

    req.on('error', (error) => {
      reject(error);
    });
  });
}

async function main() {
  const { url, timeoutMs } = parseArgs(process.argv.slice(2));
  const result = await requestHealth(url, timeoutMs);
  console.log(`health_status:${result.statusCode}`);
  if (result.body && result.body.trim()) {
    console.log(`health_body:${result.body.trim()}`);
  }
}

main().catch((error) => {
  console.error(`health_error:${error.message}`);
  process.exit(1);
});
