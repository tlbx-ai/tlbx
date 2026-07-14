# Contributing to MidTerm

Contributions are welcome! This document explains how to contribute to MidTerm.

## Contributor License Agreement (CLA)

**All contributions require CLA acceptance.**

By submitting a pull request, you agree to the terms of our [Contributor License Agreement](CLA.md). This grants the project owner rights to relicense your contribution, which enables dual-licensing (open source under AGPL-3.0, plus commercial licenses for organizations that need them).

No separate signature is required — submitting a PR implies acceptance.

## How to Contribute

### Reporting Issues

- Search existing issues first to avoid duplicates
- Include reproduction steps, expected behavior, and actual behavior
- Include OS, browser, and MidTerm version
- Report vulnerabilities through the private process in [SECURITY.md](../SECURITY.md), not a public issue

### Pull Requests

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/your-feature`)
3. Make your changes
4. Ensure the build passes: `dotnet build src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj`
5. Run tests: `dotnet test src/Ai.Tlbx.MidTerm.Tests/Ai.Tlbx.MidTerm.Tests.csproj`
6. Submit a pull request

### Code Style

**C#:**
- Allman brace style (opening brace on new line)
- 4 spaces indentation
- `_camelCase` for private fields
- Explicit access modifiers
- Minimal comments (only for complex logic)

**TypeScript:**
- K&R brace style (opening brace on same line)
- 2 spaces indentation
- Single quotes for strings
- Semicolons required

See [CLAUDE.md](ai/CLAUDE.md) for detailed style guidelines.

## Development Setup

**Prerequisites:**
- .NET 10 SDK
- esbuild (for TypeScript bundling)

```bash
git clone https://github.com/tlbx-ai/tlbx.git
cd MidTerm
dotnet build src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj
```

## Questions?

Open a GitHub issue. Repository Discussions are not enabled.
