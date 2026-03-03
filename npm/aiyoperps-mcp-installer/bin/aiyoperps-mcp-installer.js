#!/usr/bin/env node
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline/promises';
import { stdin, stdout, stderr, argv, env, exit, cwd } from 'node:process';
import { spawnSync } from 'node:child_process';
import { parse as parseToml } from 'smol-toml';

const SERVER_NAME = 'aiyoperps';
const DEFAULT_URL = 'http://127.0.0.1:5078/mcp';
const BRIDGE_PACKAGE_NAME = '@phidiassj/aiyoperps-mcp-bridge';
const BRIDGE_SCRIPT_RELATIVE_PATH = path.join(
  'node_modules',
  '@phidiassj',
  'aiyoperps-mcp-bridge',
  'bin',
  'aiyoperps-mcp-bridge.js');
const BRIDGE_DEBUG_LOG_NAME = 'codex-debug.log';
const EXECUTABLE_CACHE = new Map();
let bridgeRuntimePrepared = false;
let bridgeRuntimeAttempted = false;
let bridgeRuntimeWarning = '';

const cli = parseCliArgs(argv.slice(2));
const targetUrl = cli.url;
const interactive = stdin.isTTY && stdout.isTTY && !cli.nonInteractive;

const hosts = await detectHosts(targetUrl);

if (!interactive) {
  await runNonInteractive(cli, hosts, targetUrl);
  exit(0);
}

if (cli.statusOnly) {
  printStatus(hosts, targetUrl);
  exit(0);
}

const rl = readline.createInterface({ input: stdin, output: stdout });

try {
  printBanner(hosts, targetUrl);

  while (true) {
    stdout.write('\n1. install all\n2. install any of\n3. status\n4. uninstall\n5. exit\n');
    const choice = (await rl.question('Select an action: ')).trim();

    if (choice === '1') {
      await installAll(hosts, targetUrl);
      continue;
    }

    if (choice === '2') {
      await installAnyOf(rl, hosts, targetUrl);
      continue;
    }

    if (choice === '3') {
      printStatus(hosts, targetUrl);
      continue;
    }

    if (choice === '4') {
      await uninstallAnyOf(rl, hosts);
      continue;
    }

    if (choice === '5' || choice === '') {
      break;
    }

    stdout.write('Invalid selection.\n');
  }
} finally {
  rl.close();
}

async function detectHosts(url) {
  return [
    await detectCodex(url),
    await detectClaudeCode(url),
    await detectClaudeDesktop(url),
    await detectOpenClaw(url)
  ];
}

async function detectCodex(url) {
  const configPath = resolveCodexConfigPath();
  const exists = fs.existsSync(configPath);
  const parentExists = fs.existsSync(path.dirname(configPath));
  const detected = exists || parentExists;

  let safelyWritable = parentExists;
  let installed = false;
  let reason = detected ? 'ready for TOML block patch' : 'config directory not found';

  if (exists) {
    try {
      const content = fs.readFileSync(configPath, 'utf8');
      parseToml(content);
      installed = hasCodexBlock(content);
      reason = installed ? 'installed' : 'config parsed successfully';
    } catch (error) {
      safelyWritable = false;
      reason = `config parse failed: ${error.message}`;
    }
  }

  return {
    id: 'codex',
    name: 'Codex',
    kind: 'file',
    detected,
    supported: safelyWritable,
    installed,
    reason,
    path: configPath,
    install: () => installCodex(configPath, url),
    uninstall: () => uninstallCodex(configPath)
  };
}

async function detectClaudeCode(url) {
  const version = runCommand('claude', ['--version']);
  const detected = version.ok;
  const mcpList = detected ? runCommand('claude', ['mcp', 'list']) : { ok: false, stdout: '' };
  const installed = detected && mcpList.ok && mcpList.stdout.toLowerCase().includes(SERVER_NAME);
  const reason = detected
    ? 'official CLI integration available via claude mcp'
    : 'claude command not found in PATH';

  return {
    id: 'claude-code',
    name: 'Claude Code CLI',
    kind: 'cli',
    detected,
    supported: detected,
    installed,
    reason,
    path: detected ? 'claude mcp (CLI-managed)' : '',
    install: () => installClaudeCode(url),
    uninstall: () => uninstallClaudeCode()
  };
}

async function detectClaudeDesktop(url) {
  const candidates = getClaudeDesktopCandidates();
  const configPaths = selectClaudeDesktopConfigPaths(candidates);
  const primaryPath = configPaths[0];
  const exists = configPaths.some(fileExists);
  const parentExists = configPaths.some(configPath => fs.existsSync(path.dirname(configPath)));
  const baseRootExists = configPaths.some(configPath => fs.existsSync(path.dirname(path.dirname(configPath))));
  const detected = exists || parentExists || baseRootExists;
  let supported = baseRootExists;
  let installed = false;
  let reason = exists
    ? 'ready for JSON config merge'
    : parentExists
      ? 'ready for JSON config merge'
      : baseRootExists
        ? 'default config path can be created'
        : 'config root not found';

  if (exists) {
    try {
      installed = configPaths
        .filter(fileExists)
        .some(configPath => {
          const parsed = JSON.parse(fs.readFileSync(configPath, 'utf8'));
          return Boolean(parsed?.mcpServers?.[SERVER_NAME]);
        });
      reason = installed ? 'installed' : 'config parsed successfully';
    } catch (error) {
      supported = false;
      reason = `config parse failed: ${error.message}`;
    }
  }

  return {
    id: 'claude-desktop',
    name: 'Claude Desktop',
    kind: 'file',
    detected,
    supported,
    installed,
    reason,
    configPaths,
    path: configPaths.join(' | '),
    install: () => installClaudeDesktop(configPaths, url),
    uninstall: () => uninstallClaudeDesktop(configPaths)
  };
}

async function detectOpenClaw(url) {
  const openclawCli = runCommand('openclaw', ['--version']);
  const mcporterCli = runCommand('mcporter', ['--version']);
  const configPath = resolveMcporterHomeConfigPath();
  const detected = openclawCli.ok || fileExists(resolveOpenClawConfigPath());
  const supported = openclawCli.ok && (mcporterCli.ok || Boolean(resolveExecutablePath(process.platform === 'win32' ? 'npx.cmd' : 'npx')));

  let installed = false;
  let reason = !openclawCli.ok
    ? 'openclaw not detected'
    : mcporterCli.ok
      ? 'mcporter config helpers available'
      : supported
        ? 'mcporter will be used via npx'
        : 'mcporter runtime is unavailable';

  if (fileExists(configPath)) {
    try {
      const payload = JSON.parse(fs.readFileSync(configPath, 'utf8'));
      installed = Boolean(payload?.mcpServers?.[SERVER_NAME]);
    } catch {
      installed = false;
    }

    if (installed) {
      reason = 'installed';
    }
  }

  return {
    id: 'openclaw',
    name: 'OpenClaw',
    kind: 'cli',
    detected,
    supported,
    installed,
    reason,
    path: '~/.mcporter/mcporter.json (mcporter home config)',
    install: () => installOpenClaw(url),
    uninstall: () => uninstallOpenClaw()
  };
}

function printBanner(hosts, url) {
  stdout.write(`AiyoPerps MCP installer\nTarget MCP URL: ${url}\n`);
  printStatus(hosts, url);
}

function printStatus(hosts, url) {
  stdout.write(`\nDetected hosts for ${url}:\n`);
  hosts.forEach((host, index) => {
    const flags = [
      host.detected ? 'detected' : 'not detected',
      host.supported ? 'supported' : 'not safely writable',
      host.installed ? 'installed' : 'not installed'
    ].join(', ');

    stdout.write(
      `${index + 1}. ${host.name}: ${flags}\n   reason: ${host.reason}\n` +
      `${host.path ? `   path: ${host.path}\n` : ''}`);
  });
}

async function installAll(hosts, url) {
  const selected = hosts.filter(host => host.detected && host.supported);
  if (selected.length === 0) {
    stdout.write('No detected hosts are safe to modify.\n');
    return;
  }

  const resolvedUrl = await resolveInstallUrl(url, cli.urlExplicit);
  if (!resolvedUrl) {
    return;
  }

  bindInstallActions(selected, resolvedUrl);
  await prewarmBridgePackage(resolvedUrl);
  await runForHosts(selected, 'install');
}

async function installAnyOf(rl, hosts, url) {
  const eligible = hosts.filter(host => host.detected && host.supported);
  if (eligible.length === 0) {
    stdout.write('No detected hosts are safe to modify.\n');
    return;
  }

  stdout.write('\nSelectable hosts:\n');
  eligible.forEach((host, index) => {
    stdout.write(`${index + 1}. ${host.name}\n`);
  });

  const raw = (await rl.question('Enter host numbers (comma separated): ')).trim();
  const selected = parseSelections(raw, eligible);
  if (selected.length === 0) {
    stdout.write('Nothing selected.\n');
    return;
  }

  const resolvedUrl = await resolveInstallUrl(url, cli.urlExplicit);
  if (!resolvedUrl) {
    return;
  }

  bindInstallActions(selected, resolvedUrl);
  await prewarmBridgePackage(resolvedUrl);
  await runForHosts(selected, 'install');
}

async function uninstallAnyOf(rl, hosts) {
  const eligible = hosts.filter(host => host.detected && host.supported && host.installed);
  if (eligible.length === 0) {
    stdout.write('No installed hosts found for uninstall.\n');
    return;
  }

  stdout.write('\nInstalled hosts:\n');
  eligible.forEach((host, index) => {
    stdout.write(`${index + 1}. ${host.name}\n`);
  });

  const raw = (await rl.question('Enter host numbers to uninstall (comma separated, or "all"): ')).trim();
  const selected = raw.toLowerCase() === 'all'
    ? eligible
    : parseSelections(raw, eligible);

  if (selected.length === 0) {
    stdout.write('Nothing selected.\n');
    return;
  }

  await runForHosts(selected, 'uninstall');
}

async function runForHosts(hosts, action) {
  for (const host of hosts) {
    stdout.write(`\n${action} -> ${host.name}\n`);
    try {
      await host[action]();
      host.installed = action === 'install';
      host.reason = action === 'install' ? 'installed' : 'removed';
      stdout.write('  success\n');
    } catch (error) {
      stdout.write(`  failed: ${error.message}\n`);
    }
  }
}

async function runNonInteractive(cliArgs, hosts, url) {
  if (cliArgs.uninstall.length > 0) {
    const selected = selectHostsByIds(hosts, cliArgs.uninstall, {
      requireDetected: true,
      requireSupported: true,
      requireInstalled: false
    });
    await runForHosts(selected, 'uninstall');
    return;
  }

  if (cliArgs.installAll) {
    await installAll(hosts, url);
    return;
  }

  if (cliArgs.install.length > 0) {
    const selected = selectHostsByIds(hosts, cliArgs.install, {
      requireDetected: true,
      requireSupported: true,
      requireInstalled: false
    });
    const resolvedUrl = await resolveInstallUrl(url, cliArgs.urlExplicit);
    if (!resolvedUrl) {
      return;
    }

    bindInstallActions(selected, resolvedUrl);
    await prewarmBridgePackage(resolvedUrl);
    await runForHosts(selected, 'install');
    return;
  }

  printStatus(hosts, url);
}

function parseSelections(raw, hosts) {
  const indexes = new Set(
    raw
      .split(',')
      .map(value => Number.parseInt(value.trim(), 10))
      .filter(Number.isInteger)
      .filter(value => value >= 1 && value <= hosts.length));

  return [...indexes].map(index => hosts[index - 1]);
}

function selectHostsByIds(hosts, ids, requirements) {
  const wantAll = ids.includes('all');
  const selected = wantAll
    ? hosts
    : hosts.filter(host => ids.includes(host.id));

  return selected.filter(host => {
    if (requirements.requireDetected && !host.detected) {
      return false;
    }

    if (requirements.requireSupported && !host.supported) {
      return false;
    }

    if (requirements.requireInstalled && !host.installed) {
      return false;
    }

    return true;
  });
}

function bindInstallActions(hosts, url) {
  for (const host of hosts) {
    host.install = createInstallAction(host, url);
  }
}

function createInstallAction(host, url) {
  switch (host.id) {
    case 'codex':
      return () => installCodex(host.path, url);
    case 'claude-code':
      return () => installClaudeCode(url);
    case 'claude-desktop':
      return () => installClaudeDesktop(host.configPaths, url);
    case 'openclaw':
      return () => installOpenClaw(url);
    default:
      return async () => { };
  }
}

function installCodex(configPath, url) {
  ensureParentDir(configPath);

  const original = fileExists(configPath) ? fs.readFileSync(configPath, 'utf8') : '';
  if (original) {
    parseToml(original);
  }

  const block = buildCodexBlock(url);
  let next = original;

  if (hasCodexBlock(original)) {
    next = replaceCodexBlock(original, block);
  } else if (original.trim().length === 0) {
    next = `${block}\n`;
  } else {
    next = `${original.replace(/\s*$/, '')}\n\n${block}\n`;
  }

  parseToml(next);
  backupIfExists(configPath);
  fs.writeFileSync(configPath, next, 'utf8');
}

function uninstallCodex(configPath) {
  if (!fileExists(configPath)) {
    return;
  }

  const original = fs.readFileSync(configPath, 'utf8');
  if (!hasCodexBlock(original)) {
    return;
  }

  const next = removeCodexBlock(original);
  if (next.trim()) {
    parseToml(next);
  }

  backupIfExists(configPath);
  fs.writeFileSync(configPath, next.trim() ? `${next.replace(/\s*$/, '')}\n` : '', 'utf8');
}

function installClaudeDesktop(configPaths, url) {
  const targets = configPaths.filter(fileExists);
  if (targets.length === 0) {
    targets.push(configPaths[0]);
  }

  for (const configPath of targets) {
    ensureParentDir(configPath);

    const payload = fileExists(configPath)
      ? JSON.parse(fs.readFileSync(configPath, 'utf8'))
      : {};

    payload.mcpServers ??= {};
    payload.mcpServers[SERVER_NAME] = buildJsonServerConfig(url);

    backupIfExists(configPath);
    fs.writeFileSync(configPath, `${JSON.stringify(payload, null, 2)}\n`, 'utf8');
  }
}

function uninstallClaudeDesktop(configPaths) {
  for (const configPath of configPaths) {
    if (!fileExists(configPath)) {
      continue;
    }

    const payload = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    if (!payload?.mcpServers?.[SERVER_NAME]) {
      continue;
    }

    delete payload.mcpServers[SERVER_NAME];
    if (Object.keys(payload.mcpServers).length === 0) {
      delete payload.mcpServers;
    }

    backupIfExists(configPath);
    fs.writeFileSync(configPath, `${JSON.stringify(payload, null, 2)}\n`, 'utf8');
  }
}

function installClaudeCode(url) {
  const commandLine = resolveCommandLineSpec(process.platform, url);
  const spec = JSON.stringify({
    type: 'stdio',
    command: commandLine.command,
    args: commandLine.args
  });

  const command = ['mcp', 'add-json', SERVER_NAME, spec, '--scope', 'user'];
  const first = runCommand('claude', command);
  if (first.ok) {
    return;
  }

  const fallback = runCommand('claude', ['mcp', 'add-json', '--scope', 'user', SERVER_NAME, spec]);
  if (!fallback.ok) {
    throw new Error(first.stderr || fallback.stderr || 'claude mcp add-json failed');
  }
}

function uninstallClaudeCode() {
  const first = runCommand('claude', ['mcp', 'remove', SERVER_NAME, '--scope', 'user']);
  if (first.ok) {
    return;
  }

  const fallback = runCommand('claude', ['mcp', 'remove', '--scope', 'user', SERVER_NAME]);
  if (!fallback.ok) {
    throw new Error(first.stderr || fallback.stderr || 'claude mcp remove failed');
  }
}

function installOpenClaw(url) {
  const result = runMcporterCommand([
    'config',
    'add',
    SERVER_NAME,
    url,
    '--scope',
    'home'
  ]);
  if (!result.ok) {
    throw new Error(result.stderr || 'mcporter config add failed');
  }
}

function uninstallOpenClaw() {
  if (!fileExists(resolveMcporterHomeConfigPath())) {
    return;
  }

  const result = runMcporterCommand([
    '--config',
    resolveMcporterHomeConfigPath(),
    'config',
    'remove',
    SERVER_NAME
  ]);
  if (result.stderr && /does not exist/i.test(result.stderr)) {
    return;
  }

  if (!result.ok) {
    throw new Error(result.stderr || 'mcporter config remove failed');
  }
}

function buildCodexBlock(url) {
  const commandLine = resolveCommandLineSpec(process.platform, url);
  const args = JSON.stringify(commandLine.args, null, 0);
  return [
    `[mcp_servers.${SERVER_NAME}]`,
    `startup_timeout_sec = 60`,
    `command = ${JSON.stringify(commandLine.command)}`,
    `args = ${args}`
  ].join('\n');
}

function buildJsonServerConfig(url) {
  const commandLine = resolveCommandLineSpec(process.platform, url);
  return {
    command: commandLine.command,
    args: commandLine.args
  };
}

async function prewarmBridgePackage(url) {
  stdout.write('\nPreparing bridge runtime...\n');
  await ensureBridgeRuntimePrepared();
  if (bridgeRuntimePrepared) {
    stdout.write(`  ready: ${resolveInstalledBridgeScriptPath()}\n`);
    return;
  }

  stdout.write(`  warning: ${bridgeRuntimeWarning || 'falling back to npx'}\n`);
}

async function resolveInstallUrl(defaultUrl, urlExplicit) {
  await ensureBridgeRuntimePrepared();
  const candidates = urlExplicit
    ? [defaultUrl]
    : buildCandidateUrls(defaultUrl);

  stdout.write('\nProbing MCP endpoint candidates...\n');
  for (const candidate of candidates) {
    const result = await probeMcpEndpoint(candidate);
    stdout.write(`  ${result.ok ? 'ok' : 'fail'} ${candidate}`);
    if (!result.ok) {
      const detail = result.stderr || result.stdout || result.error?.message || `exit=${result.status ?? 'unknown'}`;
      stdout.write(` (${truncateText(detail, 180)})`);
    }
    stdout.write('\n');
    if (result.ok) {
      return candidate;
    }
  }

  stdout.write(
    'No reachable AiyoPerps MCP endpoint was detected. ' +
    'Start the AiyoPerps HTTP API first or pass a valid --url, then retry.\n');
  return null;
}

function buildCandidateUrls(defaultUrl) {
  const urls = [
    defaultUrl,
    'http://localhost:5078/mcp',
    'http://winhost:5078/mcp',
    'http://host.docker.internal:5078/mcp'
  ];

  const wslGatewayUrl = detectWslGatewayUrl();
  if (wslGatewayUrl) {
    urls.push(wslGatewayUrl);
  }

  return [...new Set(urls)];
}

function detectWslGatewayUrl() {
  if (!isWsl()) {
    return null;
  }

  try {
    const content = fs.readFileSync('/etc/resolv.conf', 'utf8');
    const match = content.match(/^nameserver\s+([0-9.]+)\s*$/m);
    return match ? `http://${match[1]}:5078/mcp` : null;
  } catch {
    return null;
  }
}

async function canReachMcpEndpoint(url) {
  const commandLine = resolveCommandLineSpec(process.platform, url, {
    healthCheck: true
  });
  return runCommand(commandLine.command, commandLine.args);
}

async function probeMcpEndpoint(url) {
  const httpResult = await probeMcpEndpointHttp(url);
  if (!httpResult.ok) {
    return httpResult;
  }

  if (!bridgeRuntimePrepared && bridgeRuntimeAttempted) {
    return httpResult;
  }

  const bridgeResult = await canReachMcpEndpoint(url);
  if (!bridgeResult.ok) {
    stdout.write(`  note bridge health check failed but HTTP MCP is reachable; continuing with ${url}\n`);
  }

  return httpResult;
}

async function probeMcpEndpointHttp(url) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 5000);

  try {
    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        jsonrpc: '2.0',
        id: 'installer-probe',
        method: 'ping',
        params: {}
      }),
      signal: controller.signal
    });

    const text = await response.text();
    if (!response.ok) {
      return {
        ok: false,
        status: response.status,
        stdout: '',
        stderr: `HTTP ${response.status}: ${text || response.statusText}`
      };
    }

    let payload;
    try {
      payload = JSON.parse(text);
    } catch (error) {
      return {
        ok: false,
        status: response.status,
        stdout: '',
        stderr: `Invalid JSON: ${error.message}`
      };
    }

    if (payload?.error) {
      return {
        ok: false,
        status: response.status,
        stdout: '',
        stderr: payload.error.message || 'JSON-RPC error'
      };
    }

    return {
      ok: true,
      status: response.status,
      stdout: '',
      stderr: ''
    };
  } catch (error) {
    const message = error?.name === 'AbortError'
      ? 'Request timeout after 5000ms'
      : error?.message || 'Unknown fetch error';
    return {
      ok: false,
      status: null,
      stdout: '',
      stderr: message,
      error
    };
  } finally {
    clearTimeout(timeout);
  }
}

function hasCodexBlock(content) {
  return new RegExp(`^\\[mcp_servers\\.${SERVER_NAME}\\]\\s*$`, 'm').test(content);
}

function replaceCodexBlock(content, block) {
  const range = findCodexBlockRange(content);
  if (!range) {
    throw new Error('Existing Codex MCP block could not be safely replaced.');
  }

  const prefix = content.slice(0, range.start).replace(/\s*$/, '');
  const suffix = content.slice(range.end).replace(/^\s*/, '');
  return [prefix, block, suffix].filter(Boolean).join('\n\n');
}

function removeCodexBlock(content) {
  const range = findCodexBlockRange(content);
  if (!range) {
    return content;
  }

  const prefix = content.slice(0, range.start).replace(/\s*$/, '');
  const suffix = content.slice(range.end).replace(/^\s*/, '');
  return [prefix, suffix].filter(Boolean).join('\n\n');
}

function findCodexBlockRange(content) {
  const headerRegex = new RegExp(`^\\[mcp_servers\\.${SERVER_NAME}\\]\\s*$`, 'm');
  const match = headerRegex.exec(content);
  if (!match) {
    return null;
  }

  const start = match.index;
  const afterHeader = start + match[0].length;
  const rest = content.slice(afterHeader);
  const nextHeaderMatch = /^\\[[^\\]]+\\]\\s*$/m.exec(rest);
  const end = nextHeaderMatch ? afterHeader + nextHeaderMatch.index : content.length;
  return { start, end };
}

function resolveTargetUrl(args) {
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg.startsWith('--url=')) {
      return arg.slice('--url='.length);
    }

    if (arg === '--url' && index + 1 < args.length) {
      return args[index + 1];
    }
  }

  return env.AIYOPERPS_MCP_URL || DEFAULT_URL;
}

function isWsl() {
  return Boolean(env.WSL_DISTRO_NAME || env.WSL_INTEROP);
}

function parseCliArgs(args) {
  const install = [];
  const uninstall = [];
  let installAll = false;
  let statusOnly = false;
  let nonInteractive = false;
  let url = env.AIYOPERPS_MCP_URL || DEFAULT_URL;
  let urlExplicit = Boolean(env.AIYOPERPS_MCP_URL);

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    if (arg === '--yes') {
      installAll = true;
      nonInteractive = true;
      continue;
    }

    if (arg === '--status') {
      statusOnly = true;
      nonInteractive = true;
      continue;
    }

    if (arg === '--install-all') {
      installAll = true;
      nonInteractive = true;
      continue;
    }

    if (arg.startsWith('--install=')) {
      install.push(...splitCsvArg(arg.slice('--install='.length)));
      nonInteractive = true;
      continue;
    }

    if (arg === '--install' && index + 1 < args.length) {
      install.push(...splitCsvArg(args[index + 1]));
      nonInteractive = true;
      index += 1;
      continue;
    }

    if (arg.startsWith('--uninstall=')) {
      uninstall.push(...splitCsvArg(arg.slice('--uninstall='.length)));
      nonInteractive = true;
      continue;
    }

    if (arg === '--uninstall' && index + 1 < args.length) {
      uninstall.push(...splitCsvArg(args[index + 1]));
      nonInteractive = true;
      index += 1;
      continue;
    }

    if (arg.startsWith('--url=')) {
      url = arg.slice('--url='.length);
      urlExplicit = true;
      continue;
    }

    if (arg === '--url' && index + 1 < args.length) {
      url = args[index + 1];
      urlExplicit = true;
      index += 1;
      continue;
    }
  }

  return {
    install,
    uninstall,
    installAll,
    statusOnly,
    nonInteractive,
    url,
    urlExplicit
  };
}

function splitCsvArg(value) {
  return value
    .split(',')
    .map(item => item.trim())
    .filter(Boolean);
}

function resolveCommandLineSpec(platform, url, options = {}) {
  const bridgeArgs = [];
  if (options.prewarm) {
    bridgeArgs.push('--help');
  } else if (options.healthCheck) {
    bridgeArgs.push('--health-check', '--quiet', '--url', url);
  } else {
    bridgeArgs.push('--debug-log', resolveBridgeDebugLogPath(), '--quiet', '--url', url);
  }

  const installedScriptPath = resolveInstalledBridgeScriptPath();
  if (installedScriptPath) {
    return {
      command: process.execPath,
      args: [installedScriptPath, ...bridgeArgs]
    };
  }

  const packageArgs = ['-y', BRIDGE_PACKAGE_NAME, ...bridgeArgs];
  const resolvedNpx = resolveExecutablePath(platform === 'win32' ? 'npx.cmd' : 'npx');
  if (platform === 'win32') {
    return {
      command: resolvedNpx || 'npx.cmd',
      args: packageArgs
    };
  }

  return {
    command: resolvedNpx || 'npx',
    args: packageArgs
  };
}

function truncateText(value, maxLength) {
  if (!value) {
    return '';
  }

  return value.length <= maxLength
    ? value
    : `${value.slice(0, maxLength)}...`;
}

async function ensureBridgeRuntimePrepared() {
  if (bridgeRuntimeAttempted) {
    return bridgeRuntimePrepared;
  }

  bridgeRuntimeAttempted = true;

  if (resolveInstalledBridgeScriptPath()) {
    bridgeRuntimePrepared = true;
    return true;
  }

  const npmCommand = resolveExecutablePath(process.platform === 'win32' ? 'npm.cmd' : 'npm');
  if (!npmCommand) {
    bridgeRuntimeWarning = 'npm was not found in PATH, falling back to npx.';
    return false;
  }

  const installRoot = resolveBridgeInstallRoot();
  ensureParentDir(path.join(installRoot, 'placeholder'));

  const result = runCommand(npmCommand, [
    'install',
    '--silent',
    '--no-audit',
    '--no-fund',
    '--prefix',
    installRoot,
    BRIDGE_PACKAGE_NAME
  ]);

  if (!result.ok) {
    bridgeRuntimeWarning = result.stderr || result.error?.message || 'npm install failed, falling back to npx.';
    return false;
  }

  if (!resolveInstalledBridgeScriptPath()) {
    bridgeRuntimeWarning = 'bridge package installed, but launcher script was not found; falling back to npx.';
    return false;
  }

  bridgeRuntimePrepared = true;
  return true;
}

function resolveBridgeInstallRoot() {
  if (env.AIYOPERPS_MCP_BRIDGE_DIR) {
    return env.AIYOPERPS_MCP_BRIDGE_DIR;
  }

  if (process.platform === 'win32') {
    const appData = env.LOCALAPPDATA || path.join(env.USERPROFILE || os.homedir(), 'AppData', 'Local');
    return path.join(appData, 'AiyoPerps', 'mcp-bridge');
  }

  return path.join(os.homedir(), '.aiyoperps', 'mcp-bridge');
}

function resolveInstalledBridgeScriptPath() {
  const scriptPath = path.join(resolveBridgeInstallRoot(), BRIDGE_SCRIPT_RELATIVE_PATH);
  return fileExists(scriptPath) ? scriptPath : null;
}

function resolveBridgeDebugLogPath() {
  return path.join(resolveBridgeInstallRoot(), BRIDGE_DEBUG_LOG_NAME);
}

function resolveCodexConfigPath() {
  if (env.CODEX_CONFIG_PATH) {
    return env.CODEX_CONFIG_PATH;
  }

  if (process.platform === 'win32') {
    const home = env.USERPROFILE || os.homedir();
    return path.join(home, '.codex', 'config.toml');
  }

  return path.join(os.homedir(), '.codex', 'config.toml');
}

function getClaudeDesktopCandidates() {
  if (env.CLAUDE_DESKTOP_CONFIG_PATH) {
    return [env.CLAUDE_DESKTOP_CONFIG_PATH];
  }

  if (process.platform === 'win32') {
    const appData = env.APPDATA || path.join(env.USERPROFILE || os.homedir(), 'AppData', 'Roaming');
    const localAppData = env.LOCALAPPDATA || path.join(env.USERPROFILE || os.homedir(), 'AppData', 'Local');
    const storeCandidates = [];
    const roamingCandidates = [
      path.join(appData, 'Claude', 'claude_desktop_config.json'),
      path.join(appData, 'Claude', 'config.json')
    ];

    const packagesRoot = path.join(localAppData, 'Packages');
    if (fs.existsSync(packagesRoot)) {
      try {
        const packageDirs = fs.readdirSync(packagesRoot, { withFileTypes: true })
          .filter(entry => entry.isDirectory())
          .map(entry => entry.name)
          .filter(name => name.toLowerCase().startsWith('claude_'));

        for (const packageDir of packageDirs) {
          storeCandidates.push(
            path.join(packagesRoot, packageDir, 'LocalCache', 'Roaming', 'Claude', 'claude_desktop_config.json'),
            path.join(packagesRoot, packageDir, 'LocalCache', 'Roaming', 'Claude', 'config.json'));
        }
      } catch {
        // Ignore directory enumeration failures and keep default candidates.
      }
    }

    return [...new Set([...storeCandidates, ...roamingCandidates])];
  }

  if (process.platform === 'darwin') {
    return [
      path.join(os.homedir(), 'Library', 'Application Support', 'Claude', 'claude_desktop_config.json'),
      path.join(os.homedir(), 'Library', 'Application Support', 'Claude', 'config.json')
    ];
  }

  return [
    path.join(os.homedir(), '.config', 'Claude', 'claude_desktop_config.json'),
    path.join(os.homedir(), '.config', 'Claude', 'config.json')
  ];
}

function selectClaudeDesktopConfigPaths(candidates) {
  const existingPrimary = candidates.filter(candidate =>
    fileExists(candidate) &&
    path.basename(candidate).toLowerCase() === 'claude_desktop_config.json');
  if (existingPrimary.length > 0) {
    return existingPrimary;
  }

  const existingAny = candidates.filter(fileExists);
  if (existingAny.length > 0) {
    return existingAny;
  }

  const creatablePrimary = candidates.find(candidate =>
    path.basename(candidate).toLowerCase() === 'claude_desktop_config.json' &&
    fs.existsSync(path.dirname(candidate)));
  if (creatablePrimary) {
    return [creatablePrimary];
  }

  const creatableAny = candidates.find(candidate => fs.existsSync(path.dirname(candidate)));
  if (creatableAny) {
    return [creatableAny];
  }

  return [candidates[0]];
}

function resolveOpenClawConfigPath() {
  const explicit = env.OPENCLAW_CONFIG_PATH;
  if (explicit) {
    return explicit;
  }

  const stateDir = env.OPENCLAW_STATE_DIR || path.join(os.homedir(), '.openclaw');
  return path.join(stateDir, 'openclaw.json');
}

function resolveMcporterHomeConfigPath() {
  return path.join(os.homedir(), '.mcporter', 'mcporter.json');
}

function runMcporterCommand(args) {
  const mcporter = resolveExecutablePath(process.platform === 'win32' ? 'mcporter.cmd' : 'mcporter');
  if (mcporter) {
    return runCommand(mcporter, args);
  }

  const npx = resolveExecutablePath(process.platform === 'win32' ? 'npx.cmd' : 'npx');
  if (!npx) {
    return {
      ok: false,
      status: null,
      stdout: '',
      stderr: 'mcporter and npx were not found in PATH',
      error: null
    };
  }

  return runCommand(npx, ['-y', 'mcporter', ...args]);
}

function runCommand(command, args) {
  const result = spawnSync(command, args, {
    encoding: 'utf8',
    cwd: cwd()
  });

  return {
    ok: result.status === 0,
    status: result.status,
    stdout: (result.stdout || '').trim(),
    stderr: (result.stderr || '').trim(),
    error: result.error
  };
}

function resolveExecutablePath(name) {
  if (EXECUTABLE_CACHE.has(name)) {
    return EXECUTABLE_CACHE.get(name);
  }

  const pathValue = env.PATH || '';
  const separator = process.platform === 'win32' ? ';' : ':';
  const extensions = process.platform === 'win32'
    ? ['', '.cmd', '.exe', '.bat']
    : [''];

  for (const directory of pathValue.split(separator)) {
    if (!directory) {
      continue;
    }

    for (const extension of extensions) {
      const candidate = path.join(directory, name.endsWith(extension) ? name : `${name}${extension}`);
      if (fileExists(candidate)) {
        EXECUTABLE_CACHE.set(name, candidate);
        return candidate;
      }
    }
  }

  EXECUTABLE_CACHE.set(name, null);
  return null;
}

function ensureParentDir(filePath) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
}

function backupIfExists(filePath) {
  if (!fileExists(filePath)) {
    return;
  }

  fs.copyFileSync(filePath, `${filePath}.bak`);
}

function fileExists(filePath) {
  try {
    return fs.existsSync(filePath) && fs.statSync(filePath).isFile();
  } catch {
    return false;
  }
}
