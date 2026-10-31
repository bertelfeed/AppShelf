# Contributing

AppShelf is a Windows 11 desktop application written in C# and WPF. The current
development preview is 0.6.0; the first stable release is planned as 1.0.

## Build and check

Use Windows 11 x64 and a stable .NET 10 SDK. Visual Studio is optional.

```powershell
./build.ps1 -Check
./build.ps1 -Check -Package
```

The second command produces an isolated portable ZIP and SHA-256 file under
`artifacts/`. It does not read or include the normal user's profile. NuGet and
CLI caches are local to the checkout and ignored by Git.

To build the MSIX test package, install the Windows SDK and run `./build.ps1 -Msix`.
`./build.ps1 -Check -Package -Msix` produces the ZIP and the MSIX from one publish, so
both contain the same binaries. `-Msix` cannot be combined with `-Publish`. Set `-IdentityName`,
`-Publisher` and `-PublisherDisplayName` to the reserved Partner Center identity for
a Store submission. The default identity is only for local package development.
Building a package does not install it or verify packaged startup, aliases or updates.

The Store release workflow and its offline checks are described in
[Store publishing setup](docs/STORE-PUBLISHING.md). Run `./tools/check-store-integration.ps1`
when changing the release scripts. No credentials or network requests are needed.

There are native window and Shell checks. Run the full suite in an unlocked
interactive Windows session. Hosted CI explicitly skips the two checks that
switch the real foreground window; it reports them as SKIP, never as PASS.
All other checks remain enabled. See [verification](docs/VERIFICATION.md).

## Safe development profiles

Never use your everyday profile as a test fixture. Pass `--data-dir` with a
temporary directory under `artifacts/`. The app must remain running for desktop
single-click and folder taskbar buttons. Close the correct profile with
`AppShelf.exe --quit --data-dir "path-to-test-profile"` before replacing its binaries.

Preserve stable item IDs, migration compatibility, `.bak` recovery and copied
launchers. Importing or removing an AppShelf item must not delete the original
application or document. Desktop tidying moves only shortcuts into the reversible
profile archive, and only when explicitly requested.

## Pull requests

Describe the user-visible problem, the change, and how you checked it. Keep fixes
focused. Include a screenshot for visible layout changes, using a demonstration
profile with no personal files. Do not commit profiles, copied launchers, logs,
tokens, private paths, generated binaries or runtime packages.

The `LICENSE` applies to contributions. Runtime components retain their own
notices; see `THIRD-PARTY-NOTICES.txt`.
