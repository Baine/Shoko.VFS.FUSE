# Security Policy

## Supported versions

| Branch  | Supported           |
|---------|---------------------|
| `main`  | Yes                 |
| older   | No                  |

Only the latest release on `main` receives security fixes. Please update
before reporting.

## Reporting a vulnerability

**Do not open a public GitHub issue for security bugs.**

Email the maintainer at the address in their GitHub profile, or use GitHub's
["Report a vulnerability"](https://github.com/Baine/Shoko.VFS.FUSE/security/advisories/new)
feature on this repository. Please include:

- A clear description of the issue and its impact.
- Reproduction steps (preferably with a minimal Shoko Server configuration).
- Logs / stack traces, redacted of any secrets.

You should receive an acknowledgement within 7 days. A fix (or a documented
decision not to fix) typically follows within 30 days, depending on severity.

## Out-of-scope

- Vulnerabilities in Shoko Server itself. Report those to the
  [Shoko Server](https://github.com/ShokoAnime/ShokoServer) project.
- Vulnerabilities in libfuse3 / the Linux kernel. Report upstream.
- Denial-of-service via malformed Shoko Server responses — the daemon treats
  Shoko Server as trusted infrastructure; if you need to harden against a
  malicious server, deploy it on a private network.
- Self-inflicted issues from leaking your `SHOKO_API_KEY` to a public
  network — that's an operational mistake, not a vulnerability in this code.

## Secrets

Never commit credentials. The repository's `.gitignore` excludes `.env`,
`.env.*`, and `*.local.json` for that reason. If you accidentally commit a
secret, rotate it immediately and use `git filter-repo` to rewrite history
before pushing.
