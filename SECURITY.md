# Security policy

## Scope

`cluster-map-downloader` is a research tool for downloading **publicly
accessible** cluster world metadata and assets.

In scope:

- Bugs in this repository's code (metadata parsing, download pipeline,
  scripts, tests).
- Accidental leakage of anything the `public repository rules` forbid
  (credentials, tokens, cookies, temporary signed URLs, third-party
  binaries, copyrighted assets) — for example via a commit that slipped
  past `.gitignore`.

Out of scope:

- Requests to implement authentication- or access-control bypass features.
  This project explicitly does **not** implement, discuss in actionable
  detail, or assist with bypassing Cluster, Inc. security controls.
- The security of Cluster's own infrastructure and games.

## Reporting

Open a GitHub issue (for non-sensitive findings) or, for anything sensitive
in this repository, open an issue describing only that sensitive material
exists and it will be removed promptly. There is no paid bounty program;
this is a volunteer-maintained project.

## Security notes for contributors

- Never commit tokens, cookies, credentials, or temporary signed URLs.
- Local research artifacts belong in `.work/`, `downloads/`, `cache/`,
  `output/`, or `artifacts-local/` — all of which are git-ignored.
- `tests/` must not depend on downloading or redistributing a specific
  copyrighted world; use the synthetic fixtures in `samples/`.
