# Contributing to Codex for Visual Studio

## Start a change

Keep `main` ready to install. Use one short-lived branch and one pull request per feature or fix. Use `feature/<short-description>` for additions, including tooling, and `fix/<short-description>` for corrections. An issue number is optional.

Start from a clean checkout. Fetch the latest `main` and branch directly from it. For example, to fix workspace inspection, run

```powershell
git fetch origin
git switch --no-track -c fix/workspace-inspection origin/main
```

For a fix, record the reproduction and add a regression test where practical. For a feature, identify one observable acceptance check. Keep unrelated cleanup on a separate branch.

## Open and validate the pull request

Commit the change on the topic branch, push it, and open a PR targeting `main`. Use a draft while verification is pending. Describe what changed, how it was tested, and any known limitations. If `main` advances, update the branch and rerun the checks before merging.

The **Windows build, tests and VSIX** check runs for every PR into `main` and every push to `main`. It restores the solution, builds Release with warnings as errors, runs the full xUnit suite, and checks the packaged manifest, VSPackage registration asset, and assemblies. Each successful run provides a `CodexForVS-VSIX` artifact for seven days. The tests run without a Codex login or API credentials.

To reproduce the gate locally, install the [Windows build prerequisites](README.md#build-and-test) and run these commands from the repository root.

```powershell
dotnet restore CodexForVS.sln
dotnet build CodexForVS.sln -c Release --no-restore -warnaserror -v minimal
dotnet test CodexForVS.sln -c Release --no-build --no-restore -v minimal --blame-hang-timeout 30s
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\eng\Test-Vsix.ps1
```

For changes affecting Visual Studio behavior or installation, install the PR artifact in a VS 2026 test instance and exercise the affected behavior in a disposable solution. Record the result on the PR. For documentation or CI-only changes, the automated checks and a review of the diff are sufficient.

## Merge and release

The maintainer reviews the diff and the validation results, resolves review conversations, and **squash merges** the PR once all applicable checks pass. Use a descriptive PR title suitable for the resulting commit. Delete the merged topic branch and branch the next change from updated `origin/main`. For a regression on `main`, submit the correction or revert through a new PR.

A CI artifact is available for testing. Publishing a tagged release or distributing a VSIX to users is a separate maintainer decision.

## One-time GitHub setup

1. Enable GitHub Actions and allow the GitHub-owned checkout, setup-dotnet, and upload-artifact actions. Enable squash merging under **Settings → General → Pull Requests**.
2. Open the first workflow PR and let the Windows job run. Under **Settings → Rules → Rulesets**, create an active branch ruleset targeting `main`. Require a pull request, resolved review conversations, and the **Windows build, tests and VSIX** status check from GitHub Actions. Require branches to be up to date before merging.
3. Block force pushes and branch deletion, and apply the rules to maintainers as well. A solo maintainer can use zero required approvals and review their own diff before merging. Require one independent approval when a second reviewer is available.

See [GitHub's branch protection documentation](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches).
