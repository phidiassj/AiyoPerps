#!/usr/bin/env node

const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");

if (process.platform !== "linux") {
  console.error("This package only supports Linux.");
  process.exit(1);
}

if (process.arch !== "x64") {
  console.error("This package only supports x64 Linux.");
  process.exit(1);
}

const executablePath = path.resolve(__dirname, "..", "app", "AiyoPerps");

if (!fs.existsSync(executablePath)) {
  console.error(`AiyoPerps executable not found: ${executablePath}`);
  process.exit(1);
}

const child = spawn(executablePath, process.argv.slice(2), {
  stdio: "inherit"
});

child.on("exit", (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }

  process.exit(code ?? 0);
});

child.on("error", (error) => {
  console.error(error.message);
  process.exit(1);
});
