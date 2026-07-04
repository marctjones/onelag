# OneLag Sample Reports

These reports are synthetic, redacted examples for documentation, tests, issue triage, and UI design. They do not contain real account names, tenant names, document titles, or machine identifiers.

View them locally:

```bash
dotnet run --project src/OneLag.Cli -- view --report samples/diagnostic-report-sample.md
dotnet run --project src/OneLag.Cli -- view --report samples/watch-report-sample.md --timeline
```

Use these samples when designing tray or GUI views so the interface can handle both one-shot scan findings and watch-mode lag episodes.
