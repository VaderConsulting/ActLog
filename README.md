# ActLog

VB.NET suite that tracks user logon and logoff events. Broker and LogBroker services collect activity; ActivityLogClient is the client front end. Backup trees of earlier project versions sit alongside the live projects.

**Source last updated:** 2009-01-17 · **Language:** VB.NET · **Target:** .NET Framework (VS 2005/2008 era) · **Output:** WinExe / libraries

## Solution structure

| Project | Language | Type | Purpose |
|---------|----------|------|---------|
| `Broker` (`Broker/Broker.vbproj`) | VB.NET | Library/service | Logon/logoff event broker |
| `LogBroker` (`LogBroker/LogBroker.vbproj`) | VB.NET | Library/service | Logging broker companion |
| `ActivityLogClient` (`ActivityLogClient/ActivityLogClient.vbproj`) | VB.NET | WinExe | Client UI for activity log |
| `Test` (`Test/Test.vbproj`) | VB.NET | WinExe | Test harness |

## How to open

Open `ActLog.sln` in Visual Studio 2005 or 2008.

## Requirements

- Visual Studio 2005 to 2008

## Attribution and provenance

Working copy from my Historical Dev folder `ActLog`.
Assembly company: Empired Limited. Assembly copyright: Copyright © 2007.

## License

MIT © 2026 Vader Consulting for Dave Robinson's code. See `LICENSE`.
