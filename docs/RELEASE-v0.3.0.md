# Codex Usage Mini v0.3.0

The widget now records remaining allowance percentages independently of Codex token logs, so you can see consumption and observed reset frequency over time.

- **Allowance history:** minute-by-minute local observations, a fixed 0-100% chart, hourly/daily summaries, reported reset deadlines, available reset credits, and CSV export for 24 hours, 7 days, 30 days, or 90 days.
- **Reset observations:** distinguishes scheduled window rollovers, early replenishments, percentage increases, and schedule changes. Gaps stay explicit, and counts represent minimum observations rather than an invented complete reset history.
- **Reliable records:** separates accounts and quota windows, preserves history across restarts and interrupted writes, and reports unavailable readings or save failures. Recording continues during color previews and uses no model turns or reset credits.
- **Daily activity charts:** correct local calendar boundaries for 7-, 30-, and 90-day ranges, including today so far, with empty dates and matching model statistics/CSV ranges.
- **Current icon:** the approved bold horizontal green/charcoal weave, embedded at nine Windows icon sizes.

Recording starts while the widget is running; earlier percentages cannot be reconstructed from token totals. Observed drop counts percentage-point decreases between valid readings within three minutes in the same quota window. Usage and additional resets during recording gaps remain unknown. Private records are retained locally for 90 days.

## Windows portable

Download **CodexUsageMini-v0.3.0-portable.zip**, extract it, keep the EXE and its `.config` together, and launch the EXE. Requires Windows 10/11, .NET Framework 4.8, and Codex signed in with ChatGPT. No installer or API key is required. Existing preferences and local history are preserved.

The download contains only `CodexUsageMini.exe`, `CodexUsageMini.exe.config`, and `QUICKSTART.txt`. Personal allowance records, token history, subscription dates, and credentials are excluded.

Validation: **159 automated checks passed**, plus native UI checks for allowance charts and reset observations, empty/missing data, day ranges, compact and full views, themes, and settings.
