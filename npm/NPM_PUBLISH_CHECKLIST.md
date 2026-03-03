# NPM Publish Checklist

Use this checklist before publishing `@phidiassj/aiyoperps-mcp-bridge`.

## 1. Package Validation
- Confirm package files exist:
  - `npm/aiyoperps-mcp-bridge/package.json`
  - `npm/aiyoperps-mcp-bridge/bin/aiyoperps-mcp-bridge.js`
  - `npm/aiyoperps-mcp-bridge/README.md`
- Verify syntax:
  - `node --check npm/aiyoperps-mcp-installer/bin/aiyoperps-mcp-installer.js`
- Verify local launch:
  - `npx -y ./npm/aiyoperps-mcp-bridge --url http://127.0.0.1:5078/mcp`

## 2. Metadata Review
- Set final `name` in `package.json`
- Set final `version`
- Confirm `license`, `description`, and `keywords`
- Confirm `engines.node` matches your support target

## 3. NPM Account
- Login:
  - `npm login`
- Confirm publishing identity:
  - `npm whoami`

## 4. Dry Run
- From `npm/aiyoperps-mcp-installer/` run:
  - `npm pack`
- Inspect the generated tarball contents

## 5. Publish
- Public scoped package:
  - `npm publish --access public`

## 6. Post-Publish Validation
- Test install:
  - `npx -y @phidiassj/aiyoperps-mcp-installer --url http://127.0.0.1:5078/mcp`
- Update `mcp-config.json` examples if package name/version strategy changed
- Add the npm command to release notes and GitHub README
