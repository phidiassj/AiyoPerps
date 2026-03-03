#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { stdin, stdout, stderr, exit, argv, env } = require('node:process');

const options = parseOptions(argv.slice(2));
const endpoint = options.url;
let buffer = Buffer.alloc(0);
let expectedBodyLength = null;

debugTrace(`process started argv=${JSON.stringify(argv.slice(2))} endpoint=${endpoint}`);

if (options.help) {
  stdout.write(buildHelpText());
  exit(0);
}

bootstrap().catch(error => {
  debugTrace(`startup failed error=${error.stack || error.message}`);
  logError(`[aiyoperps-mcp-bridge] startup failed: ${error.message}`);
  exit(1);
});

async function bootstrap() {
  if (options.healthCheck || options.startupPing) {
    debugTrace(`pingUpstream start healthCheck=${options.healthCheck} startupPing=${options.startupPing}`);
    await pingUpstream();
    debugTrace('pingUpstream ok');
    if (options.healthCheck && !options.startupPing) {
      logInfo(`[aiyoperps-mcp-bridge] health check ok ${endpoint}`);
      exit(0);
      return;
    }
  }

  logInfo(`[aiyoperps-mcp-bridge] forwarding stdio MCP to ${endpoint}`);
  debugTrace('stdio forwarding active');

  stdin.on('data', chunk => {
    debugTrace(`stdin data bytes=${chunk.length} preview=${JSON.stringify(truncateForDebug(chunk.toString('utf8'), 240))}`);
    buffer = Buffer.concat([buffer, chunk]);
    tryProcessBuffer();
  });

  stdin.on('end', () => {
    debugTrace('stdin end');
    logInfo('[aiyoperps-mcp-bridge] stdin closed');
    exit(0);
  });

  stdin.on('error', error => {
    debugTrace(`stdin error=${error.message}`);
    logError(`[aiyoperps-mcp-bridge] stdin error: ${error.message}`);
    exit(1);
  });
}

function tryProcessBuffer() {
  while (true) {
    if (expectedBodyLength === null) {
      const headerInfo = findHeaderBoundary(buffer);
      if (!headerInfo) {
        const jsonLineInfo = findJsonLineBoundary(buffer);
        if (!jsonLineInfo) {
          debugTrace(`header boundary not found bufferBytes=${buffer.length} preview=${JSON.stringify(truncateForDebug(buffer.toString('utf8'), 240))}`);
          return;
        }

        const body = buffer.subarray(0, jsonLineInfo.bodyLength);
        buffer = buffer.subarray(jsonLineInfo.nextOffset);
        debugTrace(`json line parsed bytes=${jsonLineInfo.bodyLength}`);
        handleMessage(body, 'json-line').catch(error => {
          debugTrace(`message handling failed error=${error.stack || error.message}`);
          logError(`[aiyoperps-mcp-bridge] message handling failed: ${error.stack || error.message}`);
          exit(1);
        });
        continue;
      }

      const headerText = buffer.subarray(0, headerInfo.index).toString('utf8');
      const lengthMatch = /content-length:\s*(\d+)/i.exec(headerText);
      if (!lengthMatch) {
        debugTrace(`invalid frame missing content-length header=${JSON.stringify(truncateForDebug(headerText, 240))}`);
        logError('[aiyoperps-mcp-bridge] invalid MCP frame: missing Content-Length');
        exit(1);
      }

      expectedBodyLength = Number.parseInt(lengthMatch[1], 10);
      debugTrace(`header parsed separator=${headerInfo.separator} contentLength=${expectedBodyLength}`);
      buffer = buffer.subarray(headerInfo.bodyOffset);
    }

    if (buffer.length < expectedBodyLength) {
      debugTrace(`waiting for body bufferBytes=${buffer.length} expected=${expectedBodyLength}`);
      return;
    }

    const body = buffer.subarray(0, expectedBodyLength);
    buffer = buffer.subarray(expectedBodyLength);
    expectedBodyLength = null;

    handleMessage(body, 'framed').catch(error => {
      debugTrace(`message handling failed error=${error.stack || error.message}`);
      logError(`[aiyoperps-mcp-bridge] message handling failed: ${error.stack || error.message}`);
      exit(1);
    });
  }
}

function findJsonLineBoundary(sourceBuffer) {
  const newlineIndex = sourceBuffer.indexOf('\n');
  if (newlineIndex === -1) {
    return null;
  }

  const lineBuffer = sourceBuffer.subarray(0, newlineIndex);
  const lineText = lineBuffer.toString('utf8').trim();
  if (!lineText.startsWith('{') && !lineText.startsWith('[')) {
    return null;
  }

  return {
    bodyLength: lineBuffer.length,
    nextOffset: newlineIndex + 1
  };
}

function findHeaderBoundary(sourceBuffer) {
  const crlfIndex = sourceBuffer.indexOf('\r\n\r\n');
  const lfIndex = sourceBuffer.indexOf('\n\n');

  if (crlfIndex === -1 && lfIndex === -1) {
    return null;
  }

  if (crlfIndex !== -1 && (lfIndex === -1 || crlfIndex <= lfIndex)) {
    return {
      index: crlfIndex,
      bodyOffset: crlfIndex + 4,
      separator: 'crlf'
    };
  }

  return {
    index: lfIndex,
    bodyOffset: lfIndex + 2,
    separator: 'lf'
  };
}

async function handleMessage(bodyBuffer, outputMode) {
  const bodyText = bodyBuffer.toString('utf8');
  let payload;

  try {
    payload = JSON.parse(bodyText);
    debugTrace(`client json parsed method=${payload?.method ?? '(none)'} id=${formatIdForDebug(payload?.id)} bytes=${bodyBuffer.length}`);
  } catch (error) {
    debugTrace(`invalid client json bytes=${bodyBuffer.length} error=${error.message} preview=${JSON.stringify(truncateForDebug(bodyText, 240))}`);
    logError(`[aiyoperps-mcp-bridge] invalid JSON from client: ${error.message}`);
    writeResponse(JSON.stringify({
      jsonrpc: '2.0',
      error: {
        code: -32700,
        message: 'Invalid JSON received by bridge.'
      },
      id: null
    }), outputMode);
    return;
  }

  const shouldRespond = hasResponseId(payload);

  let response;
  try {
    response = await postJson(endpoint, payload);
    debugTrace(`upstream json ok method=${payload?.method ?? '(none)'} id=${formatIdForDebug(payload?.id)}`);
  } catch (error) {
    debugTrace(`upstream request failed method=${payload?.method ?? '(none)'} id=${formatIdForDebug(payload?.id)} error=${error.message}`);
    logError(`[aiyoperps-mcp-bridge] upstream request failed: ${error.message}`);
    if (shouldRespond) {
      writeResponse(JSON.stringify({
        jsonrpc: '2.0',
        error: {
          code: -32000,
          message: `AiyoPerps MCP endpoint unavailable: ${error.message}`
        },
        id: payload && typeof payload === 'object' && 'id' in payload ? payload.id : null
      }), outputMode);
    }
    return;
  }

  if (!shouldRespond) {
    debugTrace(`notification handled method=${payload?.method ?? '(none)'} no response`);
    return;
  }

  writeResponse(JSON.stringify(response), outputMode);
}

async function postJson(url, payload) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), options.timeoutMs);

  try {
    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(payload),
      signal: controller.signal
    });

    const text = await response.text();
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}: ${text || response.statusText}`);
    }

    try {
      return JSON.parse(text);
    } catch (error) {
      throw new Error(`Invalid JSON from AiyoPerps MCP endpoint: ${error.message}`);
    }
  } catch (error) {
    if (error && error.name === 'AbortError') {
      throw new Error(`Request timeout after ${options.timeoutMs}ms`);
    }

    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

function writeResponse(jsonText, outputMode) {
  if (outputMode === 'json-line') {
    const payload = `${jsonText}\n`;
    debugTrace(`stdout json-line bytes=${Buffer.byteLength(payload, 'utf8')}`);
    stdout.write(payload);
    return;
  }

  writeFrame(jsonText);
}

async function pingUpstream() {
  const response = await postJson(endpoint, {
    jsonrpc: '2.0',
    id: 'startup-ping',
    method: 'ping',
    params: {}
  });

  if (!response || response.error) {
    const message = response?.error?.message || 'Unexpected MCP ping failure.';
    throw new Error(message);
  }
}

function writeFrame(jsonText) {
  const body = Buffer.from(jsonText, 'utf8');
  const header = Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'utf8');
  debugTrace(`stdout frame bytes=${body.length}`);
  stdout.write(Buffer.concat([header, body]));
}

function hasResponseId(payload) {
  return Boolean(payload) &&
    typeof payload === 'object' &&
    Object.prototype.hasOwnProperty.call(payload, 'id') &&
    payload.id !== undefined &&
    payload.id !== null;
}

function parseOptions(args) {
  const defaultUrl = env.AIYOPERPS_MCP_URL || 'http://127.0.0.1:5078/mcp';
  const options = {
    url: defaultUrl,
    healthCheck: false,
    startupPing: false,
    quiet: false,
    debugLog: '',
    help: false,
    timeoutMs: 5000
  };

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg.startsWith('--url=')) {
      options.url = arg.slice('--url='.length);
      continue;
    }

    if (arg === '--url' && index + 1 < args.length) {
      options.url = args[index + 1];
      index += 1;
      continue;
    }

    if (arg === '--health-check') {
      options.healthCheck = true;
      continue;
    }

    if (arg === '--startup-ping') {
      options.startupPing = true;
      continue;
    }

    if (arg === '--quiet') {
      options.quiet = true;
      continue;
    }

    if (arg.startsWith('--debug-log=')) {
      options.debugLog = arg.slice('--debug-log='.length);
      continue;
    }

    if (arg === '--debug-log' && index + 1 < args.length) {
      options.debugLog = args[index + 1];
      index += 1;
      continue;
    }

    if (arg === '--help' || arg === '-h') {
      options.help = true;
      continue;
    }

    if (arg.startsWith('--timeout-ms=')) {
      options.timeoutMs = parseTimeout(arg.slice('--timeout-ms='.length), options.timeoutMs);
      continue;
    }

    if (arg === '--timeout-ms' && index + 1 < args.length) {
      options.timeoutMs = parseTimeout(args[index + 1], options.timeoutMs);
      index += 1;
    }
  }

  return options;
}

function parseTimeout(raw, fallback) {
  const value = Number.parseInt(raw, 10);
  return Number.isInteger(value) && value > 0 ? value : fallback;
}

function buildHelpText() {
  return [
    'AiyoPerps MCP Bridge',
    '',
    'Usage:',
    '  npx -y @phidiassj/aiyoperps-mcp-bridge [options]',
    '',
    'Options:',
    '  --url <endpoint>       Target HTTP MCP endpoint (default: http://127.0.0.1:5078/mcp)',
    '  --health-check         Verify the upstream MCP endpoint with a ping and exit',
    '  --startup-ping         Ping the upstream MCP endpoint before entering stdio mode',
    '  --timeout-ms <ms>      Request timeout for health/startup ping and upstream calls (default: 5000)',
    '  --quiet                Suppress bridge diagnostic messages on stderr',
    '  --debug-log <path>     Append bridge diagnostics to a local log file',
    '  --help, -h             Show this help message',
    ''
  ].join('\n');
}

function debugTrace(message) {
  if (!options.debugLog) {
    return;
  }

  try {
    fs.mkdirSync(path.dirname(options.debugLog), { recursive: true });
    fs.appendFileSync(options.debugLog, `${new Date().toISOString()} pid=${process.pid} ${message}\n`, 'utf8');
  } catch {
    // Do not throw from debug logging.
  }
}

function truncateForDebug(text, maxLength) {
  if (typeof text !== 'string' || text.length <= maxLength) {
    return text;
  }

  return `${text.slice(0, maxLength)}...`;
}

function formatIdForDebug(id) {
  if (id === undefined || id === null) {
    return '(null)';
  }

  if (typeof id === 'string') {
    return truncateForDebug(id, 64);
  }

  return String(id);
}

function logInfo(message) {
  if (!options.quiet) {
    stderr.write(`${message}\n`);
  }
}

function logError(message) {
  stderr.write(`${message}\n`);
}
