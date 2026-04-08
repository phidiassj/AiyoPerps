#!/usr/bin/env node

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

if (process.platform !== "linux") {
  console.error("Desktop entry installation is only supported on Linux.");
  process.exit(1);
}

const packageRoot = path.resolve(__dirname, "..");
const executablePath = path.join(packageRoot, "app", "AiyoPerps");
const iconPath = path.join(packageRoot, "app", "Assets", "logo.png");
const applicationsDir = path.join(os.homedir(), ".local", "share", "applications");
const desktopFilePath = path.join(applicationsDir, "aiyoperps.desktop");

if (!fs.existsSync(executablePath)) {
  console.error(`AiyoPerps executable not found: ${executablePath}`);
  process.exit(1);
}

fs.mkdirSync(applicationsDir, { recursive: true });

const desktopEntry = [
  "[Desktop Entry]",
  "Type=Application",
  "Version=1.0",
  "Name=AiyoPerps",
  "Comment=Perpetual futures desktop terminal",
  `Exec=${executablePath}`,
  `Icon=${iconPath}`,
  "Terminal=false",
  "Categories=Office;Finance;",
  "StartupNotify=true"
].join("\n");

fs.writeFileSync(desktopFilePath, `${desktopEntry}\n`, "utf8");
console.log(`Desktop entry written to ${desktopFilePath}`);
