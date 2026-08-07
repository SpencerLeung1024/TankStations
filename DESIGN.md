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
- Release: stage a CLEAN copy (mod_info.json, preview.png, BepInEx/plugins/TankStations/
  TankStations.dll, data/, images/ — NO src/, .git/, bin/) e.g. in `Mods/TankStationsRelease/`
  + loading_order entry, and upload THAT from the in-game workshop button (the uploader ships
  the entire folder recursively). Subscribers' bridge preloader mirrors the pack's
  `BepInEx/plugins/...` into their live plugins.

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
