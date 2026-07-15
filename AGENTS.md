# AGENTS.md — InfoLCD Apex Update

Space Engineers 1 mod. Part of Chris's mod collection under
[`gitpush-mod`](https://github.com/gitpush-mod).

## What this is

InfoLCD for Apex LCD blocks — Chris's flagship SE mod (1,335+ Steam Workshop
subscribers). Text-surface-scripts + config that expose real-time ship/base
data on Apex LCD screens: cargo, power, jump drive, ammo, airlock, more.

This repo is the **Apex Update** variant. A sibling **Apex Advanced** variant
lives at [`gitpush-mod/se-infolcd-apex-advanced`](https://github.com/gitpush-mod/se-infolcd-apex-advanced)
— they share a lineage and should be kept in sync on shared features.

## Where work lives (RULE — non-negotiable)

**Every task on this mod is a ticket on its project board.** YOU (the agent) create the ticket BEFORE touching anything. No exceptions for "small" work.

**Board:** [InfoLCD Apex Update board](https://github.com/orgs/gitpush-mod/projects/3)

Concrete rules — same as TCS ticket-first:

- **Starting work?** Open a ticket in this repo, add to the board, set Status = **In Progress**, then start.
- **Have an idea for later?** Ticket in **Backlog**. Not in memory, not in a README, not in NOTES.md.
- **Finished?** Close the ticket with a closing summary comment (what you did / problems + solutions / anything NOT done).
- **Same-session micro-work?** Open + close in the same session — but the ticket exists.
- **Older than 30 days in Done?** The weekly cron in this repo moves it to Archived. The closed ticket + CHANGELOG entry persist.

Automation: closing a Done ticket auto-appends an entry to this repo's `CHANGELOG.md` (via the workflow in `gitpush-mod/.github`). Requires `MOD_PROJECT_TOKEN` org secret — already installed.

**Do NOT** work on this mod without a ticket. If Chris asks for something small, create + immediately-close the ticket. It's still faster than the paper-trail debt of un-ticketed work.

## For agents

- **Game:** Space Engineers 1
- **SE modding skill:** Chris has an SE modding Claude skill
  ([`Godimas101/se-claude-skill`](https://github.com/Godimas101/se-claude-skill))
  — use it when working on this mod
- **SDK:** `D:\SteamLibrary\steamapps\common\SpaceEngineersModSDK\`
- **Vanilla SBCs (authoritative for TypeId/SubtypeId + schema baselines):**
  `D:\SteamLibrary\steamapps\common\SpaceEngineers\Content\Data\`
- **Workshop mods (dependencies / reference):**
  `D:\SteamLibrary\steamapps\workshop\content\244850\`
- **Sibling mod:** `se-infolcd-apex-advanced`

## Key invariants

- **Client-side only.** InfoLCD must stay client-side. No SessionComponent or
  server-side solutions. That's a defining feature — see memory
  `[project_infolcd_client_only]`.
- **Backward compatibility.** Never break existing CustomData strings or save
  games. Add new config keys; don't remove or rename.
- **Performance first.** Cache subgrid scans, minimize LINQ per-tick, defensive
  parsing around DetailedInfo and type conversions.
- **Null safety.** Always check block/component existence before use.
- **Position-based screen layout.** Calculate remaining space from current draw
  position — don't hardcode layouts that break on different LCD sizes.

## Structure

- `Data/Scripts/SG/` — Text Surface Scripts (`MahLCDs_*.cs`) that inherit
  `MyTextSurfaceScriptBase` and get full game API access (no ingame API)
- `Data/AdditionalItems.ini` — config
- `metadata.mod`, `modinfo.sbmi`, `thumb.jpg` — Workshop metadata

## MUST NOT

- Modify vanilla SE game files (`steamapps/common/SpaceEngineers/`)
- Add server-side / SessionComponent code — kills the "client-only" selling point
- Break save-game compatibility without an explicit version bump
- Upload repo `Sandbox` files to a live SG server (wipes auto-pulled dependencies —
  see memory `[project_sg_core_dependencies]`)

## Related

- Sibling: [`gitpush-mod/se-infolcd-apex-advanced`](https://github.com/gitpush-mod/se-infolcd-apex-advanced)
- Historical cross-mod journal: `mods/MOD_MAKING_NOTES.md` in Chris's local tree
  (was formerly at this repo's root, moved out during the 2026-07-14 split)
- Parent when checked out under Chris's tree: `mods/AGENTS.md`
- Universal Golden Rules: top-level `AGENTS.md` in `VS Code Projects/`
