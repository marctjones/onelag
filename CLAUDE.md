# Working in this repo

## Development workflow — commit straight to `main`

This is a solo project developed locally. GitHub exists for backup and releases, not
collaboration. **Do not open pull requests. Do not create feature branches.** Commit
directly to `main` and push.

```bash
git add -A
git commit -m "what changed"
git push                 # → GitHub backup + CI runs
```

`main` is intentionally **not** branch-protected. Direct pushes are the expected path.

## CI is a signal, not a gate

The `ci.yml` workflow runs the test suite on macOS and Windows on every push to `main`.
It reports red/green on the commit but does **not** block the push — in a direct-commit
workflow CI cannot gate a merge, because there is no merge. If a push breaks a test
(usually a Windows-only one that can't run on this macOS dev box), fix it forward with
another commit. Don't reintroduce PRs or protection to "gate" it.

## Releases — tag to build the installer

The `release.yml` workflow builds the signed `win-x64-installer.zip` when a version tag
is pushed. To cut a release:

```bash
git tag -a v0.1.0-preview.N -m "what's new"
git push origin v0.1.0-preview.N
```

The installer and its `.sha256` appear on the GitHub Releases page a few minutes later.

## Testing

`dotnet test` runs everything. The 8 `WindowsProbeIntegrationTests` / `WindowsLogCollectorTests`
that touch real Windows APIs are skipped on macOS — that's expected, not a failure. Keep
the pure-parser / thin-shell-out split so probe logic stays testable here via fixtures.
