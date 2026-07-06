# OneLag Windows Installer Bundle

This bundle contains self-contained Windows x64 builds of `onelag.exe` and `onelag-gui.exe`.

Install for the current user:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Install-OneLag.ps1
```

Open a new terminal, then run:

```powershell
onelag version
onelag scan --output onelag-report.md
onelag-gui
```

Start a bounded foreground responsiveness recorder:

```powershell
onelag watch start --duration 8h
```

Mark that lag is happening:

```powershell
onelag watch mark
```

Review the Microsoft-supported OneDrive reset plan without changing anything:

```powershell
onelag repair reset-onedrive
```

Package reports for offline Codex or Claude Code analysis:

```powershell
onelag support bundle --report onelag-report.md --report onelag-watch-report.md --output onelag-support-bundle --zip
```

Execute reset only after reviewing the dry run and confirming your work policy:

```powershell
onelag repair reset-onedrive --execute --i-understand-reset-disconnects-sync
```

Generate and execute a reviewed move plan only after pausing OneDrive and confirming the destination:

```powershell
onelag remediate move-plan --source "$env:USERPROFILE\OneDrive\project" --destination "C:\LocalDev\project"
onelag remediate move --source "$env:USERPROFILE\OneDrive\project" --destination "C:\LocalDev\project" --execute --i-understand-moves-files
onelag remediate verify --source "$env:USERPROFILE\OneDrive\project" --destination "C:\LocalDev\project"
```

Uninstall:

```powershell
.\Uninstall-OneLag.ps1
```
