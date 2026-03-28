#!/usr/bin/env node
const http = require('http')
const { argv } = require('process')

const DEFAULTS = {
  endpoints: [ { host: '127.0.0.1', port: 3417 }, { host: '127.0.0.1', port: 8080 }, { host: '127.0.0.1', port: 3000 } ],
  timeout: 2000,
  retries: 3
}

function parseArgs(){
  const out = { mock: false }
  for (const a of argv.slice(2)) if (a === '--mock') out.mock = true
  return out
}

function probe(endpoint, timeout){
  return new Promise((resolve) => {
    const req = http.request({ hostname: endpoint.host, port: endpoint.port, method: 'GET', path: '/', timeout }, res => {
      resolve({ ok: res.statusCode >= 200 && res.statusCode < 400, statusCode: res.statusCode })
    })
    req.on('error', (e) => resolve({ ok: false, error: String(e) }))
    req.on('timeout', () => { req.destroy(); resolve({ ok: false, error: 'timeout' }) })
    req.end()
  })
}

async function checkAll({mock}){
  if (mock) {
    const results = DEFAULTS.endpoints.map(e => ({ host: e.host, port: e.port, ok: true, attempts: 0 }))
    const json = { timestamp: new Date().toISOString(), results, allOk: true }
    console.log(JSON.stringify(json, null, 2))
    process.exit(0)
  }

  const results = []
  for (const e of DEFAULTS.endpoints){
    let ok = false; let attempts = 0; let last = null
    for (let i=0;i<DEFAULTS.retries;i++){
      attempts++
      // eslint-disable-next-line no-await-in-loop
      const r = await probe(e, DEFAULTS.timeout)
      last = r
      if (r.ok) { ok = true; break }
      await new Promise(res => setTimeout(res, 200))
    }
    results.push({ host: e.host, port: e.port, ok, attempts, last })
  }
  const allOk = results.every(r => r.ok)
  const out = { timestamp: new Date().toISOString(), allOk, results }
  console.log(JSON.stringify(out, null, 2))
  process.exit(allOk ? 0 : 2)
}

checkAll(parseArgs())
