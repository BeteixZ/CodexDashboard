# Project automation

Run the isolated regression checks from a clean checkout with Windows, PowerShell 7, .NET 10 SDK, Python 3.13 and Node.js 24:

```powershell
pwsh -NoLogo -NoProfile -File scripts/Test-Project.ps1
```

The runner creates a new directory under `artifacts/tests/` and retains logs and `summary.json`. An optional `-OutputDirectory` must be a new absolute path. Fixtures are synthetic; no production settings, credentials, model calls, WeChat sends, SSH sessions or user tasks are used. A clean checkout needs network access to restore public NuGet dependencies. Protocol checks create real current-user named pipes and require a normal Windows user context. They fail instead of weakening pipe permissions. The historical frozen-reader/schema checks need separately retained old artifacts and are not claimed by this clean-checkout suite.

The quality workflow runs on `windows-2025`. It is reusable by the release workflow and grants only `contents: read`. All external actions use complete commit SHAs; Dependabot proposes updates.

To build a release from the GitHub Actions UI, choose **Build and release Windows packages**, select the source branch, and enter a numeric version such as `0.1.0`. Leave `publish` off to retain a draft, or enable it to publish a prerelease only after validation. An existing tag must identify the same source commit. A new tag is created against the tested commit when the draft is created. Pushing a version tag also runs the workflow and publishes a validated prerelease.

The release runs the isolated quality checks, `Build-Release.ps1`, package validation, native Setup installation/upgrade/occupancy rejection/uninstall with unique test-only registration, script installation checks, and installed-product lifecycle isolation. The Windows runner supplies Inno Setup 6. The final job alone receives `contents: write`. It creates a draft, uploads both Setup installers and both portable archives with their manifest and SHA-256 checksums, downloads them again, and compares every file hash before publication. An existing public release or populated draft is preserved and requires manual review. No workflow installs into an existing user's product or data directory. Silent Setup and synthetic lifecycle checks do not constitute normal desktop Setup click-through or real phone/SSH acceptance.

Implementation references (verified 2026-10-26):

- [GitHub Actions secure use](https://docs.github.com/en/actions/reference/security/secure-use): immutable action SHAs and minimal token permissions.
- [Windows 2025 runner software](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md): hosted Windows environment and Inno Setup.
- [setup-dotnet](https://github.com/actions/setup-dotnet), [setup-python](https://github.com/actions/setup-python), [setup-node](https://github.com/actions/setup-node): explicit runtime versions.
- [GitHub CLI release create](https://cli.github.com/manual/gh_release_create), [upload](https://cli.github.com/manual/gh_release_upload), [download](https://cli.github.com/manual/gh_release_download), [edit](https://cli.github.com/manual/gh_release_edit): draft assets, source target, download verification and prerelease publication.

Action pins resolve to official repository tags: checkout v6.1.0, setup-dotnet v6.0.0, setup-python v7.1.0, setup-node v7.1.0, upload-artifact v7.0.2, download-artifact v8.0.2. Locally the workflows were checked with actionlint v1.7.12, obtained from its official release and checked against the published SHA-256 inventory.
