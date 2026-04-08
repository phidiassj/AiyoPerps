#!/usr/bin/env node

const fs = require("node:fs");
const path = require("node:path");

if (process.platform !== "linux") {
  process.exit(0);
}

const filesToChmod = [
  path.resolve(__dirname, "..", "app", "AiyoPerps"),
  path.resolve(__dirname, "..", "bin", "aiyoperps-linux64.js"),
  path.resolve(__dirname, "..", "bin", "install-desktop-entry.js")
];

for (const filePath of filesToChmod) {
  if (!fs.existsSync(filePath)) {
    continue;
  }

  fs.chmodSync(filePath, 0o755);
}
