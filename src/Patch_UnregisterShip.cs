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
            if (Plugin.KeepDrainVictims.Value && aiShip?.Ship != null && aiShip.Ship.ShipCO != null && aiShip.Ship.ShipCO.HasCond(ShallowFuel.VictimCond))
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
}

[HarmonyPatch(typeof(FlyTo), "TryInstantCleanup")]
internal static class Patch_TryInstantCleanup
{
    private static bool Prefix(FlyTo __instance)
    {
        try
        {
            if (Plugin.KeepDrainVictims.Value && __instance?.ShipUs != null && __instance.ShipUs.ShipCO != null && __instance.ShipUs.ShipCO.HasCond(ShallowFuel.VictimCond))
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
