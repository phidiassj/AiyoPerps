# AiyoPerps Linux x64

This package ships the Linux x64 desktop build of AiyoPerps through npm.

## Install

```bash
npm install -g @phidiassj/aiyoperps-linux64
```

## Run

```bash
aiyoperps-linux64
```

You can pass the same arguments supported by the native binary:

```bash
aiyoperps-linux64 -- headless --port 5078
```

## Add a desktop launcher

```bash
aiyoperps-linux64-install-desktop
```

This creates a user-level `.desktop` entry under `~/.local/share/applications/aiyoperps.desktop` and points it to the installed package location.

## Notes

- Platform: Linux x64 only
- Node.js: 18+
- The packaged executable is stored under `app/AiyoPerps`
