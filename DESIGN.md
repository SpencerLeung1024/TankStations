# Tank Stations Mod — Design & Implementation Handoff

> **Purpose of this file:** complete briefing for the implementation session. Everything needed
> is here or in cited files. Written after deep exploration of the decompiled game code
> (`Assembly-CSharp/`, ILSpy output), vanilla data (`Ostranauts_Data/StreamingAssets/data/`),
> and three reference mods (decompiled sources at game root: `TestudoSafePump/`, `ShipsWater/`,
> `OrbitMarkers/`; workshop packs `3757331189/`, `3768554122/`, `3768492887/`).
>
> **All paths below are relative to the game root:**
> `/mnt/c/Program Files (x86)/Steam/steamapps/common/Ostranauts/`

---

## 0. Layout (updated 2026-08-06, session 2 — consolidated)

There is now only ONE `TankStations` folder: **`Ostranauts_Data/Mods/TankStations`** — it is
simultaneously (a) the dev root (this DESIGN.md, `src/` with .csproj + .cs files), (b) the live
data mod (in `Mods/loading_order.json` already), (c) the future workshop pack, and (d) a git repo
for rollback. The old game-root `Ostranauts/TankStations` folder is gone.

**Verified what reads what (game 1.0.0.7 code + decompiled bridge preloader):**
- Game (`DataHandler.LoadMod`): reads ONLY `mod_info.json`, `preview.png` (GUIModRow/workshop),
  `*.json` under the ~90 hardcoded `data/<subfolder>/` paths (**recursive** — `GetFiles("*.
  json", AllDirectories)`), and `images/` by exact filename / `LoadPNGFolder` `*.png` globs.
  `.md`/`.cs`/`.csproj`/`bin`/`obj`/`Properties`/`.git` anywhere else are invisible to it.
- Bridge preloader (`OstranautsWorkshopBepInExBridge.Preloader`): syncs `<mod>/BepInEx/{plugins,
  patchers,config}/**` (ALL file types, recursive) into live `BepInEx/.../Workshop/<id>/` — but
  ONLY for workshop item folders (`IsWorkshopItemPath` filter on loading_order entries).
  **Local mods' `BepInEx/` folders are never synced** — so for dev, the built DLL must be copied
  to the LIVE `BepInEx/plugins/TankStations/` (game root). The mod-folder `BepInEx/` subtree
  only matters for workshop subscribers; keep it limited to `plugins/TankStations/TankStations.dll`.
- In-game workshop upload: `SteamUGC.SetItemContent(<whole mod folder>)` — uploads EVERYTHING
  recursively, incl. `.git`, `src/`, `bin/`. **Publish from a clean staging copy**, not the repo.

Rules: never leave stray `.json` under `data/` (build output incl. `obj/project.assets.json`
stays under `src/`); never put sources/build output under the mod's `BepInEx/`.

dotnet on WSL (user-installed) is available for builds; run-time testing still requires the user
to start Ostranauts and follow steps:

```
spenc@SPENCER-LAPTOP3:/mnt/c/Program Files (x86)/Steam/steamapps/common/Ostranauts$ dotnet --info
.NET SDK:
 Version:           10.0.110
 Commit:            f7d90799ce
 Workload version:  10.0.100-manifests.1641d827
 MSBuild version:   18.0.11+f7d90799c

Runtime Environment:
 OS Name:     ubuntu
 OS Version:  26.04
 OS Platform: Linux
 RID:         ubuntu.26.04-x64
 Base Path:   /usr/lib/dotnet/sdk/10.0.110/
```

---

## 1. Mod concept (final, per user)

A device that accepts salvaged part-full gas/liquid tanks into an internal inventory and
automatically transfers their contents into the ship's own tanks. Four tiers:

| Tier | Footprint | Internal inv | Flow | Accepts | Extra behavior | Acquisition |
|------|-----------|--------------|------|---------|----------------|-------------|
| T1 | 1x1 | 1x1 | 5 L/s | one O2 or N2 RTA can | — | supplies kiosk, couple $1,000s; ~30% derelict spawn |
| T2 | 3x3 | 3x3 | 100 L/s | one He3 or D2O canister, or up to 9 O2/N2 cans | — | supplies kiosk, $10,000s, sometimes; few-% derelict spawn |
| T3 | 3x3 | 3x3 | 200 L/s | same as T2 | also drains tanks on a docked (derelict) ship | only "borrowed" from NPC hauler ships |
| T4 | 3x3 | 3x3 | 300 L/s | same as T2 | also drains tanks on the target-locked ship at range; full flow to 50 km, falloff beyond | rare spawn on pirate ships at Ceres ("COHO") |

Stock-game context: O2/N2 can already be moved can→can with air pumps (tedious); He3
(`StatSolidHe3`) and D2O (`StatLiqD2O`) have NO vanilla transfer path — stuck until installed
and burned. This mod fills that gap.

**Constraints (user decisions):**
- Target: Steam Workshop release. **NO FFU dependencies** (no `--ADD--` shorthands).
  Plain data JSON pack + BepInEx plugin DLL only (same architecture as Testudo Safe Pump /
  Ship's Water, which load via the community `OstranautsWorkshopBepInExBridge` preloader).
- Would like to also steal `StatLiqH2O` (Ship's Water mod) — must be a **soft dependency**,
  handled in C# only, guarded by runtime existence checks. See §10.
- Mode switches only for v1 UI (no custom GUI). Two toggles: On/Off and "Dump From Target"
  (wasteful drain) On/Off. Optional later: nav-map label (§11).

---

## 2. Final architecture (user-specified — implement as written)

```
TankStation.Run()  [called on a real-time timer from Plugin.Update, every ~1-2 s; use
                    StarSystem.fEpoch deltas for game-time flow; clamp catch-up like SafePump]
  per installed station CO (found via GetICOs1 on TIsTankStationInstalled CTs):
  - If Off (missing IsTankStationRunning): skip.
  - If srcTanks/dstTanks empty or cache older than N seconds: rescan.
      * NO event hooks for v1 (user decision; revisit if buggy).
      * srcTanks = station contents (co.GetCOsSafe(false)) filtered to accepted vessel CTs.
        Tier>=3: + tanks of docked ships. Tier 4: + shallow-target pseudo-sources (§6).
      * dstTanks = player ship's tanks, EXCLUDING anything whose objCOParent chain includes
        the station (GetICOs1 with bSubObjects:true recurses into containers — would find
        the station's own contents!).
  - Sum NeededO2/N2/He3/D2O over dstTanks (max - current each).
  - If "Dump From Target" on: force NeededN2/He3/D2O = huge (not O2 — NPC ships never have
    O2 at RCS intakes; dumping O2 is pointless).
  - Divide station flow rate between srcTanks; skip resources not needed and nearly-empty
    sources. Ideally prioritize the resource the NPC is currently using to flee (torch vs
    RCS — see §6 note; if not detectable, spread flow evenly).
      * Treat a shallow target's total N2 volume as
        (fShallowRCSRemassMax / kgN2PerCan) * volumeOfOneCan for rate semantics.
  - Per srcTank: remove its proportional share, clamped by needed amount.
      * Tier 4 shallow target: increment ledger conds fShallowDrained* on target ShipCO,
        apply IsTankDrainVictim, adjust fShallowRCSRemass / fShallowFusionRemain (§6).
  - Add looted O2/N2/He3/D2O to dstTanks in order, filling each to max (gases: 99.9% of
    StatGasPressureMax to avoid burst). Leftover at the end is wasted (vented).
Dock reconciliation: when a drained ship fully loads, subtract ledger amounts from realized
  tanks (Harmony postfix on Ship.InitShip — §7).
```

---

## 3. Key game-code facts (verified, with citations)

### 3.1 Ship load states
- `Ship.Loaded` enum (`Ship.cs:31-37`): `None < Shallow < Edit < Full`. `ship.LoadState` readonly.
- **Shallow = no items exist.** `json.aItems` instantiated only at Edit+ (`Ship.cs:1676-1678`).
  `GetRCSCans()` returns empty when shallow (`Ship.cs:7617-7619`). Fuel state is exactly:
  `fShallowRCSRemass` (kg N2), `fShallowRCSRemassMax`, `fShallowFusionRemain` (seconds of
  full-power torch burn), `fShallowMass`. **O2 has NO shallow representation.**
- Shallow fields are **baked into template JSONs** (`data/ships/*.json`, e.g. `Mesa.json`),
  computed from the realized ship at editor-save time by `Ship.GetJSON()` (`Ship.cs:8365-8385`);
  copied verbatim on spawn (`Ship.cs:1588-1590`). If max==0 → set to remain (`7581-7585`).
- `Ship.GetJSON` also stores `json.fBreakInMultiplier`, `aDocked`, etc.
- Derelicts spawn Shallow (`StarSystem.AddDerelict` `StarSystem.cs:1234`); first docking
  full-loads them: `CrewSim.DockShip` (`CrewSim.cs:4086`) → `SpawnShip(regID, Full)` (`:4096`)
  → `InitShip(bTemplateOnly:true, Full)` (`Ship.cs:1432`) → `bPrefill` → `DoLootSpawners`
  (`Ship.cs:1893,2209`) + `BreakIn()` (`Ship.cs:1948,3191`) — the randomized damage/loot pass
  (uses global `UnityEngine.Random` → savescummable). BreakIn randomizes: He3/D2O tanks
  keep uniform-random 0-100% (`Ship.cs:3257-3264`); gas cans drained random fraction via
  `GasContainer.BreakIn()` (`Ship.cs:3421-3429`, `GasContainer.cs:257-288`).
- **A shallow ship's inventory is still computable from data**: `ship.json.aItems`
  (`JsonItem[]`: `strName`, `fX/fY`, `strParentID`, `aCondOverrides` [only StatDamage]) +
  `DataHandler.dictCOs[strName].aStartingConds` (`"StatGasMolO2=1.0x13373.0"` strings).
  For previously-visited-then-unloaded ships, exact per-CO state is in
  `DataHandler.dictCOSaves` keyed by CO `strID`. Loot-spawner contents NOT in aItems.

### 3.2 NPC fuel & the despawn path
- All AI pilots call `ship.AIRefuel()` at spawn (e.g. `ScavPilot.cs:240`, `PiratePilot.cs:203`)
  → `fShallowRCSRemass = GetRCSMax()` (`Ship.cs:7641-7644`).
- Shallow RCS burn decrements `fShallowRCSRemass` only, ×0.75 for AI (`Ship.cs:7176-7184`).
  Empty → `Maneuver` → `StopManeuver` (`Ship.cs:7285-7291`). Torch burn: `NavData.TimeAdvance`
  sets `fShallowFusionRemain` (`NavData.cs:163`).
- On full-load, cans spawn FULL from defs, then `Ship.SyncFuel()` (`Ship.cs:2632-2639`)
  drains real cans DOWN to `fShallowRCSRemass` (never up). Called delayed after dock/moor
  (`CrewSim.cs:4041,4123,4524-4533`). **No fusion equivalent** → He3/D2O edits on shallow
  ships do NOT persist to realized tanks without our ledger (§7).
- Despawn chain: `FlyTo.IsOutOfFuelApproximation` (`FlyTo.cs:122`) → `RequestHelp()`
  (`FlyTo.cs:160-188`): Pirate/Hauler*/Navy/Civilian → `AIShipManager.UnregisterShip(ship)`
  immediately; Scav within 40-120 km of player → `SHIPSosAskPlayer` comms (stays), else
  `SHIPSosStranded` + unregister. Same in `SosOutOfFuel.RunCommand` (`SosOutOfFuel.cs:12-41`).
  - `UnregisterShip(Ship)` public static (`AIShipManager.cs:1322`) → private (`:1330`):
    removes AI, stamps `ShipCO.IsStale = fEpoch`. Actual deletion: `StarSystem
    .CleanupStaleShips` (`StarSystem.cs:~1096`) — needs IsStale 3h+ old, unvisited 3h+,
    shallow, undocked, no player/plot people. `FlyTo.TryInstantCleanup` (`FlyTo.cs:261-284`)
    backdates IsStale by 10800 for far unvisited ships.
  - **PATCH POINT (chosen):** Harmony Prefix on `AIShipManager.UnregisterShip(Ship)` —
    `if (ship.ShipCO.HasCond("IsTankDrainVictim")) return false;`. One choke point covers
    all command-driven fuel despawns. Ship then sits inert (FlyTo re-fails every 64 s —
    harmless). Provide config toggle; clear cond to release ship to normal cleanup.
  - ShipCO conditions persist while shallow — precedent: `IsStale` carried through
    unload/reload in `CrewSim.DestroyAndReload` (`CrewSim.cs:4721-4747`). **VERIFY early**
    (§13 test list) that custom conds on ShipCO survive game save/load; if not, carry them
    with a small patch mirroring the IsStale handling.

### 3.3 How the refuel kiosk finds tanks (`GUIStationRefuel.cs`)
- He3: CT `TIsCanisterLHe02Installed` (IsVesselHe3+IsInstalled; `condtrigs.json:692`),
  `GetICOs1(ct, bSubObjects:true, bAllowDocked:false, bAllowLocked:true)` (`:282-298`);
  capacity kg = `StatVolume × 129.11` (`DENSITY_HE3`); contents = `StatSolidHe3` kg.
  D2O: `TIsCanisterLH02Installed` (`:688`), ×1107, `StatLiqD2O`.
- O2: `TIsRTAO2Installed` (IsVesselO2+IsRTA+IsInstalled; `:2284`), same GetICOs1 pattern;
  ideal-gas amounts: max kg = `StatVolume × StatGasPressureMax / R / StatGasTemp × molMass`,
  current scaled by `StatGasPressure/StatGasPressureMax`; fill `GasContainer.AddGasMols`.
- N2 (life support): installed air pumps (`TIsAirPump02Installed`, `:377`) → their
  `mapPoints` keys containing "GasInput" → `GetCOsAtWorldCoords1` raycast with
  `TIsRTAN2Installed` (`:2273`), bAllowDocked:true (`GUIStationRefuel.cs:386-416`).
- RCS N2: `ship.GetRCSRemain()/GetRCSMax()/GetRCSCans()` (`Ship.cs:7538-7639`): scan
  `aRCSDistros` (installed RCS intake regulators) GasInput01..04 points with CT
  **`TIsRCSValidInput` = requires IsAirtight, forbids IsHuman/IsSystem (`condtrigs.json:2219`)
  — NO IsInstalled requirement.** That's why loose cans under RCS intakes work and are
  kiosk-fillable. RCS thrust drains the whole gas mixture proportionally
  (`GasContainer.RemoveGasMass`, `GasContainer.cs:603`).
- `bAllowLocked` in GetICOs1 = recurse into `IsLocked` containers; in
  `GetCOsAtWorldCoords1` the param is vestigial (unused).
- Gas price table: `GasContainer.GetGasPrice` (`GasContainer.cs:83-108`), loot table
  `GasPrices` (`loot.json:12474`).

### 3.4 Gas/liquid APIs
- `GasContainer` (`GasContainer.cs`): state lives in CO conds (`StatGasMol<X>`, total,
  derived `StatGasPressure`/`StatGasPp<X>`). `AddGasMols(strGas, double mols, run:true)`
  (`:290`) — negative removes; `Run()` applies ideal gas law P=nRT/V (`:397`). Species
  (`FluidStrings.cs:19-23`): CH4, CO2, H2, H2O, H2SO4, He2, N2, NH3, O2, CO, Smoke.
  R = 0.008314000442624092 kPa·m³/(mol·K). Molar masses (`GetGasMass` `:895`):
  N2 0.0280134, O2 0.0319988 kg/mol.
  **Overpressure: |ΔP| > 150 kPa over StatGasPressureMax → burst/shrapnel
  (`CheckPressureDifference` `:492-555`). Clamp fills ≤ 99.9% of max (Testudo uses 99%).**
- Liquids: plain `co.AddCondAmount("StatSolidHe3"/"StatLiqD2O", ±kg)`; capacity
  `StatVolume × 129.11 / × 1107` kg.
- Vanilla pump rate reference: gasrespire `AirPump02` `fVol=0.00193` m³/cycle
  (`gasrespires.json:252`, `GasPump.Pump` `GasPump.cs:201-369`). "L/s" for gases = swept
  source volume: mols = flowLiters/1000 × P_source/(R×T_source) per second.

### 3.5 Fusion bookkeeping (for Tier 4 shallow He3/D2O)
- `FusionIC.cs`: reactants `{"StatLiqD2O","StatSolidHe3"}` ratio `{0.667, 1.0}` (`:101-103`).
  Full-burn mass constant `num8 = 6.9999e11 × (StatICVe/7.05e7) / 7.05e7² × 393.06358381502895
  × fPelletMax` (`:827`); `fPelletMax` from reactor hardware, stored as reactor cond
  `StatICPellMax` (`:637`). Burn rates: D2O `num8×0.667` kg/s, He3 `num8×1.0` kg/s.
  `fShallowFusionRemain = min(D2Okg/(num8×0.667), He3kg/(num8×1.0))` (`:864-901`).
  Inverse: `SetReactants(seconds)` (`:942-981`) / `RemoveFromTanks` (`:983`, caps at
  StatVolume×density).
- **Linear in kg.** Mod shortcut (no torch curves): per template, limiting reactant kg/s =
  templateKg / bakedFShallowFusionRemain (templates saved full). Compute both
  `D2O/0.667` vs `He3/1.0` to find limiter; other resource via fixed 0.667:1.0 ratio.
  Keep per-resource kg ledger on ShipCO; when draining shallow target, also decrement
  `fShallowFusionRemain` by kg/(kgPerSec) so AI loses torch immediately.

### 3.6 Ships, docking, targets, distance
- `Ship.aDocked` dict portID→Ship (mooring keys prefix `"MP|"`). `GetDockedShips()`
  (`:8067`) direct only; `GetAllDockedShips()` (`:6935`) transitive (cached). Docking
  full-loads the target (§3.1) — docked ships always have real tanks.
- Static event `Ship.OnDock` (`Ship.cs:91`, invoked `:6210`).
- Global live-item registry: `CrewSim.objInstance.coDicts` (`CrewSim.cs:760`); ships
  register every ICO on load/spawn (`Ship.cs:4249`) and remove on unload/destroy
  (`Ship.cs:4756`, `CondOwner.cs:1632`). Mod-subscribable:
  `CODicts.COAddEvents`/`CORemoveEvents` dictionaries keyed by CO `strName`
  (`CODicts.cs:8-10,80-109`). (v1 skips hooks; here if needed.)
- Target lock: `GUIOrbitDraw.CrossHairTarget.Ship` (static; set via nav console);
  `ship.shipScanTarget` set on course plot (`NavModCoursePlot.cs:321`);
  `ship.shipCombatTarget` = weapons lock.
- Positions AU. `myShip.objSS.GetDistance(target.objSS)` AU; ×149597870 = km
  (`CrewSim.KM_PER_AU`, `CrewSim.cs:66-70`). 50 km = 3.342293553032505E-07 AU.
- `StarSystem.GetAllLoadedShips()` (`StarSystem.cs:1902`) = ALL ships any state (misnomer);
  `CrewSim.GetAllLoadedShips()` = Edit+ only.
- T4 target detection of NPC escape mode (torch vs RCS): investigate
  `target.bFusionReactorRunning` (stale for shallow ships) and `target.objSS.NavData`
  torch waypoints. If inconclusive, split flow evenly (user-approved fallback).

### 3.7 Vanilla data templates to copy (line refs in `StreamingAssets/data/`)
- `condowners/condowners.json`: `ItmRTAO2` :10363, `ItmRTAN2` :10135 (StatGasMolN2/O2
  13373, StatGasPressureMax 41400, StatVolume 0.787, StatGasTemp 293), `ItmCanisterLHe02`
  :7661 (StatSolidHe3 5216, Vol 40.4), `ItmCanisterLH02` :7562 (StatLiqD2O 44722.8),
  `ItmAirPump02Off` :1686 / `ItmAirPump02OnG` :1782, `ItmRCSDistro01` :25396,
  `ItmKioskSupplies02` :17311, `ItmCrate01` :10966 (container CT + nContainerWidth/Height),
  `SysLootSpawner` :35113.
- `condtrigs/condtrigs.json`: TIsAirPump02Installed :377, TIsAirtightShipCan :386,
  TIsCanisterLH02Installed :688, TIsCanisterLHe02Installed :692, TIsRCSValidInput :2219,
  TIsRTAN2Installed :2273, TIsRTAO2Installed :2284. Schema: `aReqs`, `aForbids`,
  `aTriggers` (OR'd nested), `bAND`, `strCondName`+`fCount` for stat-delta triggers.
- `loot/loot.json`: GasPrices :12474, ItmOKLGSupplyKioskInv :16627 (supplies kiosk stock),
  RandomPirateShip :27650, RandomHaulerShip :27644, RandomDerelict :27286.
  Entry syntax: `"Name=chance x min[-max]"` per-string independent roll; `|`-separated =
  single weighted pick. strType "item"/"ship"/"trigger"/"condition"/"text".
- `guipropmaps/guipropmaps.json`: TraderOKLGSupplyKiosk :2707 (kiosk Trader GPM keys:
  strLoot/strRestockCond/strTicker/strLootCTsBuy/Sell/discounts), AirPump :3,
  RefuelKiosk :1295.
- `star_systems/star_system.json`: COHO (Corsair's Hollow, Ceres asteroid field, owner
  BeltPirates) :3100-3127; BCER (Port Mojave) :3129 — note BCER has NO StationMinPirate/
  MaxPirate conds (cf. OKLG :3355), so tension-pirates don't spawn fresh at Ceres; Ceres
  pirates come via `AIShipManager.SpawnAI(AIType.Pirate, "COHO")` (BeatManager.cs:576)
  and region logic.
- `items/items.json`: footprint = `nCols` + socket arrays (`TILFloor` reqs, `TILObstruction`
  forbids, `TILItemForbids` center); 1x1 example :514 (ItmAirPump02Off), 3x3 :4633.
- `installables/installables.json`: e.g. RTAO2Install :819 — strActionCO,
  strInteractionTemplate (ACTInstallTEMP/ACTUninstallTEMP/ACTRepairTEMP/ACTUndamageTEMP/
  ACTDismantleTEMP), CTThem, aInputs, aToolCTsUse, aLootCOs, strStartInstall, strBuildType
  (HVAC/APPS), strJobType, strAllowLootCTsUs/Them, strProgressStat.
- `powerinfos/powerinfos.json`: AirPump02 :39 — aInputPts, fAmount, strIntPowerOn/Off
  (mode-switch interaction names), strShutDownCT, strUsePowerCT, bAllowExtPower.
- Interactions for toggles: copy Testudo's `3768554122/data/interactions/
  interactions_testudo.json` (`MSSafePumpTestudoPowerOn/Off`) and vanilla
  `MSAirPump02OnAllow`-style mode switches.
- Kiosk sell filter: item conds must pass `TIsBarterOKLGSupplyKioskSell` etc.; give stations
  `StatBasePrice` (T1 ~2000-3000; T2 ~30000).

---

## 4. Reference-mod patterns (copy these)

Decompiled sources at game root — READ THEM FIRST:
- `TestudoSafePump/TestudoSafePump/`: **Plugin.cs** (BepInPlugin, Config.Bind, Harmony
  CreateAndPatchAll, Update-timer → Run), **SafePump.cs** (the transfer loop: epoch delta,
  GetICOs1 for cabinets, GetCOsSafe for contents, GetCOsAtWorldCoords1 socket scan,
  AddGasMols with 99% clamp, self-heal patterns, status logging), **KioskStock.cs**
  (DataHandler.LoadComplete += Inject; append `"ItmX=1.0x2"` to `DataHandler.dictLoot[pool]
  .aLoots` for 11 pools — pool names list inside), **Patch_Ship_InitShip.cs** (Postfix on
  `Ship.InitShip`; guard `IsDerelict() && aRooms.Count>0 && !playerShip`; deterministic
  FNV-1a(regID) roll; floor scan; `DataHandler.GetCondOwner(name,null,null,bLoot:false)`;
  position `co.tf.position`; `ship.AddCO(co, bTiles:true)`; wear via StatDamage).
- `ShipsWater/ShipsWater/`: Plumbing.cs (3s timer moving liquid conds between installed
  tanks), KioskPatch.cs (Harmony on GUIStationRefuel adding a refuel row — shows how to
  extend that UI if ever wanted), Patch_ModeSwitch_KeepFountainWater.cs, IWaterTank.cs.
- `OrbitMarkers/OrbitMarkers/`: nav-map drawing — see §11.

mod_info.json schema (array of one object): `strName, strAuthor, strModURL, strGameVersion
("0.15.1.20"), strModVersion, strNotes (BBCODE ok), strWorkshopID`.

Data-pack conventions: each `data/<folder>/*.json` is an **array of objects keyed by
strName**; later loads in `Ostranauts_Data/Mods/loading_order.json` (`aLoadOrder`,
`"core"` = vanilla StreamingAssets) overwrite same-named entries wholesale
(`DataHandler.JsonToData` `DataHandler.cs:1252-1294`). Recognized folders include:
conditions, conditions_simple, condowners, condtrigs, guipropmaps, installables,
interactions, items, loot, powerinfos, ships, strings, tickers, slots, slot_effects, etc.
Images in `images/*.png`.

---

## 5. Files to create

Dev root = the mod folder itself: `Ostranauts_Data/Mods/TankStations/` (git repo).

```
Ostranauts_Data/Mods/TankStations/
  DESIGN.md                          (this file)
  .gitignore                         (src/bin/, src/obj/)
  mod_info.json                      (exists; fill strWorkshopID on first upload)
  preview.png                        (TODO before workshop upload)
  src/TankStations.csproj            (netstandard2.1; refs w/ HintPaths — §9)
  src/Plugin.cs                      (BepInPlugin "com.<author>.tankstations"; Config entries:
                                      per-tier flow L/s, kiosk chances, derelict chances,
                                      T4 max range/falloff, debug logging; Update timer)
  src/TankStation.cs                 (Run loop per §2: scans, Needed* sums, transfer)
  src/ShallowFuel.cs                 (template inventory calc from json.aItems/dictCOSaves;
                                      kg/s conversion for fusion; ledger read/write on ShipCO)
  src/KioskStock.cs                  (near-verbatim Testudo copy; T1 always, T2 chance)
  src/Patch_Ship_InitShip.cs         (derelict T1 30% / T2 ~3% deterministic FNV rolls;
                                      T3 on haulers; T4 on COHO pirates; ALSO applies drain
                                      ledger to freshly loaded ships — §7)
  src/Patch_UnregisterShip.cs        (Prefix AIShipManager.UnregisterShip(Ship); skip if
                                      ShipCO.HasCond("IsTankDrainVictim"))
  src/NavLabel.cs                    (OPTIONAL later — §11)
  BepInEx/plugins/TankStations/TankStations.dll   (PUBLISH-TIME ONLY; dev uses live BepInEx)
  data/conditions_simple/conditions_simple_tankstations.json
  data/conditions/conditions_tankstations.json     (stat conds: StatTankDrainO2/N2/He3/D2O
                                                    w/ bPersists; friendly names hidden)
  data/condowners/condowners_tankstations.json     (T1..T4 + Loose + Dmg variants)
  data/condtrigs/condtrigs_tankstations.json
  data/items/items_tankstations.json               (footprints 1x1 / 3x3)
  data/installables/installables_tankstations.json (install/uninstall/repair/undamage/
                                                    dismantle per tier)
  data/interactions/interactions_tankstations.json (Inventory + MSTankStationOn/Off +
                                                    MSTankStationDumpOn/Off mode switches)
  data/guipropmaps/guipropmaps_tankstations.json   (optional; stock GUIOnOff if used)
  data/powerinfos/powerinfos_tankstations.json     (per tier, fAmount scaled)
  data/loot/loot_tankstations.json                 (per-item spawn tables for kiosk
                                                    injection + dismantle yields)
  images/*.png                                     (placeholders: recolor vanilla art)
```

**New conditions (our pack defines; safe without other mods):** `IsTankStation`,
`IsTankStationT1..T4`, `IsTankStationRunning`, `IsTankStationDump`, `IsTankDrainVictim`,
`StatTankDrainO2/N2/He3/D2O` (kg ledger on victim ShipCO), maybe `StatTankStationFlow`
for config display.

**New CTs:** `TIsTankStationT1Installed`…`T4` (+ `TIsTankStationInstalled` OR-wrapper via
aTriggers), container-fit CTs: `TIsFitTankStationT1` (IsRTA + IsVesselO2/N2 via aTriggers),
`TIsFitTankStationT2` (adds IsVesselHe3/H2 canisters; forbid IsDamaged? decide).

**Condowner per tier** (model on ItmSafePumpTestudo): conds IsInstalled/IsContainer/
IsTankStationTx/IsSolid/IsMechanical/IsCategoryHVAC/IsSalvageValueHigh(T2+)/StatBasePrice/
StatMass/StatDamageMax/Stat*ProgressMax; `strContainerCT` + `nContainerWidth/Height` (1 or 3);
`aInteractions`: Inventory, MSTankStation* ; `aTickers:["Power"]`; `jsonPI` link;
`aUpdateCommands`: Destructable + Electrical; power `mapPoints` (PowerA… multi-edge like
Testudo's 8-point); `mapGUIPropMaps` Electrical (+ Panel A if using GUIOnOff). Loose variant
drops IsInstalled, adds IsCumbersome/IsOversized + PickupItem/DropItem; Dmg variants.

---

## 6. Tier-4 shallow-target handling (detail)

When target `LoadState <= Shallow` (normal at range):
- **Compute its full inventory once per target** (cache by regID): enumerate
  `target.json.aItems` → `dictCOs[strName].aStartingConds` → sum StatGasMolO2×0.0319988,
  StatGasMolN2×0.0280134, StatSolidHe3, StatLiqD2O. Prefer `DataHandler.dictCOSaves` values
  for previously-loaded ships. N2 cross-check: `fShallowRCSRemassMax`.
- **Ledger conds on `target.ShipCO`**: StatTankDrain* kg per resource + IsTankDrainVictim.
  Current contents = template/computed totals − ledger (for N2 also live-check
  fShallowRCSRemass; use min of the two views).
- **N2 drain**: decrement `target.fShallowRCSRemass` directly (kg). AI stops thrusting at 0
  (`Ship.cs:7287-7291`) → out-of-fuel AI → our UnregisterShip prefix keeps it alive (§3.2).
- **He3/D2O drain**: decrement `target.fShallowFusionRemain` by kg/(kgPerSec for that
  resource, from §3.5 template-derived rate). Take min-consistency: after update, clamp
  fShallowFusionRemain ≤ min over remaining resources of seconds-equivalent.
- **O2 drain**: ledger only (no shallow field exists) — applied at load (§7). User accepts.
- **Crediting our ship**: add kg to dstTanks per §2 (gases via AddGasMols with 99.9% clamp;
  liquids via AddCondAmount capped at StatVolume×density).
- **Flow falloff**: full flow ≤ 50 km; beyond, scale (e.g. `min(1, 50/distKm)`); config.
- Cache target inventory; invalidate when target changes or after dock events.

## 7. Dock reconciliation (drained ship loads)

`Patch_Ship_InitShip` Postfix — for EVERY ship load (not just derelicts):
1. If `__instance.ShipCO` has StatTankDrain* > 0 and ship just reached Edit+ (check
   `LoadState`/aRooms like Testudo patch): subtract ledger kg from realized tanks
   (He3: GetICOs1 TIsCanisterLHe02Installed → AddCondAmount negative, floor 0 per can,
   walk cans until satisfied; D2O same; O2/N2: GetICOs1 TIsRTAO2/N2Installed + GetRCSCans()
   → GasContainer.AddGasMols negative / RemoveGasMass).
2. Zero the ledger conds afterwards (keep IsTankDrainVictim until user releases or ship
   destroyed — decide; simplest: keep, it only blocks AI-despawn).
3. Note ordering: vanilla `SyncFuel` runs delayed AFTER dock (CrewSim.cs:4123) and drains
   cans toward fShallowRCSRemass — our N2 ledger and SyncFuel both drain; ensure
   idempotence (drain to min(ledger view, shallow view), not double-subtract). Test with
   partially-drained-then-boarded ships.

## 8. Acquisition implementation

- **Kiosk (T1/T2)**: `KioskStock.Install()` on `DataHandler.LoadComplete` (guard
  `DataHandler.bLoaded` for immediate inject — see Testudo source). Append e.g.
  `"ItmTankStationT1Loose=1.0x1-2"` and `"ItmTankStationT2Loose=0.25x1"` to pools:
  ItmOKLGSupplyKioskInv, ItmSupplyKioskBCERInv, ItmSupplyKioskBCRSInv, ItmSupplyKioskInv,
  ItmOKLGFurnishingsKioskInv, ItmFurnishingsKioskBCER/BCRSInv, ItmTraderSanDiegoKangInv,
  ItmTraderSanDiegoTSDOInv, ItmTraderBCERKioskTSDOInv, ItmTraderBCRSKioskTSDOInv.
  (No FFU `--ADD--`; this C# append IS the no-FFU mechanism.)
- **Derelicts (T1 30%, T2 ~3%)**: in Patch_Ship_InitShip, `IsDerelict()` branch,
  deterministic `Fnv1a(regID + "|tankstationT1")` roll (savescum-proof, matches reference
  mods), floor-scan, spawn Loose variant, random wear; Testudo's Spawn/FindSpot code is
  directly adaptable.
- **T3 on haulers**: options (pick at impl time): (a) postfix on hauler pilot creation
  (`HaulerCargoPilot`/`HaulerRetriever/Deployer` CreateAIShip, or
  `AIShipManager.SpawnHauler` AIShipManager.cs:297 / CheckTradeRoutes :502); (b) InitShip
  postfix keyed on template name ∈ {MesaCargo, IbexCargo, Ostrich A8R, Ostrich A4R,
  Light Tug…} (from RandomHaulerShip/Regional*CargoShip loot tables, loot.json:27644+).
  Place inside hold. High or 100% chance — T3 is "only obtained by borrowing".
  Optional: crime/faction consequences via data/crime (stretch goal).
- **T4 at Ceres pirates**: postfix where atc=="COHO": `AIShipManager.SpawnAI(AIType.Pirate,
  "COHO")` path (BeatManager.cs:576 → AIShip.cs SpawnNewShip :342, faction "COHOPirates",
  templates RandomPirateShip loot.json:27650) OR InitShip postfix matching pirate template
  names + owner/faction contains "COHO". Rare chance (few %), config.

## 9. Build & deploy

- `dotnet` SDK: installed in WSL (10.0.110; fine for netstandard2.1 target).
- csproj lives at `Ostranauts_Data/Mods/TankStations/src/TankStations.csproj` — game root is
  `../../../../` from there (verified DLL names):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>TankStations</AssemblyName>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>latest</LangVersion>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Assembly-CSharp"><HintPath>../../../../Ostranauts_Data/Managed/Assembly-CSharp.dll</HintPath></Reference>
    <Reference Include="UnityEngine.CoreModule"><HintPath>../../../../Ostranauts_Data/Managed/UnityEngine.CoreModule.dll</HintPath></Reference>
    <!-- only if/when needed: UnityEngine.UI, Unity.TextMeshPro, Vectrosity (same folder) -->
    <Reference Include="0Harmony"><HintPath>../../../../BepInEx/core/0Harmony.dll</HintPath></Reference>
    <Reference Include="BepInEx"><HintPath>../../../../BepInEx/core/BepInEx.dll</HintPath></Reference>
  </ItemGroup>
</Project>
```
- Dev loop: `dotnet build -c Release src/` → copy
  `src/bin/Release/netstandard2.1/TankStations.dll` to LIVE `BepInEx/plugins/TankStations/`
  (game root — the bridge does NOT sync local mods, §0). JSON/data edits need no copy step:
  the mod folder IS the live mod (`Mods/loading_order.json` entry already present).
  Then relaunch game → check `BepInEx/LogOutput.log`. No hot reload.
- Release: user manually removes `.git/`, `src/`, `bin/` from a copy of the folder before the
  in-game workshop upload (the uploader ships the entire folder recursively). Subscribers'
  bridge preloader mirrors the pack's `BepInEx/plugins/...` into their live plugins.

## 10. Ship's Water (StatLiqH2O) soft dependency

- NEVER reference StatLiqH2O/IsVesselWater/TIsWaterVesselInstalled in our JSON pack —
  undefined cond names in data could error on installs without the mod.
- C#-only: at `DataHandler.LoadComplete`, check `DataHandler.GetCond("StatLiqH2O") != null`
  and `DataHandler.GetCondTrigger("TIsWaterVesselInstalled") != null`; set static bool
  `WaterModPresent`. All water code paths guarded. If present: T2+ stations accept water
  tanks (add `IsVesselWater` handling; capacity `StatVolume×1000` L per ShipsWater
  Plumbing/WaterMath — verify in `ShipsWater/ShipsWater/WaterMath.cs`), steal from docked/
  targeted ships same as He3/D2O (ledger cond StatTankDrainH2O — ours, always defined).
- Ship's Water tanks' water is plain cond `StatLiqH2O` (kg≈L) — transfer via AddCondAmount.
- Their refuel-kiosk row patch (KioskPatch.cs) is a GUIStationRefuel Harmony example if
  ever needed.

## 11. Optional nav-map label (Tier 4 UX)

Pattern from OrbitMarkers (decompiled at `OrbitMarkers/OrbitMarkers/`):
- `[HarmonyPatch(typeof(GUIOrbitDraw), "DrawSystem")]` Postfix (see
  DrawClosestApproachPatch.cs / ClosestApproachRenderer.cs — the "label beside crosshair
  target" template).
- `GUIOrbitDraw.CrossHairTarget.Ship` + `CrossHairTarget.GetSXY(out sx, out sy)`;
  instantiate `Resources.Load<Transform>("GUIShip/lblOrbit")` under orbit panel; TMP_Text;
  refresh on timer; destroy/hide lifecycle per ClosestApproachRenderer.
- Text from drain ledger: "N2 1,234 kg −2.5 kg/s" etc. Refs: Vectrosity.dll,
  Unity.TextMeshPro.dll, UnityEngine.UI.dll.

## 12. Gotchas checklist

- Exclude station contents from dstTanks (objCOParent chain) — GetICOs1 bSubObjects:true
  recurses into containers.
- Clamp gas fills ≤ 99.9% StatGasPressureMax (burst → shrapnel, GasContainer.cs:492).
- Use StarSystem.fEpoch deltas (game time), clamp catch-up (Testudo: 86400 s cap).
- GetCOsAtWorldCoords1 bAllowLocked param is unused — don't rely on it.
- GetRCSCans()/GetRCSRemain() return empty/shallow-fields when shallow — guard.
- Don't call Ship.RefuelRCS on shallow ships — bug: adds to fShallowMass (Ship.cs:7646-7658).
- RCS scans use bAllowDocked:true in vanilla — for "my ship only" semantics pass false
  where appropriate (GetICOs1 default) / be explicit.
- Anything IsAirtight matches TIsRCSValidInput (even EVA suits) — fine, mirrors vanilla.
- COs must have GasContainer component for AddGasMols (null-check like SafePump).
- GUIAirPump switches have TWO conds each (GUIAirPump.cs:183-219,345-383; GasPump.Pump):
  capability conds IsTurbo/IsSlowMode/IsReverse (on the CO def, make the switch VISIBLE) vs
  state conds IsTurboOn/IsSlowModeOn/IsReverseOn (written by the toggle, bPersists, read by
  behavior code). Behavior code must read the *On conds. Turbo multiplier = amount of the
  IsTurbo cond (vanilla turbo pump: 20 via CNDOLAirPump01). If both on, slow wins (0.1x).
- Deterministic FNV rolls: same regID → same result; use distinct salt per tier/purpose.
- Keep per-station state keyed by station strID (COs persist across saves; static
  dictionaries keyed by strID, cleaned when CO destroyed — check bDestroyed).
- Time scales: Plugin.Update is real-time; convert via fEpoch delta for game-seconds rates.
- Multi-dock: GetAllDockedShips includes stations; filter IsStation() as needed.
- The game pauses on some UIs (bPausesGame) — epoch delta handles it naturally.

## 13. Test plan (in order)

1. JSON pack loads (LogOutput has no data errors); T1 item spawnable via debug console
   (ConsoleResolver.cs has spawn/damage commands — check `spawnitem`-style commands).
2. T1 transfer: insert part-full N2 can → ship's installed RTAN2 fills; RCS-intake cans
   fill; no overpressure; stops at 99.9%/full; Off toggle stops.
3. Kiosk stock appears (check OKLG supplies kiosk); prices sane.
4. Derelict spawn: force chance=1.0 in config, dock fresh derelict, verify placement;
   reset chance, verify determinism per regID.
5. T2: He3/D2O transfer into installed LHe02/LH02 cans; capacity clamps.
6. T3: docked derelict tanks drain into our ship; undock/re-dock stability.
7. T4: lock target at range → ledger increments, fShallowRCSRemass drops, target stops
   maneuvering, does NOT despawn (UnregisterShip patch), label (if built) shows.
8. Board drained ship: InitShip postfix applies ledger; realized tanks match ledger;
   no double-drain from SyncFuel; save/load persistence of ledger conds (§3.2 VERIFY).
9. Without Ship's Water: no errors, water paths inert. With it: water stealing works.
10. Multi-station, FFWD, dock/undock storms, target destruction mid-drain (null-guards).

## 14. Session startup notes for implementer

- Work dir is the game root (quoted paths contain spaces & parentheses). Mod/dev root is
  `Ostranauts_Data/Mods/TankStations/` — a git repo; commit there freely (user wants rollback
  history). Never touch other mods' folders or the game root with git.
- Decompiled game source: `Assembly-CSharp/*.cs` (flat + Ostranauts/ subfolders).
- Vanilla JSONs: `Ostranauts_Data/StreamingAssets/data/<folder>/*.json` (large files;
  grep with line numbers, read slices).
- Reference mod sources (game root): `TestudoSafePump/TestudoSafePump/`,
  `ShipsWater/ShipsWater/`, `OrbitMarkers/OrbitMarkers/` — decompiled; contain ILSpy
  artifacts like `//IL_0040: Unknown result type` comments; logic is intact.
  Bridge preloader decompiled copy: `/tmp/opencode/bridge/` (regenerate with ilspycmd if gone).
- User's prior JSON-only mod example: `Ostranauts_Data/Mods/LargeStorageBay`.
- Ask user before: launching the game. Building writes to `Mods/TankStations/src/{bin,obj}/`
  and the deploy step copies the DLL to live `BepInEx/plugins/TankStations/` — both pre-approved.

---

## 15. Implementation log

### Session 2 (2026-08-06) — milestone 1 built, awaiting first run-time test

- Consolidated dev root into `Ostranauts_Data/Mods/TankStations/` (git repo, commit ea462ec+).
- Data pack written for ALL tiers: conditions_simple (IsTankStation, IsTankStationT1..T4,
  IsTankDrainVictim — all hidden 0/0), conditions (StatTankDrainO2/N2/He3/D2O/H2O, bPersists),
  condtrigs (per-tier Installed/Uninstalled/Dmg CTs + TIsTankStationInstalled OR-wrapper +
  fit CTs TIsFitTankStationT1/T2 with helpers), items (1x1 T1 / 3x3 T2-4, all 4 variants each),
  condowners (16 COs modeled on ItmSafePumpTestudo, minus IsHiddenInv so contents stay visible),
  installables (9/tier, Testudo-cloned), interactions (GUITankStation(+Allow),
  MSTankStationPowerOn/Off), guipropmaps (TankStationUI → GUIAirPump prefab),
  powerinfos (TankStationT1..T4), loot (Loose entries + dismantle yields).
- C# milestone 1: Plugin.cs (config: per-tier flow, tick, kiosk T2 chance, derelict chances,
  T4 ranges, verbose), TankStation.cs (core transfer loop: internal-hopper sources → ship
  installed tanks + RCS-intake cans; 99.9% pressure clamp; swept-volume gas rate
  mols = L/1000 × molSpecies/StatVolume; liquid kg = L/1000 × density; dump = vent excess),
  KioskStock.cs (T1 always 1-2, T2 at config chance, Testudo's 11 pools, invariant-culture).
  Builds clean (`dotnet build -c Release src/`), DLL deployed to live `BepInEx/plugins/TankStations/`.
- **Design deviations (simplifications proven by Testudo Safe Pump):**
  * On/Off is the vanilla GUIAirPump knob writing IsOverrideOff/IsOverrideOn, surfaced through
    the Power ticker as `IsPowered` — there is NO IsTankStationRunning cond. Off = knob OFF or
    unpowered. (`GUIAirPump.cs:124-140`, `Powered.cs:369-388`.)
  * "Dump From Target" toggle = the panel's Reverse checkbox (`IsReverse` cond shows it,
    writes `IsReverseOn`). Label reads "REVERSE" in v1 (prefab-hardcoded); documented in
    item descs + interaction tooltip.
  * Dmg variants are separate spawn states + repair-flow targets, mirroring Testudo (the
    Destructable command's 3rd field is a *loot table of interactions* — ACTMechDestroy →
    MSDestroyMech husk — not an auto-swap to the Dmg CO).
- Placeholder art: T1 = ItmCanister03* copies; T2-4 = ItmCanisterLHe02Loose/LH02Loosen copies.
  Replace with real art (per-tier recolors) before release.
- NOT yet implemented (next milestones): T3 docked-ship sources; T4 shallow-target drain +
  ledger + Patch_UnregisterShip; Patch_Ship_InitShip (derelict/hauler/COHO spawns + dock
  reconciliation, §7); Ship's Water soft dep (§10); nav label (§11).
- Known question for testing: station scan uses `CrewSim.coPlayer.ship` (SafePump precedent) —
  if coPlayer.ship changes while boarding a derelict, stations pause until you return. Verify.

### Session 2b (2026-08-07) — milestone 1 PASSED in-game; fixes applied

User tested live: pack loads clean, T1/T2 spawn/install/uninstall/transfer/99.9% clamp/kiosk all
work; He3+D2O transfers work; dump mode works. User edits (commit 6f79e47): GUID
`com.ostranauts.tankstations`, Ryokka flavor + IsRYO cond, T4 price buffed + ItmMineral79 in T4
dismantle loot, kiosk pools cut to 4 supplies-only, new config entries HaulerT3Chance (1.0) and
PirateT4Chance (0.05) — milestones 2-3 must consume these names.

**Changes in 2b (this pass):**
- Dump toggle MOVED off the GUIAirPump Reverse checkbox (users read "reverse" as pump-back) to
  right-click interactions `TankStationDumpOn`/`TankStationDumpOff` ("Dump Mode: Start/Stop")
  applying `±IsTankStationDump` via `LootCondsThem` (vanilla cond-loot mechanism, cf.
  CONDSitStartThem). New conds: IsTankStationDump (visible chip), IsTankStationIdle (hidden).
  New CTs TIsTankStationNotDumping/Dumping gate which menu entry shows. IsReverse removed.
- Idle power save: powerinfos now use `strOverrideCond: IsTankStationIdle` + small
  `fOverrideAmount` (vanilla mechanism, inverted vs air-pump turbo); C# SetIdle() toggles it.
  (`Powered.cs:275`: override cond present ⇒ draw = fOverrideAmount instead of fAmount.)
- T2-4 power points simplified to 4 orthogonal (32,0)/(-32,0)/(0,32)/(0,-32) + PowerA-D inputs
  (was 8-point "knight's move" pattern).
- TankStation.cs refactored to a `ResSpec[]` resource table (Name/VesselCond/Species/Stat/
  Density/DstCT/DumpForced/MinTier) — no more 4-parallel-variables; Ship's Water = +1 row later.
  RCS-intake cans now inserted FIRST in the N2 dst list (fill priority over room N2; user's TODO).
- Answers to test-session questions: AUTO vs ON differ only when a signal/sensor is wired
  (AUTO obeys it, ON forces on; `Powered.cs:269-278`); our code keys on IsPowered so both work.
  `num/num2/flag/item` naming in reference mods = ILSpy decompiler artifact (locals lose names
  in compilation); original sources are normal C#. He3 "30 L/tick" observation ≈ 25.8 kg theory
  (kg/L slip); D2O matched exactly. Kiosk row price = StatBasePrice × kiosk markup.

**Known issues / next session:**
- Uninstalling a station ejects contents to floor; a big 3x3 canister can VANISH if no 3x3 free
  floor exists (vanilla content-ejection). Workaround: empty hopper before uninstall. Candidate
  fix: give Loose variants container fields + Inventory interaction (contents carry over like
  FFU bins) — needs testing; Testudo chose no-container-loose.
- T2-4 installed art is the loose He3-canister placeholder (user will do art before release).
- Shelved (post-release, ask players): selling gas back to stations (GUIStationRefuel slider
  won't go negative) — potential He3/D2O sell loop.
- Milestone 2: T3 docked-ship sources (GetAllDockedShips tanks as sources); Patch_Ship_InitShip
  derelict T1/T2 + hauler T3 spawns (use DerelictT1Chance/DerelictT2Chance/HaulerT3Chance).
- Milestone 3: T4 shallow-target drain + ShipCO ledger + Patch_UnregisterShip + dock
  reconciliation (§7); uses PirateT4Chance, T4FullRangeKm/T4MaxRangeKm.

### Manual Exploration (2026-08-07)
- Sometimes, if you want something done the way you want, you gotta be the one to dig and implement.
- Added `Ostranauts/.gitignore` (outside the TankStations mod folder) that ignores the ship folder so searching a term doesn't return 10000+ results
- Tried to use "aInteractions" "ACTTogglePower" used by ItmSwitch01 and ItmToolWorkLamp01. It seems to not work when put on random mod equipment. I don't know what part of the code runs it. I noticed both ItmSwitch01 and TimToolWorkLamp01 have two condowners: on and off, where the off condowner has "IsOff=1.0x1".
- I looked into GUIAirPump.cs. It has three switches: Turbo, Slow, and Reverse. It checks the condowner whether the conditions "IsTurbo", "IsSlowMode", and "IsReverse" exist, and only shows those switches if so.
- I'd like to do the extra work to implement turbo (20x flow rate on the stock Turbo Air Pump, oddly defined in "CNDOLAirPump01" in loot.json for some reason) and slow (0.1x flow rate). See GasPump.cs, 234-241.
- Unfortunately, powerinfos.json only accepts *one* "strOverrideCond" and "fOverrideAmount". I'm already using my normal and override states for idle and pumping.
- I switched it around from last night. "fAmount" is now idle power draw (flat 100 W) and "fOverrideAmount" is now pumping power draw (2880 W, 8640 W, 11520 W, 14400 W).
- An alternative way to support different power draws (including more than 2, although no stock device has more than 2) is a different condowner for every power state.
- "TowingBrace01" has "fAmount": 5e-6 (18 W), "TowingBrace01Secured" has "fAmount": 0.033 (118800 W).
- But I don't want to deal with combinatorics.
- I've settled on the following:
- - (GUIAirPump) Off/Auto/On: Off = 100 W, auto or on = barely any work to do ? 100 W and do nothing : 1x base power draw and pump
- - (GUIAirPump) Slow Off/On: 0.1x flow rate. Does not save power.
- - (GUIAirPump) Reverse Off/On: Switches srcTanks and dstTanks before running the transfer
- - (Interaction) Dump Mode On/Off: retained from your 2b. It applies the "IsTankStationDump" condition. I've verified that this is retained through save and load.
- In-game, I can't get Slow or Reverse to do anything. It's like slow is always on and reverse is always off.
- Unfortunately there seems to be no way to use 0 W other than gating the tank station with a power switch. You can do the same thing with a towing brace to make it use no power, even when secured.
- In theory I could use "IsOverrideOn" to distinguish auto and on, but I personally don't have a use case for "force pumping action when I know there's no work to do". I might leave this to public feedback.
- Loose tank stations now have inventories. Damaged (possibly loose) tank stations need to be pried open. I really don't want to chase down the missing 3x3 tank bug and it might involve core game systems, so I'll just guarantee that anything that fits in the installed tank station's inventory will stay there upon uninstall.
- Interestingly, I can open loose inventories, despite FFU Storage Rebalance supposedly banning that. It's a lot like how I can open the Water Recycler's inventory. Maybe Storage Rebalance only blocks stock equipment.
- "real" images exist now. I am not an artist and just stole albedo and normals from the N2 can and the He3 tank, but at least visually you can expect the tank station to fit either 9 small tanks or 1 big tank.
- The use point has been moved to the bottom ("use,0,-16" for T1, "use,0,-32" for others).
- There is a closed form solution to "how much gas was transfered after x seconds assuming a continuous process": Each active tank station on the ship stakes a L/s flow rate on each src tank, sum all staked flow rate on a src tank, evaluate the exponential, proportion gas moles and liquid masses back to each tank station according to the ratio of their stake. I have decided to keep the linear step instead of solving the exponential. This saves us a loop and in gameplay terms it really doesn't matter, all the player cares about is that src tanks become nearly empty after some time.
- Right now TankStation.cs logic goes: ItmRTAN2 -> May contain N2, Cannot contain O2, etc. Similarly for ItmRTAO2. In-game there is ItmCanister01, an unlabeled orange gas canister that does not have "IsRTA" or "IsVessel*". There is also ItmRTACO2, the CO2 parallel. CO2 has no use, and no NPC ship spawns with anything in their orange or CO2 cans, or the wrong resource in the wrong can. However, in the course of gameplay, any can can be filled with any gas through an air pump. I can't think of a good way to lump orange cans into tank station logic without inadvertent wasting of non-needed gases.
- - One "common" use case is using a pump to vacuum out your ship into an orange can before doing renovations. That orange can contains a mix of O2, N2, and CO2. It can later be put under the pump in reverse to repressurize your ship. I say "common" because the cost of food you eat in the time it takes to vacuum out your ship is literally more expensive than the room gas so no actual player does this, but stock ship designs include an air pump and an orange can with this intention.
- Is there a reason you did MoveGas for each src? That's O(src*dst). Can't you just remove from all src in one loop, then add to all dst in one loop, then dump any remaining resource?

### Session 3 (2026-08-08) — milestones 2+3 implemented; Slow/Reverse fixed; Turbo added

**Slow/Reverse root cause (both were capability/state confusion + a no-op block):** GUIAirPump
shows a switch when the CO has the *capability* cond (IsSlowMode/IsReverse/IsTurbo) but the
toggle writes the *state* cond (IsSlowModeOn/IsReverseOn/IsTurboOn — vanilla, bPersists). The
code read the capability conds → slow read always-on. Reverse additionally built the swapped
lists but never assigned them back → always-off no-op. Fixed: read IsSlowModeOn/IsReverseOn, swap
now assigns. Turbo added: `IsTurbo=20.0x1` capability cond on the 4 installed COs; flow ×
GetCondAmount("IsTurbo") when IsTurboOn, slow wins (mirrors GasPump.Pump). Turbo does NOT raise
power draw (one strOverrideCond limit, already used by IsTankStationPumping).

**Milestone 2a — T3 docked sources:** tier≥3 adds tanks of every ship in GetAllDockedShips()
(transitive; stations excluded via IsStation()) to srcs: their RCS-intake cans (GetRCSCans(),
filtered `item.ship == docked` because the raycast uses bAllowDocked:true and can see OUR cans
near the airlock) + their installed tanks (per-resource DstCT GetICOs1 on THAT ship). Works in
reverse too (refuels the docked ship — feature).

**Milestone 2b — Patch_Ship_InitShip (adapted from Testudo, same helper code):** postfix gated on
aRooms.Count>0 (Edit+ only). Derelicts: T2 roll first (exclusive), then T1 (DerelictT2/T1Chance).
Haulers: template (json.strName) ∈ {Light Tug, MesaCargo, IbexCargo, Ostrich A8R, Ostrich A4R}
AND GetShipOwner EndsWith("Hauler") (covers "<stn>Hauler" tugs + "<stn>CargoHauler" cargo).
Pirates: template ∈ RandomPirateShip set AND owner == "COHOPirates". Spawns are the Loose
variants with ≤80% wear; FNV1a salts "|tankstationT1..T4"; anti-farm marker IsTankStationSeeded
on ShipCO (new hidden cond) so a stolen station doesn't respawn on re-dock/re-load. Player's
current ship + player-owned ships (owner == coPlayer.strID) excluded. Verified facts:
dictShipOwners IS serialized; ship.json.strName is the template name on fresh spawns; ShipCO
conds serialize even for shallow ships (GetJSON: json.Clone + shipCO = ShipCO.GetJSONSave()).

**Milestone 3 — T4 remote drain:** target = GUIOrbitDraw.CrossHairTarget.Ship (global namespace),
range = objSS.GetRangeTo × CrewSim.KM_PER_AU; ≤T4FullRangeKm full flow, linear falloff to
T4MaxRangeKm, skip when docked-to-us (T3 covers it) or in reverse. Loaded (Edit+) targets: real
tanks join srcs (v1: full flow in-range, falloff only for shallow). Shallow targets: ShallowFuel.cs
- TemplateInv per target (cached by regID): totals from the PRISTINE dictShips template (clones at
  spawn keep editor-baked full values; fallback: live ship.json), dictCOSaves per-CO aConds
  override def aStartingConds ("DEFAULT" marker expands to def conds; format "Name=1.0xAmount"),
  N2 cross-checked against fShallowRCSRemassMax (catches cans missed by item enumeration).
- Fusion kg/s from limiting reactant (D2O:He3 = 0.667:1.0 mass; templates saved full →
  rate = templateKg/bakedSeconds). He3/D2O drains decrement fShallowFusionRemain by kg/rate with
  a min-consistency clamp across both reactants. N2 decrements fShallowRCSRemass (AI stops
  thrusting at 0). O2 is ledger-only (no shallow field exists).
- Remote gas flow decays like real cans via virtual source volume = mols×R×293/41400 (rated RTA
  P/T — can-independent identity for StatVolume 0.787).
- Ledger = StatTankDrain{O2,N2,He3,D2O} kg on target.ShipCO + IsTankDrainVictim marker.
- Each active remote resource = one pseudo-source sharing the station's flow with local srcs
  (numActive denominator). Dump mode forces remote need → target vents dry (the T4 fantasy).
- Even split between resources (design-approved fallback; no torch-vs-RCS prioritization in v1).
- ReconcileOnLoad (in the InitShip postfix, BEFORE spawn rolls; after BreakIn, before delayed
  SyncFuel): subtracts ledger from realized tanks (gas via AddGasMols negative — N2 includes
  GetRCSCans(); liquids via AddCondAmount, floor 0 per can), zeroes all ledger conds, keeps
  IsTankDrainVictim. SyncFuel then only drains cans toward the ALREADY-reduced
  fShallowRCSRemass → no double-drain (idempotent).
- Patch_UnregisterShip.cs: prefixes on the PRIVATE UnregisterShip(AIShip) choke point (covers
  public wrapper + RegionCleanup + SosOutOfFuel) and FlyTo.TryInstantCleanup (which backdates
  IsStale WITHOUT going through UnregisterShip). Both gated on new config "Keep Drain Victims"
  (default true). Victims sit inert; clear the cond or disable config to release them.

**Open items:** Ship's Water soft dep (§10); nav label (§11); loaded-remote falloff; resource
prioritization; derelict-cleanup paths don't touch victims (victims are live AI ships, not
derelicts) but a victim far away that the player never visits may still age out via IsStale if
some other system stamps it — not observed, watch in testing.

**Answers to session-2b questions:** MoveGas per-src is NOT O(src×dst) in practice — the inner
dst loop breaks as soon as that source's aliquot is placed, so it's O(src + dst) total; pooling
(removing all then filling) is mathematically identical (same need[] bookkeeping, same dst fill
order), so the per-src form stays (matches SafePump, makes vented attribution explicit). Orange
cans (ItmCanister01/ItmRTACO2): correctly excluded — no IsVessel*/fit-CT match; the mixed-gas
vacuum-can use case is real but out of scope for v1 (a "drain room air" feature would need
GasInput-point semantics, not vessel CTs).

**Now testable (§13):** items 4 (derelict spawn — set DerelictT1Chance=1.0), 6 (T3 docked drain),
7 (T4 lock+drain+victim keep-alive), 8 (board drained ship: ledger reconcile, save/load).

### Testing (2026-08-08)
- Oh, uh, maybe I shouldn't have been so wordy. My meaning above is "If I could have more than two power states, I would add turbo and make it use more than 100% base power. In the absence of more than one 'strOverrideCond', I am fine with having no turbo and only a slow mode that does not save power."
- You went ahead and plugged in turbo (20x flow rate for no additional power draw), which I guess is okay. It's always better to give the player more options (I want to fully drain this tank NOW) and let them choose for themselves which options they will use and which ones are too OP.
- Verified on my ship:
- - Tank stations do not accept ItmCanister01 or ItmRTACO2, since the conditions they have do not match any accepted VesselConds. This is fine for the first release. I will see if sorting mixed gases from an orange can is a desired use case.
- - N2 cans under RCS intakes are filled before other N2 cans
- - When an accepted can (like N2) has both N2 and O2 through air pump shennanigans, it's as if the tank station sweeps the expected volume from the entire source, keeps only N2, and returns O2 back to the source. N2 goes down proportionally while O2 stays the same. O2 does not get put in ship O2 cans even if there is needed O2.
- - On a T2 tank station, normal takes 0.256x every 2 second cycle (100.78 L/s). slow takes 0.0255x every cycle (10.05 L/s). Turbo *has no effect*. It seems to still be 0.256x. You cannot select turbo and slow simultaneously. Selecting one will deselect the other. The stock turbo air pump is like this too.
- Verified from docking at derelicts:
- - When both T1 spawn chance and T2 spawn chance are 100%, a T2 tank station spawns (loose). The T1 is blocked.
- - See `C:\Users\spenc\AppData\LocalLow\Blue Bottle Games\Ostranauts` `Player.log` and `Player-prev.log` (this particular interaction may not be in there because the game only keeps the last 2 game sessions)
- - It does not interfere with Valtorra's water tanks. I saw a water tank along with my tank station on the same derelict.
- - Undocking and re-docking does not regenerate the tank station. On the other hand, Valtorra's water tank respawned.
- Verified from mooring at haulers:
- - The T3 is there (loose).
- - I noticed you made all the tank stations spawn loose. The water tanks and safe pump are spawned installed.
- - First thing I did (after uninstalling all their thrusters and saying hi) was install the T3 on the hauler. It immediately started draining my ship to fill their ship (moored ships are considered docked and T3-able even if no physical link exists). See `Ostranauts/MileStone2Images`. Working as intended.
- - When there's nothing to do, the power draw (as measured from the hauler's battery) goes from 27 kW to 14 kW.
- - I then uninstalled the T3 and put it on my ship, which did the same thing in reverse.
- - The reverse toggle works as expected.
- - Due to the hauler layout, the only valid spawn point for a 3x3 item is in the airlock. I don't think any loot / loose items every spawn there so it should always be available.
- Now the hard part: Taking down pirates with guns at Ceres and stealing their T4:
- - There is no T4. I think you have the wrong PirateTemplates in Patch_Ship_Initship.cs. Those are tiny racing craft used by *OKLG* pirates, not the gunboats used by *COHO* pirates.
- - loot.json line 27650: "RandomPirateShip" "SalvagePodSmall=0.125x1|SalvagePodEndurance=0.125x1|SalvagePod=0.125x1|Inspection Pod=0.125x1|Coffin=0.125x1|Whistler Hot-Rod=0.125x1|Argute Rapid Courier=0.125x1|Primigenial PY=1.0x1"
- - line 27461: "RandomNavyShipBeltPirates" "Vector3 Pirate Refit=0.45x1|Babak Refit=0.45x1|Pequod Pirate Refit=0.1x1"
- - Swapping out PirateTemplates still didn't fix it. Even at 100% spawn rate, nothing was in the logs. So I checked text
- - [Debug  :Tank Stations] [TankStations] InitShip: O-GEZ owner=BeltPirates template=Babak Refit
- - Swapping "COHOPirates" for "BeltPirates" fixed it. I got my T4
- - [Debug  :Tank Stations] [TankStations] InitShip: O-GEZ owner=BeltPirates template=Babak Refit
- - [Info   :Tank Stations] [TankStations] Spawn: placed ItmTankStationT4Loose on O-GEZ.
- - Yay.
- - With my new T4 station, I tried to sneak up on another Babak Refit. The 500 km cutoff range of the remote siphoning is smaller than their 600 km sensor range. After buffing my cutoff range I found that I could sit outside their sensor range (much as I can manually snipe at 1000 km using railguns) and drain fuel without them ever being aware of me existing or taking evasive manuvers.
- - My RCS N2 reserves ticks up by a few kg every few seconds, and presumably their RCS ticks down an equal amount.
- - I don't even need to press the button to target lock them. Simply having the navigation crosshair on them was enough.
- - - Target lock is used by *NPC* pirates when trying to chase and board the *Player*. The *Player* gets a target lock alert on their nav. It is used by all ships to fire missiles (otherwise the missiles go off sideways in a straight line). Apparently the NPC Babak Refit takes no evasive manuvers when I target lock them at 700 km.
- - Then I crashed into an asteroid while I wasn't paying attention to the map and my ship blew up. I got this when quitting to menu or loading a save:
- - - NullReferenceException: Object reference not set to an instance of an object
  at StarSystem.Update (System.Double fTimeDelta) [0x0013d] in <f9c3e707ae374f5ab5de549194f2ee62>:0 
  at CrewSim.Update () [0x001f5] in <f9c3e707ae374f5ab5de549194f2ee62>:0 
- - - I've tried setting the T4 on or off, installed or loose or in my drag slot, or trashing it in my inventory. So long as it has existed at least once in the world, even if it no longer exists, the game will not exit properly. Maybe it really does run on witchcraft. Needless to say I can't release the mod in this state.
- - In fact, I seem to be unable to use the debug menu to instantly return to OKLG.
- - - Leaving ATC Region: BCER
NullReferenceException: Object reference not set to an instance of an object
  at AIShipManager.AIShipCleanup (Ostranauts.Ships.AIPilots.AIShip aiShip, System.Collections.Generic.Dictionary`2[TKey,TValue] dictStns, Ship atcLast) [0x00007] in <f9c3e707ae374f5ab5de549194f2ee62>:0 
  at AIShipManager.RegionCleanup () [0x00124] in <f9c3e707ae374f5ab5de549194f2ee62>:0 
  at AIShipManager.Update () [0x00017] in <f9c3e707ae374f5ab5de549194f2ee62>:0 
  at StarSystem.Update (System.Double fTimeDelta) [0x00399] in <f9c3e707ae374f5ab5de549194f2ee62>:0 
  at CrewSim.Update () [0x001f5] in <f9c3e707ae374f5ab5de549194f2ee62>:0 
- T4 Testing (by loading a save from before I instantly travelled to Ceres and then spawn ItmTankStationT4Loose):
- - Waiting for a scavenger with a reactor ship took a very long time. There are reactor ships in loot.json "RandomScavShip" but I guess luck isn't with me today.
- - I found a Heavy Tug 01 at 23:38, 10 hours after game start, and immediately chased it down.
- - Template: 2 O2 cans, 2 atmo N2 cans, 6 RCS N2 cans, 1 He3 tank, 1 D2O tank
- - Expected contents: 855 kg O2, 749 kg atmo N2, 2247 kg RCS N2, 5216 kg He3, 44722 kg D2O
- - Remote drained (as measured from my tanks): 855 kg O2, 1349 kg N2, 183 kg He3, 41243 kg D2O
- - Even after changes in my ship's resources stopped, I didn't receive the SOS from the scavenger.
- - I then saved, alt + f4, and loaded that save
- - After 3 hours 30 minutes of 16x game time, the ship did not despawn.
- - I then attempted a force docking to see if that would get the AI to switch to evade mode, then realize it's out of N2, then SOS. It didn't do any of that.
- - I have no reference to what a normal ship despawning is like and don't know if the ship would have despawned on hour 4. I think this is one of the things that will need testing in real gameplay by different people.
- - Remaining when docked: 0.01 kg O2, 482 kg atmo N2, 0.02 kg RCS N2, 5026 kg He3, 3420 kg D2O
- - The remaining amount is close to the 5216 : 3479 He3 : D2O ratio
- - In fact, while remote draining, for every 1 kg He3 I get like 8.6 kg D2O, which is way too D2O rich
- - He3 stops increasing early. D2O keeps increasing for longer.
- - Did you flip the masses attributable to fShallowFusionRemain / unburnable?
- - The log says:
- - - [Info   :Tank Stations] [TankStations] Reconciled drain ledger on O-8JGZ: O2 855.83 kg, N2 1355.27 kg, He3 183.15 kg, D2O 41243.73 kg.
[Debug  :Tank Stations] [TankStations] InitShip: O-8JGZ owner=OKLGScav template=Heavy Tug 01
- What does "selected on the map" mean?
- - In-game each nav station keeps track of its separate selection. I spawned a ship for myself that has two nav stations.
- - There is also the navmap GUI that you can access in your PDA at all times, which can be opened to show a map too.
- - It seems to be selection of the map *most recently* interacted with. I can change drain or no drain by opening the PDA, or hopping in nav station 1, or hopping in nav station 2, and selecting either the heavy tug or empty space. The behavior of the T4 and whether tanks on my ship fill over time reflects this state, even if I'm not in a map and just walking around.
- I realized that if you have a T4 running on your ship and you click around various derelicts and ships to take a look, as I often do, *each ship you click on will be drained a bit and will be marked as a drain victim*. This might eventually clog up the ship list. I'll have to see the long-term effects of this.
- You may also want to take a look at this log when my tanks are *full*.
- - [Info   :Tank Stations] [TankStation f26834e0-d82f-4988-8592-da9f1a8ad6e7] IDLE: loaded tanks are nearly empty
- Oh I also changed the T4 falloff from lerp to 1/n (with a hard cutoff). I actually intended for T4 to remote drain 30 L/s at 500 km, 60 L/s at 250 km, and 300 L/s at 50 km. Lerp means that at 250 km (you can still be acquired and get missiles, but are outside coilgun and railgun auto range), you move 150 L/s. Tank Stations should encourage either patient zoning (if you don't want to get hit) or rapidly closing in and going for the mobility and/or kinetic kill (if the target can't fight back or you can tolerate hits on your ship).
- My original intention was for T4 to siphon out to infinity. It would be basically useless beyond a couple hundred km but it would be funny.
- Strictly speaking incorporating StatLiqH2O and adding remote drain remaining and rate text to the nav map isn't necessary for a release, but I really want the latter because even with the log file open I have a hard time figuring out what the T4 station is doing.

### Session 4 (2026-08-09) — crash fix, fusion model fix, turbo fix, nav label

Response to the 2026-08-08 test notes (commit 359b957 kept: BeltPirates owner + gunboat
templates + 1/n falloff are the user's edits, retained):

1. **Exit/load NRE (release blocker) — root-caused and fixed.** Vanilla `Ship.Destroy()` order:
   `CrewSim.system.RemoveShip(this)` THEN `AIShipManager.UnregisterShip(this)`, with
   `bDestroyed = true` only AFTER. Our UnregisterShip prefix blocked that call for victims → dead
   ship stayed in `dictAIs`; the `AIShip.Ship` getter (`if (_shipUs.bDestroyed) _shipUs =
   GetShipByRegID(...)`) then turns the reference into a REAL null once the ship is gone from
   dictShips → next `GetAIShipByRegID` NREs mid-teardown (`StarSystem.Destroy` → `Ship.Destroy` →
   `UnregisterShip` → `GetAIShipByRegID` lambda NRE, seen in Player-prev.log:14993), aborting
   teardown and leaving `StarSystem.Update` NREing every frame. Fix: prefixes never block when
   `ship.bDestroyed` OR the ship is no longer in `CrewSim.system.dictShips` (= being torn down)
   OR `CrewSim.system == null` (scene teardown). Note region changes still recycle victims
   (AIShipCleanup destroys AI ships directly on "Leaving ATC Region") — that's vanilla region
   recycling, unaffected and correct.

2. **Fusion drain model ("did you flip the masses?") — fixed.** Old model debited
   `fShallowFusionRemain` by kg/rate for BOTH reactants: draining the unburnable D2O reserve
   (Heavy Tug carries 44722 kg D2O vs 5216 kg He3; burn needs only 3479 kg D2O for that He3)
   zeroed the torch seconds, which the availability formula then read as "all He3 burned" → He3
   stopped at 183 kg while D2O kept going (exactly the user's numbers). New model: ledger cond
   `StatTankDrainTorch` (bPersists, seconds) records the torch time WE debited;
   `burnedSec = max(0, baked − live − drainedTorch)`; availability `kgNow = templateKg −
   burnedSec×rate − ledger`. After each fusion-reactant drain, `fShallowFusionRemain` is
   RECOMPUTED as `min(live, kgNowHe3/rHe3, kgNowD2O/rD2O)` and the debit accumulated into the
   torch ledger. Draining excess D2O now leaves the torch almost untouched; draining the limiting
   He3 kills it proportionally. Reconcile zeroes the torch ledger with the rest. Expected retest
   on the same Heavy Tug: full ~5,033 kg He3 drainable and ~41 t D2O, torch dies exactly when the
   limiting reactant empties. (The 8.6:1 D2O:He3 drain ratio the user saw is just the density
   ratio of the swept-volume rate model — both canisters are 40.4 m³ — not the bug.)

3. **Turbo fixed.** Vanilla format is `IsTurbo=1.0x20.0` (CNDOLAirPump01, loot.json:3148);
   GetCondAmount returns the part after "x". Ours was `20.0x1` → multiplier 1.0. Fixed on all 4
   installed COs. Turbo/slow mutual exclusion observed in-game is stock GUI behavior.

4. **Victim marking threshold (anti-clog).** IsTankDrainVictim is now applied only when the
   target is meaningfully crippled: RCS dry (fShallowRCSRemass ≤ floor with GetRCSMax > 0), OR
   torch dead (baked > 0 and fShallowFusionRemain ≤ 0), OR any single resource's ledger ≥ 90% of
   its template total. Clicking around the map sips a few kg without condemning ships to eternal
   inertness; the kg ledger is always written and reconciles on boarding regardless.

5. **Nav label (§11, "I really want this") — implemented.** `NavLabel.cs`: postfix on
   `GUIOrbitDraw.DrawSystem`, label instantiated from `GUIShip/lblOrbit` under the orbit panel
   (OrbitMarkers pattern), shown when the player's ship has an installed+powered Mk IV and the
   crosshair target is a ship. Shows range/flow%, per-resource remaining kg and live kg/s rate
   (TankStation records last-tick remote rates into `RemoteKgPerSec[]`/`RemoteTargetRegID`),
   "OUT OF RANGE", "TARGET DRY", and a loaded-target note. Toggle: config "Nav Siphon Label"
   (default on). New csproj refs: Unity.TextMeshPro, UnityEngine.UIModule, UnityEngine.UI.

6. **Cosmetics.** Idle log now distinguishes "ship's tanks are full" vs "sources are empty" vs
   "no tanks loaded"; the user's InitShip owner/template debug line is gated behind Verbose
   Logging.

**Retest list:** (a) exit-crash repro: create a victim, then quit to menu / load save / debug
jump regions — should be clean now; (b) Heavy Tug He3 full drain + torch behavior; (c) turbo
switch actually multiplies flow 20x (and slow still 0.1x, slow wins); (d) click-sipping a ship
briefly then leaving it alone → it should despawn normally later (no victim marker until
crippled); (e) nav label appears on crosshair targets with a powered T4, updates rates while
draining; (f) reconcile on boarding unchanged.

### Session 4 Testing (2026-08-09)
- Holy shit the singularity might be upon us. You figured out what caused the null reference exception from nothing more than offsets of core game functions. If I had to debug that myself I genuinely would have mothballed this mod.
- I loaded the save with the heavy tug.
- - I can see your orange label on the nav map.
SIPHON 10.5 km - full flow
O2 775.4 kg -40.8 kg/s
N2 1,323 kg -16.1 kg/s
He3 5,197 kg -9.68 kg/s
D2O 44,559 kg -83 kg/s
- - My interpretation: there are two O2 cans at 100% pressure, there is a single (shallow) N2 can at 80% pressure, there is 1 He3 tank, there is 1 D2O tank
- - You said the tank station logic does an equal volume sweep of the shallow ship's He3 and D2O. Both are equal volume (40400 L) and D2O is 8.6 times denser, so you end up siphoning 1 : 8.6 He3 : D2O mass ratio. Technically the fastest mobility kill is to siphon one of the resources only, but I prefer the equal volume sweep. It's more robust to future game updates or mods introducing new reactor types that have different reactants and limiting reactants. Just drain everything equally.
- - The log is as expected. O2 and N2 slow down while He3 and D2O remain constant.
[Info   :Tank Stations] [TankStation bc414805-08cd-48d8-8669-07300f7d1898] PUMPING O2 +2515.242 mol N2 +1137.435 mol He3 +19.108 kg D2O +163.834 kg remote:O-8JGZ
[Info   :Tank Stations] [TankStation bc414805-08cd-48d8-8669-07300f7d1898] PUMPING O2 +2346.903 mol N2 +1143.936 mol He3 +19.68 kg D2O +168.738 kg remote:O-8JGZ
[Info   :Tank Stations] [TankStation bc414805-08cd-48d8-8669-07300f7d1898] PUMPING O2 +2085.329 mol N2 +1098.193 mol He3 +19.362 kg D2O +166.01 kg remote:O-8JGZ
- - This time I immediately forced dock but the heavy tug continued its course. It still had N2 at this point.
- - I'm honestly not sure what triggers a NPC to begin evasive manuvers, or if I was hallucinating the whole time and NPCs do not try to evade when you Initiate Forced Docking Procedure. Non-combatants (miners or cargo) at Ceres belonging to CCRE or GalileanConfederacy will book it if you are a known enemy and approach them. This time I got Fctn:s in the Event Log at the bottom of the screen, which I thought indicated an unfriendly encounter.
- - - Comms Controls:
> <Configuring Docking Procedure>
<Handshake received> <
> Original Renegade, ready to proceed
O-UZW2, please choose a docking port. <
> Original Renegade this is Eldritch Funk requesting clearance for close approach and docking.
Negative Eldritch Funk. You are not cleared for close approach. Change your vector. <
> Be advised Original Renegade we are initiating maneuvers for a forced dock.
Captain Hailey Cervantes change your vector, you are unauthorized. I repeat you are not cleared for close approach or dock. <
- - - Event Log:
Fctn:XinhuaCiv 0.00 Fctn:Goodluck Gillespie -0.27 Fctn:XinhuaCiv -0.14 Fctn:Goodluck Gillespie -10.00
- - Back at Nav Controls: SIPHON: target is loaded (boarding range state) - contact drain via real tanks
- - Player.log:
[Info   :Tank Stations] [TankStations] Reconciled drain ledger on O-8JGZ: O2 855.75 kg, N2 1336.6 kg, He3 3440.04 kg, D2O 29495.22 kg.
[Debug  :Tank Stations] [TankStations] InitShip: O-8JGZ owner=OKLGScav template=Heavy Tug 01
- - There's 1770 kg He3 and 15179 kg D2O (about one third of each) remaining on the heavy tug. My ship has 3445 kg He3 and 29543 kg D2O. The equal-volume siphon is working.
- - I noticed something while walking between my ship and their ship. Tank stations only run while I am standing on my ship. The game has some really weird code where the active ship is the one you're standing on, and changes as you walk around. For example, if you're standing on a derelict and a micrometeoroid is scheduled to strike, it will always strike the derelict and not your own ship docked. I assume TankStation.cs is hardcoded to only check tank stations on "your ship", for some definition of "your ship", and since there are no tank stations on the OKLGScav ship nothing is pumped.
- - I then saved and quit to menu. No errors this time. Yay.
- Hopping back in and debug travelling to BCER.
- - No errors.
- - I approached a RandomCivilianShipCCRE Edelweiss and did the "I am not a pirate" thing.
- - - Comms Controls:
> <Configuring Docking Procedure>
<Handshake received> <
> Cerebral Bore, ready to proceed
O-UZW2, please choose a docking port. <
> Cerebral Bore this is Eldritch Funk requesting clearance for close approach and docking.
Negative Eldritch Funk. You are not cleared for close approach. Change your vector. <
> Be advised Cerebral Bore we are initiating maneuvers for a forced dock.
Captain Hailey Cervantes change your vector, you are unauthorized. I repeat you are not cleared for close approach or dock. <
- - - Event Log:
Fctn:Cai Weber -0.23 Fctn:CCRE 0.00 Fctn:CCRECiv 0.00 Fctn:Cai Weber -10.00 Fctn:CCRE -0.10 Fctn:CCRECiv -0.10
- - The NPC ship stayed put.
- - I shot at it, which caused it to run away, and then crash into an asteroid and despawn. I guess I'll have to try a target outside the asteroid field.
- - I approached a RandomCivilianShipGalileanConfederacy Tombolo 2 that was returning to BCER. It was at like 11 km/s but decelerating at 2 Gs
- - It did not respond to forced docking or being shot at. It just carried on its course decelerating.
- - While it was decelerating using the torch, its He3 and D2O labels were not going down.
- - Once within 500 km, I used the T4 to drain its N2, He3, and D2O. But even after all three were dry it kept decelerating.
- - I know that when the player has a course plotted on their long range course plot, the ship is considered on rails. Check if NPCs on torch trajectories also have autopilot and see if there's a way to disengage it.
- - I could have used > 2 Gs acceleration to close the distance and board it at any time, but this is very much not the expected result of draining a ship of all fuel.
- Siphoning behavior:
- - You can select OKLG:
SIPHON 55.6 km - flow 89%
O2 855.8 kg
But stations are protected from tank draining of any kind. Maybe edit the text to show that stations cannot be siphoned?
- - You can select a derelict:
SIPHON 57.9 km - flow 86%
O2 1,513 kg -32.1 kg/s
N2 2,887 kg -18.3 kg/s
He3 20,816 kg -8.25 kg/s
D2O 44,303 kg -70.8 kg/s
This is a MesaCargo which I know has 4 He3 tanks. But we treat the shallow ship as having a single He3 "tank" that holds 4x5216 kg.
This is a problem for bean counters, although it might not be a problem for players. Shallow ships (OKLG derelicts, ships at Ceres you're shooting at, etc.) aren't realized yet. The derelict damage pass or actual ship combat damage might destroy tanks, so you would have been siphoning from tanks that vanish when realized.
Ultimately there is no way to know "does the tank actually exist?" without realizing the ship, and we can't do that because the game would become unplayable. I've thought about this and there is no good solution that both allows siphoning from a shallow ship while satifying bean counters. I think the mod should 1. have a checkbox for "allow siphoning from unvisited derelicts" with a note that the random 0 - 100% fill is not applied and you may successfully siphon more fuel than you could have in the stock game, and 2. simply tell the player in the steam description that combat damage that destroys a fuel tank is not included in fuel capacity calculations. We already have a magical device that drains fuel across open space so might as well permit ridiculous levels of OP.
This discrepancy can be seen in fShallowFusionRemain too. If you shoot out part of a ship that you know has fusion fuel, the ship will keep thrusting.
- After that, I reverted to session 3 and turned off Patch_UnregisterShip.cs to see if that was the causative agent. Then I siphoned the heavy tug again.
- - 
- Your ResSpec for each resource is really convenient... but everything surrounding it is not. Originally I was going to add support for StatGasMolCO2, StatLiqH2O, and StatLiqH2OWaste. But 1. I don't know best practices for handling presence / absence of the Ship's Water mod, 2. disabling O2 remote drain showed the current setup is extremely brittle, and 3. implementing these resources has far lower utility. CO2 and waste water never spawns on other ships. Water spawns uncommonly, is consumed in small amounts, and would be far cheaper in opportunity cost even with Tank Stations to just buy from the refuel kiosk than to go hunting for it and moving cans around. 
- I know you already figured out the ledger for draining O2 from a shallow ship, but having 4 remotely drainable resources clutters the nav map a bit and draining O2 isn't strictly needed for piracy. Keep the flow rate prioritized on what will achieve a mobility kill. I've disabled the paths for remotely draining O2 through the laziest method possible: if i == 0 continue. It exists if there is demand to re-enable it.
- I hate how there's two separate lists of resources, one in TankStation.cs and one in ShallowFuel.cs.
- I also moved the orange label from above the target to to the right of the target. Personal preference.
- There seems to be interference with the Orbit Markers mod. The CPA label is no longer at the green closest point. It's always pinned to wherever this mod's orange label is. I don't know how this game's UI library works so can you check if anything needs to be unlinked or deep copied?
- The tank station does not touch orange cans, or the wrong kind of gas (O2 that got pumped into an N2 can, etc.). I am fine with this. There is another mod that adds a manifold where you can put any gas can at the input side and it will partition O2, N2 and CO2 into each of its outputs.
- I just need to get permission from EddieSM for the pattern to extend the nav map, and then this is ready for release. I'm still not 100% confident no more NREs or various bugs will happen but I will preface the mod as experimental and needing help with testing in real gameplay.
- Can you give me a section that describes how to get a tank station and the usage pattern, as well as a section with a list of gotchas or weird behavior? Things that you and I found out, but would be unintuitive to someone who downloaded the mod?