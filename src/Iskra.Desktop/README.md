# Iskra.Desktop

Side-by-side Avalonia frontend for the cross-platform port. WPF remains the
supported Windows operator app; this one does not replace it until it has
hardware-in-the-loop acceptance on Windows, Linux, and macOS.

## What it does

The localized app has the same four top-level destinations as WPF and consumes
the shared `Iskra.Application` catalog, station-readiness, batch, history, and
settings policies — no duplicated safety logic.

The Flash tab now runs real flashing through `FlashWorkflow`, the same
transaction the WPF station uses:

- **Compact field row** — operator, product, version, and (only when batch mode
  is enabled) batch ID. The batch block sits in an `Auto` column so it collapses
  to zero width when batches are off.
- **Giant PASS/FAIL band** — the WPF palette exactly: green `#1B8A1B` on pass,
  red `#C0392B` on fail, amber `#F2C14E` for a blocking readiness prompt, grey
  when idle. The verdict renders at 56 px and guidance prompts at 34 px.
- **Big FLASH button** — full width, 80 px tall, with the configured hotkey
  shown underneath. The `AppSettings.FlashHotkey` value (`None`/`Space`/`Enter`/
  `F2`/`F5`) is honored window-wide via a tunneling key handler, so a barcode
  scanner's trailing Enter still means "flash". Space is suppressed while an
  editable text box has focus.
- **gdb console** — dark read-only log that streams `GdbLine` output live and
  auto-scrolls to the tail.

Star rows with `MinHeight` let the band and the console share spare space and
shrink together; `ClipToBounds` means a very short window crops rather than
painting one section over another.

Gating matches WPF: gdb found, exactly one Black Magic Probe, a trusted catalog,
a non-empty operator, a batch ID when batch mode is on, and a resolved
product/release. Discovery re-runs immediately before the request snapshot, so a
probe unplugged after the last refresh cannot be flashed against a stale port.

## Known gaps versus WPF

- **No GitHub Device Flow UI.** Remote releases reuse the Windows DPAPI token
  store written by WPF or `Iskra.Cli --login`; the Flash tab shows a hint when a
  remote release is selected and no credentials are stored. Linux and macOS fail
  closed on remote firmware — there is no encrypted `ITokenStore` for them yet,
  and no plaintext fallback will be added.
- **Settings are read-only** apart from the language selector, which reloads the
  latest settings and atomically saves only the language change so a stale
  session cannot clobber newer WPF values.
- **No CSV export** on the History tab.
- **No hardware-in-the-loop acceptance yet.** The header badge says so.

`global.json` pins .NET SDK 10.0.301, the project targets .NET 10, and Avalonia
is pinned to 12.1.0.

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project src\Iskra.Desktop
```
