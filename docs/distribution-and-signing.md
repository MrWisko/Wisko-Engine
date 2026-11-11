# Distributing Wisko: unsigned test builds and signed releases

**Status (2026-10-25): a decision document.** Every Wisko build so far is unsigned on Windows and only ad-hoc signed on
macOS. Nothing has been bought, no developer program has been joined, and nothing has been published. Vendor
requirements and prices below come from the vendors' pages on that date; check them again before deciding.

## What people see with an unsigned build

### Windows

The installer (`Wisko-<version>-win-x64-setup.exe`), its uninstaller and `Wisko.exe` (installer and portable zip) carry
no Authenticode signature.

| Situation | What happens |
|---|---|
| A copy downloaded with a browser (Windows marks it as coming from the internet) | Microsoft Defender SmartScreen shows "Windows protected your PC" with Unknown publisher. "More info" > "Run anyway" starts it. Explorer passes the mark on to files extracted from a downloaded zip, so the portable `Wisko.exe` asks too. |
| Smart App Control is on (Windows 11) | Unsigned apps without reputation are **blocked**. Microsoft: "There is currently no way to bypass Smart App Control protection for individual apps"; the only way is to turn Smart App Control off. Many people cannot or should not do that. |
| "Install for all users" (administrator) | The UAC prompt says Unknown publisher. The default per-user install shows no UAC prompt. |
| A copy built on the same computer, or copied without the download mark | No prompt. This is why the development PC never shows any of this. |

### macOS

`Wisko Engine.app` is ad-hoc signed (as Apple Silicon requires) but not signed with a Developer ID and not notarized.

| Situation | What happens |
|---|---|
| A copy downloaded with a browser (quarantined) | macOS refuses to open it. Allowing it: System Settings > Privacy & Security > Security > **Open Anyway** (for about an hour after the attempt), then the account password. Since macOS 15, Control-click > Open no longer skips this. |
| A managed Mac (school, company) | Allowing it may be impossible for the person. |
| A copy built on the Mac, or fetched with `gh` or `curl` (no quarantine) | Opens directly. |

## A clearly documented unsigned test build

Until a release is signed, an unsigned build can serve testers, provided it is labelled and handled as one:

- **Distribute it privately**: the private repository's Actions artifacts (kept 7 days for the app), or a direct share.
  It is not a public release.
- **Label it** as an unsigned test build, in its name and wherever it is offered. The macOS artifact is already named
  `Wisko-Engine-macos-arm64-unsigned.zip`; the Windows file names do not say so yet.
- **Publish its SHA-256** next to it, so testers can check they have the tested file.
- **Give testers the exact steps**:
  - Windows: SmartScreen "More info" > "Run anyway"; a PC with Smart App Control on cannot run it.
  - macOS: "Open Anyway" in Privacy & Security
    ([real-Mac checklist](macos-real-mac-checklist.md#getting-the-tested-app-no-public-release)).
- **Never ask the public** to turn off Smart App Control or Gatekeeper.

## What a signed release changes

| | Unsigned test build | Signed (Windows) / signed and notarized (macOS) |
|---|---|---|
| Windows download | "Windows protected your PC", Unknown publisher, More info > Run anyway | The verified publisher's name. SmartScreen can still warn until the file has enough download history ("reputation builds up automatically", Microsoft) |
| Windows with Smart App Control on | Blocked, no override | Runs: Smart App Control accepts a valid signature when it has no prediction for the app |
| UAC (all-users install) | Unknown publisher | The publisher's name |
| macOS download | Refused; "Open Anyway" in Privacy & Security plus the password | Opens after the usual "downloaded from the internet" confirmation |
| Name shown | None | The validated legal name of the person or registered organization |
| Cost | None | Yearly (below) |
| Work in Wisko | None | Signing (and on macOS notarization) in both build scripts and CI, with secrets, and testing again |

### Windows signing options

- **Microsoft Artifact Signing** (formerly Trusted Signing):
  - Public Trust certificates for **individual developers only in the United States or Canada**.
  - Organizations in the US, Canada, the EU, the UK and some other countries, after validation of a legal business
    entity (business identifier, website, domain email; 1 to 20 business days).
  - Needs a paid Azure subscription. Tiers: Basic (5,000 signatures a month) and Premium. The pricing page shows no
    amounts; it points to Azure's price calculator. No EV certificates.
- **A code-signing certificate from a commercial certificate authority**, issued to the person or the organization:
  - The key is kept on a hardware token or in a cloud signing service.
  - Prices and identity checks differ by authority.

Either way, the certificate shows the validated legal name. "Wisko Industry Development" can only appear once it is a
registered legal entity; for an individual, it is the person's own name.

What would be signed:

- `Wisko.exe` and Wisko's own DLLs;
- the installer and the uninstaller (Inno Setup's `SignTool` setting signs both);
- the .NET runtime files, which are already signed by Microsoft;
- the third-party native DLLs, which are not signed now: signing them as distributed is a separate choice.

### macOS signing and notarization

- **Apple Developer Program:**
  - 99 USD a year (shown in local currency when enrolling).
  - An **individual** enrollment shows the person's legal name.
  - An **organization** needs a registered legal entity and a D-U-N-S number.
- **Then:**
  - sign every binary in the app (the executable, the .NET runtime libraries and every native library) with a
    Developer ID Application certificate, the hardened runtime and a secure timestamp. .NET needs JIT entitlements, to
    be confirmed with Microsoft's .NET documentation;
  - submit the app to Apple's notary service and staple the ticket;
  - test again that bgfx/Metal, Lua, Assimp and the file dialogs still load under the hardened runtime.
- **Changes in Wisko:**
  - `scripts/build-macos-app.sh` (Developer ID instead of ad-hoc signing);
  - the CI packaging check, which today compares ad-hoc signatures;
  - the checklist's Gatekeeper section.

## Decisions to make

1. Publish 0.1.0 unsigned, as a clearly labelled preview, or wait for signing.
2. Whose name appears: the person's own, or an organization that would first have to be registered.
3. Windows: a certificate authority's certificate, or Artifact Signing (open to individuals only in the US and Canada).
4. macOS: joining the Apple Developer Program.

None of this has been started.
