using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TankStations;

// Acquisition spawns (derelict T1/T2, hauler T3, COHO pirate T4) and, for milestone 3, dock
// reconciliation of the T4 drain ledger when a drained ship loads for real.
//
// Spawn approach adapted from Testudo Safe Pump's Patch_Ship_InitShip: Postfix on Ship.InitShip,
// Permission granted by Valtora, 2026-08-08
// gated on aRooms.Count > 0 (only Edit+ loads have real items), deterministic FNV-1a rolls keyed
// on regID so results are savescum-proof and stable across visits, floor scan for placement.
//
// Ship identity at full-load time:
// - derelicts: ship.IsDerelict() (DMGStatus == Damage.Derelict), as Testudo.
// - haulers:   template name (ship.json.strName) in the hauler set AND registered owner ends in
//              "Hauler" (covers "<station>Hauler" tug pilots and "<station>CargoHauler" cargo
//              pilots). The owner check keeps bought/claimed player ships from qualifying.
// - pirates:   template name in the pirate set AND owner == "COHOPirates" (Ceres only).
// GetShipOwner is StarSystem.dictShipOwners, which is serialized - it survives save/load.
[HarmonyPatch(typeof(Ship), "InitShip")]
internal static class Patch_Ship_InitShip
{
    private const float MaxWearFraction = 0.8f;

    private const string SeededCond = "IsTankStationSeeded"; // ShipCO marker: anti-farm on re-dock

    private static readonly HashSet<string> _logged = new HashSet<string>();

    private static readonly HashSet<string> HaulerTemplates = new HashSet<string>
    {
        "Light Tug", "MesaCargo", "IbexCargo", "Ostrich A8R", "Ostrich A4R"
    };

    private static readonly HashSet<string> PirateTemplates = new HashSet<string>
    {
        //"SalvagePodSmall", "SalvagePodEndurance", "SalvagePod", "Inspection Pod", "Coffin", "Whistler Hot-Rod", "Argute Rapid Courier", "Primigenial PY"
        // Those are tiny racing craft used by *OKLG* pirates, not the gunboats used by *COHO* pirates
        "Vector3 Pirate Refit", "Babak Refit", "Pequod Pirate Refit"
    };

    // TODO: Investigate if there is a way to tell if a ship was spawned by the "RandomNavyShipBeltPirates" rule
    // Modded Ceres pirate ships won't be picked up by our hardcoded pirate templates
    // Alternatively just ignore template checks altogether and only check the owner
    // In the future we may limit T4 to only the big Babak Refit but for now any BeltPirates ship can get a T4

    private static void Postfix(Ship __instance)
    {
        try
        {
            if (__instance == null || __instance.aRooms == null || __instance.aRooms.Count == 0)
            {
                return; // shallow loads have no items; nothing to spawn into or reconcile
            }
            ShallowFuel.ReconcileOnLoad(__instance); // milestone 3; no-op without a drain ledger
            if (IsPlayerInvolved(__instance))
            {
                return;
            }
            string strRegID = __instance.strRegID;
            if (string.IsNullOrEmpty(strRegID) || __instance.ShipCO == null || __instance.ShipCO.HasCond(SeededCond))
            {
                return;
            }
            if (__instance.IsDerelict())
            {
                // Rarer tier rolls first; a ship gets at most one station.
                if (!TrySpawn(__instance, strRegID, "|tankstationT2", Plugin.DerelictT2Chance.Value, "ItmTankStationT2Loose", 3))
                {
                    TrySpawn(__instance, strRegID, "|tankstationT1", Plugin.DerelictT1Chance.Value, "ItmTankStationT1Loose", 1);
                }
            }
            else
            {
                string text = CrewSim.system?.GetShipOwner(strRegID);
                string text2 = __instance.json?.strName;
                Plugin.Log.LogDebug($"[TankStations] InitShip: {strRegID} owner={text} template={text2}");
                if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(text2))
                {
                    if (text.EndsWith("Hauler") && HaulerTemplates.Contains(text2))
                    {
                        TrySpawn(__instance, strRegID, "|tankstationT3", Plugin.HaulerT3Chance.Value, "ItmTankStationT3Loose", 3);
                    }
                    //else if (text == "COHOPirates" && PirateTemplates.Contains(text2))
                    else if (text == "BeltPirates" && PirateTemplates.Contains(text2))
                    {
                        TrySpawn(__instance, strRegID, "|tankstationT4", Plugin.PirateT4Chance.Value, "ItmTankStationT4Loose", 3);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError("[TankStations] InitShip spawn/reconcile failed: " + ex);
        }
    }

    private static bool TrySpawn(Ship ship, string regID, string salt, float chance, string itemName, int size)
    {
        uint num = Fnv1a(regID + salt);
        double num2 = num / 4294967295.0;
        if (num2 >= (double)chance)
        {
            Verbose(regID + salt, $"Spawn check: {regID} rolled {num2:F3} vs chance {chance:F2} - no {itemName}.");
            return false;
        }
        if (ShipHasCond(ship, "IsTankStation"))
        {
            Verbose(regID + salt, "Spawn check: " + regID + " already has a tank station.");
            return false;
        }
        System.Random random = new System.Random((int)num);
        Tile tile = FindSpot(ship, random, size);
        if (tile == null || tile.tf == null)
        {
            Verbose(regID + salt, "Spawn check: " + regID + " won the " + itemName + " roll but has no free " + size + "x" + size + " floor.");
            return false;
        }
        CondOwner condOwner = Spawn(ship, itemName, tile, size, random);
        if (condOwner == null)
        {
            return false;
        }
        ship.ShipCO?.SetCondAmount(SeededCond, 1.0);
        Plugin.Log.LogInfo("[TankStations] Spawn: placed " + itemName + " on " + regID + ".");
        return true;
    }

    private static CondOwner Spawn(Ship ship, string itemName, Tile anchor, int size, System.Random rng)
    {
        CondOwner condOwner = DataHandler.GetCondOwner(itemName, null, null, bLoot: false);
        if (condOwner == null)
        {
            Plugin.Log.LogError("[TankStations] Spawn: could not create " + itemName + ".");
            return null;
        }
        if (condOwner.tf != null && anchor != null && anchor.tf != null)
        {
            FootprintBounds(size, out var lo, out var hi);
            float num = (float)(lo + hi) / 2f;
            condOwner.tf.position = new Vector3(anchor.tf.position.x + num, anchor.tf.position.y + num, condOwner.tf.position.z);
        }
        ship.AddCO(condOwner, bTiles: true);
        ApplyWear(condOwner, rng);
        return condOwner;
    }

    private static void ApplyWear(CondOwner co, System.Random rng)
    {
        if (co == null)
        {
            return;
        }
        float num = (float)co.GetCondAmount("StatDamageMax");
        if (!(num <= 0f))
        {
            float num2 = (float)(rng.NextDouble() * (double)MaxWearFraction * (double)num);
            if (num2 > 0f)
            {
                co.AddCondAmount("StatDamage", num2);
            }
        }
    }

    private static void Verbose(string key, string message)
    {
        if (Plugin.VerboseLogging.Value && _logged.Add(key))
        {
            Plugin.Log.LogInfo("[TankStations] " + message);
        }
    }

    private static uint Fnv1a(string s)
    {
        uint num = 2166136261u;
        for (int i = 0; i < s.Length; i++)
        {
            num ^= s[i];
            num *= 16777619;
        }
        return num;
    }

    private static bool IsPlayerInvolved(Ship ship)
    {
        CondOwner selectedCrew = CrewSim.GetSelectedCrew();
        if (selectedCrew != null && selectedCrew.ship == ship)
        {
            return true; // the ship the player is currently on
        }
        string text = CrewSim.system?.GetShipOwner(ship.strRegID);
        return text != null && CrewSim.coPlayer != null && text == CrewSim.coPlayer.strID; // player-owned
    }

    private static bool ShipHasCond(Ship ship, string cond)
    {
        foreach (Room aRoom in ship.aRooms)
        {
            if (aRoom == null || aRoom.aCos == null)
            {
                continue;
            }
            foreach (CondOwner aCo in aRoom.aCos)
            {
                if (aCo != null && aCo.HasCond(cond))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static Tile FindSpot(Ship ship, System.Random rng, int size)
    {
        List<Room> list = new List<Room>();
        foreach (Room aRoom in ship.aRooms)
        {
            if (aRoom != null && !aRoom.Void && aRoom.aTiles != null && aRoom.aTiles.Count > 0)
            {
                list.Add(aRoom);
            }
        }
        Shuffle(list, rng);
        foreach (Room item in list)
        {
            List<Tile> list2 = new List<Tile>();
            List<Tile> list3 = new List<Tile>();
            foreach (Tile aTile in item.aTiles)
            {
                if (FootprintFits(ship, item, aTile, size))
                {
                    list3.Add(aTile);
                    if (!NearPortal(ship, aTile, size))
                    {
                        list2.Add(aTile);
                    }
                }
            }
            List<Tile> list4 = ((list2.Count > 0) ? list2 : list3);
            if (list4.Count > 0)
            {
                return list4[rng.Next(list4.Count)];
            }
        }
        return null;
    }

    private static void FootprintBounds(int size, out int lo, out int hi)
    {
        if (size % 2 == 1)
        {
            lo = -(size / 2);
            hi = size / 2;
        }
        else
        {
            lo = -(size / 2);
            hi = size / 2 - 1;
        }
    }

    private static bool FootprintFits(Ship ship, Room room, Tile tile, int size)
    {
        if (tile == null || tile.tf == null)
        {
            return false;
        }
        FootprintBounds(size, out var lo, out var hi);
        for (int i = lo; i <= hi; i++)
        {
            for (int j = lo; j <= hi; j++)
            {
                Tile tile2 = ((i == 0 && j == 0) ? tile : ship.GetTileAtWorldCoords1(tile.tf.position.x + (float)i, tile.tf.position.y + (float)j, bAllowDocked: false));
                if (tile2 == null || tile2.room != room || !IsFreeFloor(tile2))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsFreeFloor(Tile tile)
    {
        if (tile == null || tile.tf == null || tile.coProps == null || tile.IsWall || tile.IsPortal)
        {
            return false;
        }
        CondOwner coProps = tile.coProps;
        if (coProps.HasCond("IsFloor") && coProps.HasCond("IsFloorSealed") && !coProps.HasCond("IsObstruction") && !coProps.HasCond("IsFixture"))
        {
            return !coProps.HasCond("IsItemTile");
        }
        return false;
    }

    private static bool NearPortal(Ship ship, Tile tile, int size)
    {
        int reach = size / 2 + 1;
        foreach (Tile item in OrthoNeighbors(ship, tile, reach))
        {
            if (item != null && item.IsPortal)
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<Tile> OrthoNeighbors(Ship ship, Tile tile, int reach)
    {
        float x = tile.tf.position.x;
        float y = tile.tf.position.y;
        yield return ship.GetTileAtWorldCoords1(x + (float)reach, y, bAllowDocked: false);
        yield return ship.GetTileAtWorldCoords1(x - (float)reach, y, bAllowDocked: false);
        yield return ship.GetTileAtWorldCoords1(x, y + (float)reach, bAllowDocked: false);
        yield return ship.GetTileAtWorldCoords1(x, y - (float)reach, bAllowDocked: false);
    }

    private static void Shuffle<T>(List<T> list, System.Random rng)
    {
        for (int num = list.Count - 1; num > 0; num--)
        {
            int index = rng.Next(num + 1);
            T value = list[num];
            list[num] = list[index];
            list[index] = value;
        }
    }
}
