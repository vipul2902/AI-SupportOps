# Security Policy

## Reporting a vulnerability

Please do not open a public issue. Report vulnerabilities privately via GitHub's
[security advisory](https://github.com/vipul2902/AI-SupportOps/security/advisories/new) feature.

## Secrets

This repository never contains real credentials. Configuration is supplied through
`.env` (git-ignored), `dotnet user-secrets`, or the hosting platform's secret store.
See `.env.example` for the required variables.
