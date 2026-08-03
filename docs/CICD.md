# Tally BigQuery Agent CI/CD Guide

## Goal

Every code change must be built and tested automatically. Production installers are created only from approved release tags.

## Workflows

### Windows Agent CI

File: `.github/workflows/windows-agent-ci.yml`

Runs on:

- Pull requests
- Pushes to `develop`
- Pushes to `main`
- Manual workflow runs

Actions:

1. Checks out the repository.
2. Installs .NET 8.
3. Installs Inno Setup.
4. Restores and tests the solution.
5. Publishes Service, CLI and Manager as self-contained `win-x64`.
6. Compiles `Tally BigQuery Agent Setup.exe`.
7. Generates a SHA-256 checksum.
8. Uploads the installer and test results as workflow artifacts.

### Windows Agent Release

File: `.github/workflows/windows-agent-release.yml`

Runs when a semantic-version tag is pushed, for example:

```bash
git tag v1.0.1
git push origin v1.0.1
```

Actions:

1. Runs the complete build and test process.
2. Produces a versioned installer.
3. Uploads the installer as an artifact.
4. Creates a GitHub Release.
5. Attaches the installer and SHA-256 checksum.

## Branch policy

Recommended branches:

- `main`: approved production code
- `develop`: integrated development testing
- `feature/<name>`: individual changes

Recommended flow:

```text
feature branch
  -> pull request to develop
  -> CI passes
  -> test installer on a non-production Tally machine
  -> pull request to main
  -> CI passes
  -> create version tag
  -> release installer
  -> administrator-approved production installation
```

## First GitHub setup

1. Create a private GitHub repository.
2. Upload this source tree to the repository.
3. Open **Settings > Actions > General**.
4. Allow GitHub Actions to run.
5. Open **Settings > Branches**.
6. Protect `main`.
7. Require a pull request.
8. Require the `build-test-package` status check.
9. Prevent direct pushes to `main`.

No cloud or code-signing secrets are required for the initial CI build.

## Production update procedure

1. Developer creates a feature branch.
2. Developer pushes code and opens a pull request.
3. GitHub Actions builds and tests automatically.
4. Download the CI installer and test it on a test machine.
5. Merge the approved code to `main`.
6. Create a release tag such as `v1.0.1`.
7. Download the installer from the GitHub Release.
8. Verify its SHA-256 checksum.
9. Back up:
   - `C:\ProgramData\TallyBigQueryAgent\config.json`
   - `C:\ProgramData\TallyBigQueryAgent\agent.db`
10. Run the installer as Administrator.
11. Confirm the service is `Running` and `Automatic`.
12. Test Tally, cloud connectivity and one synchronization.

## Version rules

Use semantic versioning:

- `1.0.1`: bug fix
- `1.1.0`: backward-compatible feature
- `2.0.0`: breaking change

The Git tag controls the file and installer version during release builds.

## Code signing

Before external distribution, add certificate-based signing. Do not commit certificates or passwords into Git.

Recommended options:

- Microsoft Trusted Signing / Artifact Signing
- Azure Key Vault-backed signing
- Organization-managed hardware signing certificate

The release must sign the Service, CLI, Manager and final installer before publication.

## Safety

Windows Agent updates are continuous delivery, not silent continuous deployment. CI/CD prepares and validates the installer. A human administrator approves installation on the production Tally server.
