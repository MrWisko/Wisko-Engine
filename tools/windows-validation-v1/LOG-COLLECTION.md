# Collecting the logs

## After the automated run

The script collects everything itself. Bring back one file:

`%USERPROFILE%\WiskoValidation\run-<date>-<time>-results.zip`

It holds `SUMMARY.md`, `SHA256SUMS.txt`, every log under `logs\`, and the GUI screenshots. It leaves out the large
`packages\`, `work\` and `installer-test\` folders.

If the script stopped before writing the zip, bring the run folder's `SUMMARY.md` and `logs\` folder instead
(right-click > Send to > Compressed (zipped) folder).

## After the manual checklist

- `MANUAL-CHECKLIST.md`, filled in.
- For each failure: a screenshot (Win+Shift+S) and the log of the account you tested in:
  - `%LOCALAPPDATA%\Wisko\Logs\Wisko.log` (this session);
  - `%LOCALAPPDATA%\Wisko\Logs\Wisko.previous.log` (the session before).

  Copy them right after the failure: the next start of Wisko replaces `Wisko.previous.log`.
- Installer tests:
  - Setup's own log, `Setup Log <date> #<n>.txt` in `%TEMP%` of the test account. It records every example project
    copied or left alone.
  - The exact SmartScreen text (B1, B8).

## Before sending anything

The logs contain paths such as `C:\Users\<your name>\…` and the PC's hardware details. They contain no passwords or
tokens: the script never reads Git or GitHub credentials. Look through them if you plan to share them beyond your own
use.

Do not send or upload the packages from `packages\` as release files. They are test builds of the commit; the release
files are built again from the tagged commit when the release is made.
