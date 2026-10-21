# Codex Usage Mini v0.2.0

This update adds local usage insights, a readable compact view, appearance controls, and automatic subscription-period detection.

- Hourly usage patterns and model-by-model token statistics from local Codex records, with 24-hour, 7-day, 30-day, and 90-day ranges and CSV export.
- A visible **Percentage only** button for the smaller colored-percentage view; hover and use the expand arrow to restore full details. Dragging moves the compact widget.
- Daylight, Dark, Ocean, and Forest themes, 0–50% transparency, and proportional full-widget scaling.
- Automatic **Paid through** detection from matching Codex subscription metadata, with its source check time, a Check now control, and a separate manual expiry fallback.
- Private local history and preferences preserved across restarts.

Paid through is the subscription-period end reported by Codex; renewal/cancellation status may be unavailable. Model token totals describe observed local activity and are not model-specific subscription charges.

## Windows portable

Download **CodexUsageMini-v0.2.0-portable.zip**, extract it, keep the EXE and its .config together, and launch the EXE. Requires Windows 10/11, .NET Framework 4.8, and Codex signed in with ChatGPT. No installer or API key is required.

The download contains only the executable, its runtime configuration, and QUICKSTART.txt. Personal history, subscription dates, and credentials are not included.

Validation: **103 deterministic checks passed**, plus native UI checks for compact/expanded views, themes, transparency, history, subscription display, and settings.

SHA-256: `884856C8D113182200267D9A26896BF6840968921044A1A5BBC17FF68F5C7171`
