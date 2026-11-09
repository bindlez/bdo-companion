# BDO Companion product and engineering specification

Status: working specification, November 8, 2026. Targets below are proposed acceptance gates. The alpha.3 preview and BDO Vision 1.13.0-rc.29 are the implementation and behavior baselines respectively.

## 1. Product

A Windows desktop companion for grinding and lifeskilling. A small tracker runs during play, and the player can open richer desktop tools or pair the existing browser planner. Keep verified records distinct from estimates and make uncertainty visible. The first supported live matrix is English BDO on Windows x64, with NA and EU market data. Expand each language and region only with labeled replay coverage and a live setup check.

| Surface | Required behavior |
| --- | --- |
| Tracker | Own capture, recognition scheduling, session history, active timers and local browser synchronization. Start only the tracker. Suspend capture when the game or region is unavailable. |
| Desktop companion | Launch one separate Avalonia process from the tray. Show craft and loot status, recipes, regional prices, history, gear and CP. Closing the last full window exits that process and leaves tracking running. |
| Browser planner | Keep the existing paired planner usable in the user's browser. Sync timers, recipes and market data with the tracker while the desktop window is closed. |
| Local data | Track and review sessions offline. Require an explicit action for exports or sharing. Label stale and unavailable prices, and retain historical valuation inputs. |

Use C# Native AOT for Tier 1. Keep Chromium out of both desktop processes. Start the Windows OCR worker only when capture needs it and stop it when capture is suspended. A worker crash must preserve the tracker, last verified observations and committed history.

## 2. Competitive baseline

Public feature descriptions reviewed November 8, 2026. They establish what each vendor advertises. Comparable CPU, memory, accuracy and frame-time results require controlled runs. An undocumented capability is unconfirmed.

| Area | Garmoth App | AionJanis's BDO Loot Tracker | Companion requirement |
| --- | --- | --- | --- |
| Live loot | OCR loot feed, grind and lifeskill sessions, multiple session automation modes | Drop-log OCR across grinding, hunting, gathering, cooking and alchemy, with rare-log coverage | Verify normal and rare loot and distinguish repeated observations from new events. Cover grind and crafting first. Measure additional lifeskills separately. |
| Valuation | Estimated silver per hour and grind goals | Regional Central Market prices, post-tax silver/hour and visible uncertainty | Show gross, estimated net and unpriced items separately. Record quote region, identity, timestamp and basis. |
| Overlay | Customizable in-game widgets and an OBS browser source | Compact, pin-on-top display and notable-drop alerts | Ship a compact tracker overlay. Put rich views in Tier 2. OBS and share cards follow core parity. |
| History and planning | Account-linked tracking and wider Garmoth planning tools | Local history, insights, goals, and crafting and upgrade planning tools | Preserve local searchable history. Differentiate through verified tool durability, covered craft estimates, scoped guild progress, and timer, recipe and market sync between desktop and browser. |
| Coverage | Advertises 13 client languages and nine PC regions | Advertises nine PC regions and scaled 4K interfaces | Publish an explicit support matrix. Treat initial language and region coverage as a competitive gap. |

Sources: [Garmoth App](https://garmoth.com/APP), [Garmoth changelog](https://garmoth.com/changelog), [AionJanis's BDO Loot Tracker](https://bdoloottracker.com/).

## 3. Recognition and session rules

- Verify the game process, window and calibrated crop before capture. Check again after capture or asynchronous OCR. Invalidate pending work on moves, visibility changes, configuration changes and session transitions. Work from bounded crops and queues. Record dropped frames and ambiguous intervals.
- Separate image observations from accepted events. Every accepted loot event needs an item and quantity supported by the source. Preserve unknown rows for review. Match consecutive feed windows by ordered overlap so retained rows do not inflate totals and identical new drops can still count. An ambiguous overlap opens a gap rather than backfilling guesses.
- Start, pause, resume and finish are explicit session transitions. Ignore observations while paused and rebaseline retained feed rows on resume. Use capture timestamps for accepted events and monotonic time for elapsed duration. Commit each accepted batch and its checkpoint in one SQLite transaction under one writer. Replaying a committed batch must be safe.
- Confirm durability using matching, valid reads. Track verified state separately from the countdown estimate. Pause and pace changes settle elapsed time before changing the projection. Covered countdowns show last verification time and estimate status. Reconcile after visibility returns. Timer conflicts expose the current timer and require explicit cancellation or replacement.
- Guild reads must match the intended quest and counter. Last-seen values may remain visible with age. A hidden or unreadable counter provides no progress or completion evidence. Equipment imports require user review before saving.
- Use visible pixels and ordinary Windows capture only. Prohibit game input automation, game memory reads, hooks and packet capture. Keep private captures, profiles and diagnostics out of commits and packages.

## 4. Browser and storage contracts

- Keep the bridge on loopback. Preserve rc.29 routes and request/response semantics for supported flows, including instance IDs, ordered durability signals and acknowledgments. Preserve local planner serving and opt-in loot sharing. Require the expected origin on writes and validate framing, content type, sizes and route payloads.
- One tracker owns active timers and history. Browser and desktop views reconnect to a current snapshot. Stale commands must not change state. Duplicate commands must return the original outcome or fail safely. Reject requests that target another instance.
- Isolate development and first-run preview profiles. Before any rc.29 migration, create a consistent backup and run a dry migration against a disposable copy. Compare session counts, event totals, date ranges and price references. Preserve the original database for rollback.
- Separate quote lookup, session valuation and historical pricing. Match exact item IDs where available, surface ambiguous names, preserve region boundaries and distinguish provider quotes from user overrides.

## 5. Evidence and release gates

Targets are provisional until measured. Passing CI verifies the packaged contracts exercised by CI. Live BDO checks are separate.

| Gate | Target and proof |
| --- | --- |
| Loot correctness | At least 99.5% exact item-and-quantity precision and 98% recall on an independently labeled holdout with at least 2,000 visible events. Report support by capture scenario. Require zero duplicate ledger entries in replay, pause/resume and restart fixtures. |
| Hard negatives | Zero accepted loot, tool or guild events on labeled blocked, minimized, unrelated UI and unreadable fixtures. Test wrapped rows, rapid repeats, rare drops, unknown items, DPI and monitor changes. Report misses separately from wrong accepts. |
| Detection latency | p95 ≤2 seconds from visible loot to ledger update under normal workload. Report burst latency and skipped frames separately. |
| Craft timing | No premature completion alert in labeled pause and visibility-loss fixtures. In live covered-interval tests report absolute countdown error and signed early/late error after at least ten minutes, then compare with rc.29. |
| Memory and game impact | Provisional idle budget ≤35 MiB private for the tracker. After closing Tier 2, return within 10 MiB of the tracker-only baseline within 30 seconds. In live tests target ≤5% p95 game frame-time regression versus BDO alone. Record worker, desktop and browser memory separately and total each scenario. |
| Compatibility | Verify rc.29 timer, recipe, quote, reconnect, conflicts and sharing behavior in a browser, with Tier 2 open and closed. Rehearse migration and recovery on disposable data. Pass clean extracted Windows package checks. |

Use the same machine, resolution, client scale, language, feed layout, window mode, capture workload and sampling interval across comparisons. Record versions, process trees, hardware, settings, fixture hashes and sample sizes. Run BDO alone first, then rc.29, Companion and each competitor separately. Use at least three repeated runs per scenario. Include idle, ordinary grinding, burst loot, crafting and covered windows. Compare only scenarios each product supports. Avoid comparative claims until matched runs support them.

Alpha.2 local evidence: extracted package passed bridge smoke and desktop lifecycle checks. Sample-mode private memory was 19.6 MiB tracker only, 118.3 MiB with desktop open and 20.0 MiB after closing. These exclude live OCR, BDO and the browser.

Alpha.3 local evidence: synthetic loot overlap and guild parsing passed, as did SQLite reopen and stale-command checks. The Windows OCR worker recognized a two-line synthetic guild fixture. These checks do not establish live accuracy or rc.29 parity. Keep rc.29 as the production version until the live gates pass.

## 6. Delivery order

1. Prove alpha.2 durability and timer sync live. Measure foreground, covered and resumed behavior against rc.29.
2. Add replay-tested loot event extraction, deduplication, local SQLite history and restart recovery. Validate normal and rare feeds before shipping live totals.
3. Add guild progress, craft timer ownership and desktop overlay. Keep stale readings and uncertain events visible.
4. Finish the native recipe, gear, CP and history surfaces. Verify end-to-end browser parity, including shared state with both interfaces open.
5. Run the competitive and migration gates, then publish a candidate with its measured support matrix and known gaps.

When a pull request changes capture, recognition, tracking, bridge, storage or performance, cite the relevant spec section and attach verification or name the remaining gate. Recheck official competitor sources before each release candidate and update this matrix when capabilities change.
