# Microsoft Store listing

Texts to paste into Partner Center → app → submission → **Store listings**, one file per language:
[`en-US.md`](en-US.md) and [`ru-RU.md`](ru-RU.md). Only these two languages: the app has no others.

Limits, from [Add and edit Store listing info for MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info):

| Field | Limit | Notes |
| --- | --- | --- |
| Description | 10,000 characters, required | Plain text. No HTML, code or URLs. |
| Short description | 1,000 characters | Only the first 270 show in some views, so the texts here stay under that. |
| Product features | up to 20 items, 200 characters each | Shown as a bulleted list: paste each line without its own bullet. |
| What's new in this version | 1,500 characters | Leave empty for the first submission. |

## Media for each language

| Partner Center | File in `artifacts/store-media/<language>/` |
| --- | --- |
| Screenshots | `screenshots/01-desktop.png` … `06-editor.png`, in that order |
| Trailer | `promo/promo-1920x1080.mp4` (the only video uploaded) |
| Trailer thumbnail, 16:9 Super hero art | `art/hero-16x9-1920x1080.png` |
| 2:3 Poster art | `art/poster-art-2x3-1440x2160.png` |
| 1:1 Box art | `art/box-art-1x1-2160.png` |

## Fields shared by all languages

These live in other parts of the submission (Properties, Pricing and availability), not in the listing.

| Field | Value |
| --- | --- |
| Category | Productivity first; Utilities & tools or Personalization second |
| Website | `https://github.com/bertelfeed/AppShelf` |
| Support contact | `https://github.com/bertelfeed/AppShelf/issues/new/choose` |
| Privacy policy | AppShelf collects no data. The Store version only asks Microsoft Store about updates; the GitHub version asks GitHub for its latest release once a day (Settings can turn this off). If Partner Center asks for a URL, a short `PRIVACY.md` in the repository is enough (not written yet). |
| Copyright | © 2026 bertelfeed. MIT License. |
