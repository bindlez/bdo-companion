# Contributing and releases

Keep main runnable. Use short feature branches and pull requests. Squash each reviewed change into a focused commit. Use a revert commit to undo a change already merged into main.

The Windows workflow builds the Native AOT executable and runs a loopback smoke test on pushes and pull requests targeting main. Run the same checks locally with:

```powershell
dotnet publish -c Release -r win-x64 -p:TreatWarningsAsErrors=true -o artifacts/native
./scripts/Test-Bridge.ps1 -Executable artifacts/native/Companion.exe
```

For release candidates, update VERSION and CHANGELOG.md in the same pull request. Once it merges and Windows CI passes, create an annotated tag from that exact commit using the version prefixed with v. Pushing the tag builds and tests the tagged commit and creates a GitHub release draft with a Windows ZIP and SHA-256 checksum. Review the draft and test the extracted ZIP before publishing. Create a new version for any fixes after tagging.

Keep game captures, profiles, databases and credentials outside version control. Keep the current prototype on a separate profile when adding persistence. Test data upgrades with a disposable copy.

For changes to capture, recognition, tracking, bridge, storage or performance, cite the relevant [spec section](docs/SPEC.md) in the pull request. Include test evidence or name the remaining gate. Refresh the competitive baseline before each release candidate.
