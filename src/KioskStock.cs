using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TankStations;

internal static class KioskStock
{
    private static readonly string[] PoolNames = new string[11]
    {
        "ItmOKLGSupplyKioskInv", "ItmSupplyKioskBCERInv", "ItmSupplyKioskBCRSInv", "ItmSupplyKioskInv", "ItmOKLGFurnishingsKioskInv", "ItmFurnishingsKioskBCERInv", "ItmFurnishingsKioskBCRSInv", "ItmTraderSanDiegoKangInv", "ItmTraderSanDiegoTSDOInv", "ItmTraderBCERKioskTSDOInv",
        "ItmTraderBCRSKioskTSDOInv"
    };

    private static string[] OurEntries()
    {
        return new string[2]
        {
            "ItmTankStationT1Loose=1.0x1-2",
            "ItmTankStationT2Loose=" + Plugin.KioskT2Chance.Value.ToString("0.###", CultureInfo.InvariantCulture) + "x1"
        };
    }

    internal static void Install()
    {
        DataHandler.LoadComplete = (Action)Delegate.Combine(DataHandler.LoadComplete, new Action(Inject));
        if (DataHandler.bLoaded)
        {
            Inject();
        }
    }

    private static void Inject()
    {
        try
        {
            if (DataHandler.dictLoot == null)
            {
                Plugin.Log.LogWarning("Kiosk stock: loot table missing after data load; Tank Stations will not be sold.");
                return;
            }
            int num = 0;
            int num2 = 0;
            string[] ourEntries = OurEntries();
            string[] poolNames = PoolNames;
            foreach (string text in poolNames)
            {
                if (!DataHandler.dictLoot.TryGetValue(text, out var value) || value == null)
                {
                    Plugin.Log.LogWarning("Kiosk stock: loot pool '" + text + "' missing after data load; skipping it.");
                    continue;
                }
                string[] array = value.aLoots ?? new string[0];
                HashSet<string> have = new HashSet<string>(array.Select(ItemOf));
                string[] array2 = ourEntries.Where((string e) => !have.Contains(ItemOf(e))).ToArray();
                if (array2.Length != 0)
                {
                    value.aLoots = array.Concat(array2).ToArray();
                    num2 += array2.Length;
                }
                num++;
                if (Plugin.VerboseLogging.Value)
                {
                    Plugin.Log.LogInfo($"Kiosk stock: {text} +{array2.Length}/{ourEntries.Length} (pool now {value.aLoots.Length}).");
                }
            }
            Plugin.Log.LogInfo($"Kiosk stock: stocked {num}/{PoolNames.Length} pool(s), {num2} entrie(s) added.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError("Kiosk stock injection failed: " + ex);
        }
    }

    private static string ItemOf(string entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return entry;
        }
        int num = entry.IndexOf('=');
        return ((num >= 0) ? entry.Substring(0, num) : entry).TrimStart('-');
    }
}
