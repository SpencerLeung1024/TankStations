# Tank Stations — Player Guide

Salvaged gas and liquid tanks are finally worth hauling home. A Tank Station accepts part-full
tanks into its hopper and automatically pumps their contents into your ship's own tanks — and at
higher tiers, out of other ships entirely.

## Getting one

| Model | How to get it |
|-------|---------------|
| Ryokka "GX1" Mk I | Supplies kiosks (always stocked, 1–2 units, a couple grand). Also found on ~30% of derelicts. |
| Ryokka "GX9" Mk II | Supplies kiosks, ~30% of restocks (~$30k — check back after restock). Rarely on derelicts (~7%). |
| Ryokka "GX9+" Mk III | Not sold. NPC cargo haulers (Light Tugs, Mesa/Ibex/Ostrich hulls) carry one in the hold. Borrow it. |
| Corsair "Vampire" Mk IV | ~5% of Belt Pirate gunboats at Ceres (Vector3 / Babak / Pequod refits) carry one. Take it. |

## Using it

1. Install on clear floor (Mk I: 1x1; Mk II–IV: 3x3) and run a power conduit to any edge tile.
2. Open the hopper (Inventory) and drop in salvaged tanks. Mk I takes one O2 or N2 can;
   Mk II and up take up to nine gas cans or a single large He3 or D2O canister — the only
   practical way to move fusion fuel between ships.
3. Panel: OFF/AUTO/ON knob — the unit runs on AUTO or ON. SLOW = 10% flow. TURBO = 20x flow.
   REVERSE = fills the cans in the hopper FROM your ship's tanks.
4. Right-click → Dump Mode: sources keep draining even when your tanks are full, venting the
   excess into space. On Mk III/IV this is how you strip a target completely dry.
5. Filling stops at 99.9% of rated pressure — the station cannot burst your tanks. Cans feeding
   your RCS intakes are filled before other N2 destinations.
6. Mk III: also drains the tanks of any ship docked or moored to you (stations excluded).
7. Mk IV: also siphons whatever ship is currently selected (crosshair) on your nav map — no
   weapons lock needed. Full flow to 50 km, then flow falls off with distance (hard stop at
   500 km; all configurable). Drains N2, He3, and D2O. The orange nav label beside the target
   shows remaining kg and the live drain rate. A ship drained dry is kept from despawning so you
   can board and loot it (configurable) — boarding applies exactly what you took to its real
   tanks.

## Gotchas and weird behavior

- Stations only operate on ships you OWN. They keep running while you're aboard a derelict or
  inside a station shop, but a station installed on a ship you don't own won't run.
- The hopper is picky: O2 cans take O2, N2 cans take N2, and the big canisters take He3/D2O.
  Unmarked orange cans and CO2 cans are not accepted. A can holding a gas MIXTURE is skimmed —
  the gas the station wants is drawn out and the rest stays behind.
- A can drained to "empty" still has a whiff left (the station stops at a small floor).
- Loose stations keep their contents when uninstalled; damaged stations must be pried open.
- Siphoning a derelict you have never boarded uses the ship's template values: the random
  0–100% post-break-in fill and any combat-destroyed tanks are NOT accounted for, so you may
  pull more fuel than the realized ship would actually have had. There's a config checkbox
  ("Siphon Unvisited Derelicts") if that offends your inner bean counter.
- A ship on a plotted torch course may continue its burn even with dry tanks — NPC autopilot is
  its own beast and mobility kills against maneuvering (RCS) targets are the reliable case.
  NPCs do not react to being siphoned at range; they can't detect it.
- Clicking ships on the nav map sips a little fuel from each while a Mk IV is running. A ship
  only becomes a protected "drain victim" (kept from despawning) once it's meaningfully drained:
  RCS dry, torch dead, or any resource 90% stripped.
- Leaving an ATC region recycles that region's NPC ships (vanilla behavior) — a victim you
  leave behind may be replaced by a fresh, fully-fueled one when you return.
- Reverse + Dump together will vent YOUR ship's tanks into space. The station is not smart
  enough to stop you.
- Idle draw is ~100 W; pumping draws the full rated power. There is no true 0 W state short of
  switching the circuit off — same as vanilla towing braces.
- Everything persists through save/load: switch states, dump mode, drain ledgers on victims.

## Notes for fellow modders / compatibility

- Plain data-pack JSON + BepInEx plugin; loads via the community OstranautsWorkshopBepInExBridge
  (same architecture as Testudo Safe Pump / Ship's Water). No FFU dependency.
- Ship's Water tanks are currently NOT siphoned and NOT accepted in hoppers (soft dependency is
  designed but not implemented in this version).
- Nav-map label pattern courtesy of OrbitMarkers (EddieSM); kiosk-injection and derelict-spawn
  patterns courtesy of Testudo Safe Pump / Ship's Water (Valtorra). Used with permission.
- Balance knobs (per-tier flow rates, spawn chances, ranges, derelict siphoning, drain-victim
  protection, the nav label, verbose logging) are all in the BepInEx config.
