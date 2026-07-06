# Windows 11 Validation

OneLag has two Windows validation paths:

- Hosted GitHub Actions on `windows-latest` validates Windows build, publish, CLI smoke, GUI smoke, watch/report flow, support-bundle export, and remediation move/rollback flow. This is useful but may run on Windows Server rather than a Windows 11 laptop.
- A self-hosted runner labeled `Windows11` validates the same workflow on a real Windows 11 machine and can be configured to fail if the runner is not Windows 11.

Run the manual workflow:

```text
Actions -> windows-11-validation -> Run workflow
```

For a real laptop validation, register a self-hosted runner with labels:

```text
self-hosted, Windows, X64, Windows11
```

Then run the workflow with:

- `require_windows_11`: `true`
- `run_self_hosted_windows11`: `true`

The validation script performs only local temporary-file operations:

- Builds and tests the solution.
- Publishes `onelag.exe` and `onelag-gui.exe`.
- Runs `onelag-gui.exe --smoke`.
- Runs a redacted temp-root scan and report view.
- Runs bounded watch start, mark, report, and timeline view.
- Runs support-bundle export and verifies the offline analysis prompt and ZIP.
- Generates a move plan.
- Runs remediation move dry-run, explicit move execution, verify, and rollback against temporary folders.
- Exercises the normal lightweight evidence path, including recent Windows event summaries and safe OneDrive metadata where available.

It does not use a real OneDrive folder unless the caller explicitly changes the script.
