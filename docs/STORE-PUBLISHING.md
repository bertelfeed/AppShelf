# GitHub releases to Microsoft Store

The integration is prepared but **not connected**. No app is currently configured
in Partner Center, and publishing stays disabled until setup is complete.

After setup, publishing a normal GitHub Release starts this sequence:

1. Check out its version tag and verify it matches `Directory.Build.props`.
2. Run app checks and offline Store integration checks.
3. Build the x64 MSIX with the reserved Partner Center identity.
4. Check its identity, version, contents and SHA-256 checksum.
5. Save the checked MSIX as a GitHub Actions artifact.
6. Authenticate with Microsoft Store Developer CLI and submit that exact package.

Store availability follows Microsoft's certification process. GitHub drafts and
prereleases do not trigger a production submission. Runs are serialized so two
releases do not submit simultaneously. A failed submission stops the job; inspect
Partner Center before retrying a partially completed submission.

## One-time setup

First reserve the app name in Partner Center, complete its listing and age ratings,
and publish its first version manually. The CLI requires the first publication
to be completed before it can publish updates. See [Microsoft's MSIX CLI prerequisites](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/overview).

Copy the public values from **Product management → Product identity** into
`packaging/StoreIdentity.props`:

| Property | Partner Center value |
| --- | --- |
| `StoreProductId` | The 12-character Store product ID, beginning with `9` |
| `StoreIdentityName` | Package/Identity/Name |
| `StorePublisher` | Package/Identity/Publisher, including `CN=` |
| `StorePublisherDisplayName` | Package/Properties/PublisherDisplayName |

Use the exact reserved identity. `AppShelf.Dev` and the development publisher are
only for local testing and are rejected for submission. Escape XML characters
when editing the props file. These values are public identity data, not secrets.

Alternatively, set these **repository variables** under GitHub Settings → Secrets
and variables → Actions. They override the props file in CI:

| Variable | Overrides |
| --- | --- |
| `MSSTORE_PRODUCT_ID` | `StoreProductId` in both portable and MSIX workflows |
| `MSSTORE_IDENTITY_NAME` | `StoreIdentityName` |
| `MSSTORE_PUBLISHER` | `StorePublisher` |
| `MSSTORE_PUBLISHER_DISPLAY_NAME` | `StorePublisherDisplayName` |

The product ID is embedded into the app when built. It enables the Settings
**Microsoft Store** button and opens the product's Store page, with a browser
fallback. Without an ID the button remains disabled with a localized hint.
To enable it in local builds too, use the props file or pass `-StoreProductId`.

Associate a Microsoft Entra application with your Partner Center account and
configure its API permissions according to [Microsoft's GitHub Actions guide](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions).
Create a GitHub environment named **`microsoft-store`** and add these environment
secrets directly in GitHub:

- `AZURE_AD_TENANT_ID`
- `SELLER_ID`
- `AZURE_AD_APPLICATION_CLIENT_ID`
- `AZURE_AD_APPLICATION_SECRET`

Do not put credentials in this repository, the identity props file, a chat, or
release artifacts. Keep the secret's expiry in mind and rotate it in GitHub when
needed. The workflow only exposes credentials to production validation and
submission steps, not the build or uploaded artifacts.

Finally, set the **repository variable** `MSSTORE_PUBLISH_ENABLED` to `true`.
Without it production runs report that Store is not connected and submit nothing.
Configure environment protection rules if your release process requires them;
the repository does not impose an additional manual approval step.

## Check the connection without publishing

In Actions, run **Microsoft Store update**, choose an existing version tag that
contains this integration, and leave **dry_run** enabled. The tag must be in the
form `v1.0.1` and match the version in its source checkout.

A dry run builds and checks the MSIX without authentication, uploads to Microsoft,
or submission. Without an identity it produces a development MSIX. The package is
available as a GitHub Actions artifact for inspection. A successful dry run proves
the local build and validation, not that Store credentials or certification work.

Local checks, also offline:

```powershell
./tools/check-store-integration.ps1
./tools/prepare-store-release.ps1 -Tag v0.6.0 -DryRun
./build.ps1 -Check -Msix
./tools/submit-store-release.ps1 -Tag v0.6.0 -PackagePath artifacts/AppShelf-0.6.0-x64.msix -DryRun
```

Use the tag and file name for your actual current version. The integration checks
use synthetic local fixtures and a mocked CLI; they never contact Microsoft.

## Release updates

Increase `Version` in `Directory.Build.props` for every Store update. MSIX uses
`major.minor.patch.0`; the final component is reserved as zero. Keep new versions
higher than the last published package. Run the GitHub draft-release workflow,
review its outputs, then publish the normal release. The Store workflow rebuilds
from the same tag and submits after its checks pass. The existing draft workflow
marks versions below `1.0.0` as prereleases; they remain GitHub previews.

The initial Store publication and live package upgrade still require manual
validation. A workflow has not been run against a real Store app in this project.
