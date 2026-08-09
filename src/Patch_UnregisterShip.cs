using HarmonyLib;
using Ostranauts.Ships.AIPilots;
using Ostranauts.Ships.Commands;

namespace TankStations;

// Keep T4 drain victims from despawning. Vanilla chain: FlyTo.IsOutOfFuelApproximation ->
// RequestHelp -> AIShipManager.UnregisterShip(ship) [+ FlyTo.TryInstantCleanup backdating
// IsStale directly], then StarSystem.CleanupStaleShips deletes stale ships. Both entry points
// are blocked while the ShipCO carries IsTankDrainVictim, so a drained ship sits inert (its AI
// re-fails FlyTo every 64s - harmless) instead of vanishing before the player can board it.
// Clear the cond (or disable the config) to release the ship to normal cleanup.
[HarmonyPatch(typeof(AIShipManager), "UnregisterShip", new System.Type[] { typeof(AIShip) })]
internal static class Patch_UnregisterShip
{
    private static bool Prefix(AIShip aiShip)
    {
        try
        {
            Ship ship = aiShip?.Ship;
            if (Plugin.KeepDrainVictims.Value && ship != null && !ship.bDestroyed && ship.ShipCO != null && ship.ShipCO.HasCond(ShallowFuel.VictimCond) && !ShipIsBeingTornDown(ship))
            {
                return false; // skip: keep the drained ship registered and inert
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError("[TankStations] UnregisterShip prefix failed: " + ex);
        }
        return true;
    }

    // Ship.Destroy() removes the ship from StarSystem.dictShips and only THEN calls
    // UnregisterShip, with bDestroyed still false. If we blocked that unregister, the dead ship
    // stayed in dictAIs; its AIShip.Ship getter later turns the reference into a REAL null
    // (GetShipByRegID miss) and the next GetAIShipByRegID caller NREs - which is what broke
    // quit-to-menu / save loading. So: never block a ship the system no longer tracks.
    private static bool ShipIsBeingTornDown(Ship ship)
    {
        if (CrewSim.system == null)
        {
            return true; // scene teardown
        }
        return CrewSim.system.GetShipByRegID(ship.strRegID) == null;
    }
}

[HarmonyPatch(typeof(FlyTo), "TryInstantCleanup")]
internal static class Patch_TryInstantCleanup
{
    private static bool Prefix(FlyTo __instance)
    {
        try
        {
            Ship shipUs = __instance?.ShipUs;
            if (Plugin.KeepDrainVictims.Value && shipUs != null && !shipUs.bDestroyed && shipUs.ShipCO != null && shipUs.ShipCO.HasCond(ShallowFuel.VictimCond))
            {
                return false; // skip: don't backdate IsStale on a drain victim
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError("[TankStations] TryInstantCleanup prefix failed: " + ex);
        }
        return true;
    }
}
