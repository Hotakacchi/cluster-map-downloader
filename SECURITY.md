# Security policy

## Scope

`cluster-map-downloader` is a research tool for downloading **publicly
accessible** cluster world metadata and assets.

In scope:

- Bugs in this repository's code (metadata parsing, guest-entry flow,
  download pipeline, scripts, tests), including secret-handling defects
  (a key/token printed, logged, or persisted by the tool itself).
- Accidental leakage of anything the `public repository rules` forbid
  (credentials, tokens, cookies, temporary signed URLs, third-party
  binaries, copyrighted assets) — for example via a commit that slipped
  past `.gitignore`.

Out of scope:

- Requests to implement authentication- or access-control bypass features
  (key extraction, DRM circumvention, credential theft, enumeration). The
  opt-in `--guest-entry` mode is deliberately restricted to the anonymous
  guest chain a fresh free client performs; anything beyond that boundary
  (accounts, tickets, invitations, encrypted-bundle decryption) is refused
  or left encrypted by design, not bypassed.
- The security of Cluster's own infrastructure and games.

## Reporting

Open a GitHub issue (for non-sensitive findings) or, for anything sensitive
in this repository, open an issue describing only that sensitive material
exists and it will be removed promptly. There is no paid bounty program;
this is a volunteer-maintained project.

## Security notes for contributors

- Never commit tokens, cookies, credentials, or temporary signed URLs —
  including "demo" values shaped like real ones in tests (fixtures use
  synthetic AIza-shaped keys only).
- The guest-entry flow receives the Firebase web API key via the CLI/GUI or
  by scanning the *user's* local artifacts (the GUI auto-discovers a locally
  installed client package through `GuestEntryResolverFactory` — user-local
  scanning only, verified candidates, nothing vendored); keep it that way
  (no vendoring), route every error message through `SecretMasker`, and keep
  Firebase credentials in memory only (`IDisposable` clients, no
  serialization).
- Local research artifacts belong in `.work/`, `downloads/`, `cache/`,
  `output/`, or `artifacts-local/` — all of which are git-ignored.
- `tests/` must not depend on downloading or redistributing a specific
  copyrighted world; use the synthetic fixtures in `samples/` and stub HTTP
  handlers (no live requests in CI).
