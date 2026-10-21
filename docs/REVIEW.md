# v0.2 validation

- [x] 103 deterministic allowance, protocol, accounting, history, subscription, and preference checks pass.
- [x] Native UI checks pass for allowance states, pin/hide/show, four themes, full-view scaling, opacity, hourly/model history, and settings.
- [x] The visible Percentage only button collapses to a fixed readable percentage; the hover expand button restores the original dimensions. Green/orange/red/gray states remain correct.
- [x] History and appearance remain in separate windows opened on request; compact options appear on hover or keyboard focus.
- [x] Interactive native review confirms mouse collapse, drag without expansion, keyboard expansion after dragging, restoration to 100% size, and opening/closing appearance from the compact options menu.
- [x] Live allowance and the separate entered expiry display correctly in the running widget.
- [x] Available real local usage records import in the background without storing prompt/message content.
- [x] Mixed-format sessions prefer authoritative response records to avoid inflated totals.
- [x] Synthetic screenshots and the portable package contain no personal history or expiry dates.
- [x] The supplied personal expiry is stored only in private local preferences.
- [x] Live automatic detection reads the matching Codex subscription metadata independently of the entered date.
- [x] Authentication expiry and allowance reset dates are excluded from subscription detection; account conflicts and invalid metadata fail safely.
- [x] Detected dates are labeled Paid through with source check time; renewal/cancellation status is not inferred.
- [x] Interactive native review confirms automatic detection, the source-check display, disable/re-enable with manual fallback intact, and Check now. No detected credential data is persisted.
- [x] User reviewed the update and approved publishing v0.2.0 and its portable Windows app.

The live allowance connection was verified in the running v0.2 widget after a temporary service outage. During the outage, unknown/stale allowance, cached plan details, entered expiry, and local history remained available as designed.
