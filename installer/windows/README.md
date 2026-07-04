# OneLag Windows Installer Bundle

This bundle contains a self-contained Windows x64 build of `onelag.exe`.

Install for the current user:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\Install-OneLag.ps1
```

Open a new terminal, then run:

```powershell
onelag version
onelag scan --output onelag-report.md
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

Execute reset only after reviewing the dry run and confirming your work policy:

```powershell
onelag repair reset-onedrive --execute --i-understand-reset-disconnects-sync
```

Uninstall:

```powershell
.\Uninstall-OneLag.ps1
```
