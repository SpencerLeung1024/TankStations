using System;
using System.Collections.Generic;
using System.Globalization;

namespace TankStations;

// Tier-4 shallow-target fuel model, drain ledger, and dock reconciliation.
//
// A ship at LoadState.Shallow has no item COs; its fuel lives in three baked fields:
// fShallowRCSRemass (kg N2 at RCS intakes), fShallowFusionRemain (seconds of full-power torch
// burn), and fShallowRCSRemassMax. O2 has NO shallow representation. So remote draining works
// like this:
//   N2   - decrement fShallowRCSRemass directly (AI stops thrusting at 0), record kg in ledger.
//   He3  - kg now = template kg - burned - ledger; decrement fShallowFusionRemain by kg/rate.
//   D2O  - same with its own rate (burn ratio D2O:He3 = 0.667:1.0 by mass, from FusionIC).
//   O2   - ledger only; subtracted from the real cans when the ship loads (ReconcileOnLoad).
// The ledger is a set of StatTankDrain* kg conds (bPersists) on the victim's ShipCO, plus the
// IsTankDrainVictim marker cond that Patch_UnregisterShip uses to block fuel-despawn.
//
// Template totals come from the PRISTINE template in DataHandler.dictShips (clones are made at
// spawn, so dictShips entries keep editor-baked full-tank values); for previously visited ships
// the live ship.json.aItems + DataHandler.dictCOSaves per-CO saves give exact contents. Loot
// spawner contents are in neither - for N2 the fShallowRCSRemassMax view backstops that gap.
internal static class ShallowFuel
{
    public const string VictimCond = "IsTankDrainVictim";

    // Resource indices used everywhere in this file.
    public const int O2 = 0;

    public const int N2 = 1;

    public const int He3 = 2;

    public const int D2O = 3;

    private static readonly string[] Names = { "O2", "N2", "He3", "D2O" };

    // Contents cond per resource on a source tank (mols for gases, kg for liquids).
    private static readonly string[] ContentStat = { "StatGasMolO2", "StatGasMolN2", "StatSolidHe3", "StatLiqD2O" };

    // Vessel marker cond per resource on the CO definition.
    private static readonly string[] VesselCond = { "IsVesselO2", "IsVesselN2", "IsVesselHe3", "IsVesselH2" };

    private static readonly double[] MolarMass = { 0.0319988, 0.0280134, 0.0, 0.0 }; // kg/mol, gases only

    private const double R = 0.008314000442624092; // kPa m^3 / (mol K)

    private const double RtaPmax = 41400.0; // rated max pressure of vanilla RTA cans (kPa)

    private const double RtaTemp = 293.0; // rated temp of vanilla RTA cans (K)

    private const double D2OPerHe3 = 0.667; // fusion burn mass ratio (FusionIC reactants)

    private const double KgFloor = 0.01;

    // Everything we know about one shallow target's tanks, in kg. Cached by regID.
    public class TemplateInv
    {
        public readonly double[] Kg = new double[4]; // template totals (full / last-save state)

        public readonly double[] VirtualVolM3 = new double[4]; // gases: virtual source volume (flow decay)

        public double BakedFusionSec; // template fShallowFusionRemain (templates are saved full)

        public double He3Rate; // kg/s at full torch burn (0 if not derivable)

        public double D2ORate;
    }

    private static readonly Dictionary<string, TemplateInv> _cache = new Dictionary<string, TemplateInv>();

    // TankStation.Res index <-> our fixed resource index, by short name.
    public static int IdxOf(string name)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            if (Names[i] == name)
            {
                return i;
            }
        }
        return -1;
    }

    public static void Invalidate(string regID)
    {
        if (regID != null)
        {
            _cache.Remove(regID);
        }
    }

    // Ledger kg drained so far. shipCO = target.ShipCO.
    public static double GetLedgerKg(CondOwner shipCO, int res)
    {
        return shipCO?.GetCondAmount("StatTankDrain" + Names[res]) ?? 0.0;
    }

    public static bool HasAnyLedger(Ship ship)
    {
        CondOwner shipCO = ship?.ShipCO;
        if (shipCO == null)
        {
            return false;
        }
        for (int i = 0; i < Names.Length; i++)
        {
            if (GetLedgerKg(shipCO, i) > KgFloor)
            {
                return true;
            }
        }
        return false;
    }

    // kg of `res` currently drainable from the shallow target (template view minus ledger, with
    // the live shallow fields as a second opinion where they exist).
    public static double AvailableKg(Ship target, TemplateInv inv, int res)
    {
        if (target == null || inv == null)
        {
            return 0.0;
        }
        double num = GetLedgerKg(target.ShipCO, res);
        switch (res)
        {
            case O2:
                return Math.Max(0.0, inv.Kg[O2] - num);
            case N2:
            {
                double num2 = Math.Max(inv.Kg[N2], target.GetRCSMax());
                return Math.Max(0.0, Math.Min(num2 - num, target.fShallowRCSRemass));
            }
            case He3:
            case D2O:
            {
                double num3 = ((res == He3) ? inv.He3Rate : inv.D2ORate);
                if (num3 <= 0.0 || inv.BakedFusionSec <= 0.0)
                {
                    return 0.0;
                }
                double num4 = Math.Max(0.0, inv.BakedFusionSec - Math.Max(0.0, target.fShallowFusionRemain));
                return Math.Max(0.0, inv.Kg[res] - num4 * num3 - num);
            }
            default:
                return 0.0;
        }
    }

    // Remove up to kgWant kg of `res` from the shallow target: updates the live shallow fields,
    // adds to the ledger, and marks the ship as a drain victim. Returns kg actually removed.
    public static double RemoveKg(Ship target, TemplateInv inv, int res, double kgWant)
    {
        if (target == null || inv == null || target.ShipCO == null || kgWant <= 0.0)
        {
            return 0.0;
        }
        double num = Math.Min(kgWant, AvailableKg(target, inv, res));
        if (num <= KgFloor)
        {
            return 0.0;
        }
        switch (res)
        {
            case N2:
                target.fShallowRCSRemass = Math.Max(0.0, target.fShallowRCSRemass - num);
                break;
            case He3:
            case D2O:
            {
                double num2 = ((res == He3) ? inv.He3Rate : inv.D2ORate);
                if (num2 > 0.0)
                {
                    target.fShallowFusionRemain = Math.Max(0.0, target.fShallowFusionRemain - num / num2);
                }
                break;
            }
        }
        target.ShipCO.AddCondAmount("StatTankDrain" + Names[res], num);
        target.ShipCO.SetCondAmount(VictimCond, 1.0);
        if (res == He3 || res == D2O)
        {
            ClampFusionConsistency(target, inv);
        }
        return num;
    }

    // After editing fusion seconds, keep them consistent with the remaining kg of BOTH reactants
    // (draining either one must be able to zero the torch; the other must never imply more burn
    // time than its own remaining mass allows).
    private static void ClampFusionConsistency(Ship target, TemplateInv inv)
    {
        double num = Math.Max(0.0, target.fShallowFusionRemain);
        double num2 = double.PositiveInfinity;
        if (inv.He3Rate > 0.0)
        {
            num2 = Math.Min(num2, AvailableKg(target, inv, He3) / inv.He3Rate);
        }
        if (inv.D2ORate > 0.0)
        {
            num2 = Math.Min(num2, AvailableKg(target, inv, D2O) / inv.D2ORate);
        }
        if (!double.IsPositiveInfinity(num2))
        {
            num = Math.Min(num, Math.Max(0.0, num2));
        }
        target.fShallowFusionRemain = num;
    }

    // Virtual source volume (m^3) for remote gas drains, so flow decays as the target empties
    // just like a real can: the volume the template's gas would occupy at rated max pressure.
    public static double VirtualVolumeM3(TemplateInv inv, int res)
    {
        return inv?.VirtualVolM3[res] ?? 0.0;
    }

    public static double MolarMassOf(int res)
    {
        return MolarMass[res];
    }

    // Build (or fetch cached) the template inventory for a target ship.
    public static TemplateInv GetInventory(Ship target)
    {
        if (target == null)
        {
            return null;
        }
        string text = target.strRegID;
        if (text != null && _cache.TryGetValue(text, out var value))
        {
            return value;
        }
        value = BuildInventory(target);
        if (text != null)
        {
            _cache[text] = value;
        }
        return value;
    }

    private static TemplateInv BuildInventory(Ship target)
    {
        TemplateInv templateInv = new TemplateInv();
        // Prefer the pristine template (editor-baked, full tanks) over the live json.
        JsonShip jsonShip = null;
        string text = target.json?.strName;
        if (!string.IsNullOrEmpty(text) && DataHandler.dictShips.TryGetValue(text, out var value))
        {
            jsonShip = value;
        }
        jsonShip = jsonShip ?? target.json;
        if (jsonShip == null)
        {
            return templateInv;
        }
        templateInv.BakedFusionSec = Math.Max(0.0, jsonShip.fShallowFusionRemain);
        if (jsonShip.aItems != null)
        {
            foreach (JsonItem jsonItem in jsonShip.aItems)
            {
                if (jsonItem?.strName == null)
                {
                    continue;
                }
                for (int j = 0; j < Names.Length; j++)
                {
                    double num = ReadContentKg(jsonItem, j);
                    if (num > 0.0)
                    {
                        templateInv.Kg[j] += num;
                    }
                }
            }
        }
        // Fusion burn rates from the limiting reactant: templates are saved full, so baked
        // seconds correspond to the template kg. D2O burns at 0.667 kg per 1.0 kg He3.
        if (templateInv.BakedFusionSec > 0.0 && templateInv.Kg[He3] > 0.0 && templateInv.Kg[D2O] > 0.0)
        {
            if (templateInv.Kg[D2O] / D2OPerHe3 >= templateInv.Kg[He3])
            {
                templateInv.He3Rate = templateInv.Kg[He3] / templateInv.BakedFusionSec;
                templateInv.D2ORate = templateInv.He3Rate * D2OPerHe3;
            }
            else
            {
                templateInv.D2ORate = templateInv.Kg[D2O] / templateInv.BakedFusionSec;
                templateInv.He3Rate = templateInv.D2ORate / D2OPerHe3;
            }
        }
        // Virtual gas volumes: what the template gas would occupy at rated max pressure.
        for (int k = 0; k <= 1; k++)
        {
            if (templateInv.Kg[k] > 0.0)
            {
                double num2 = templateInv.Kg[k] / MolarMass[k];
                templateInv.VirtualVolM3[k] = num2 * R * RtaTemp / RtaPmax;
            }
        }
        // N2 cross-check: the RCS bookkeeping view can see cans the item enumeration missed.
        double num3 = Math.Max(0.0, target.fShallowRCSRemassMax);
        if (num3 > templateInv.Kg[N2])
        {
            templateInv.Kg[N2] = num3;
            double num4 = num3 / MolarMass[N2];
            templateInv.VirtualVolM3[N2] = num4 * R * RtaTemp / RtaPmax;
        }
        return templateInv;
    }

    // kg of `res` in one template item: exact per-CO save when available, else definition
    // starting conds. Only items that are vessels of the right type count.
    private static double ReadContentKg(JsonItem item, int res)
    {
        if (!DataHandler.dictCOs.TryGetValue(item.strName, out var value) || value?.aStartingConds == null)
        {
            return 0.0;
        }
        if (!HasStringPrefix(value.aStartingConds, VesselCond[res]))
        {
            return 0.0;
        }
        if (res == O2 || res == N2)
        {
            if (!HasStringPrefix(value.aStartingConds, "IsRTA"))
            {
                return 0.0;
            }
        }
        else if (HasStringPrefix(value.aStartingConds, "IsRTA"))
        {
            return 0.0; // liquids live in the big non-RTA canisters; don't double count
        }
        if (item.strID != null && DataHandler.dictCOSaves != null && DataHandler.dictCOSaves.TryGetValue(item.strID, out var value2) && value2?.aConds != null)
        {
            double num = FindStat(value2.aConds, ContentStat[res], value.aStartingConds);
            if (num >= 0.0)
            {
                return ToKg(res, num);
            }
        }
        double num2 = FindStat(value.aStartingConds, ContentStat[res], null);
        if (!(num2 >= 0.0))
        {
            return 0.0;
        }
        return ToKg(res, num2);
    }

    private static double ToKg(int res, double statAmount)
    {
        if (res != O2 && res != N2)
        {
            return statAmount;
        }
        return statAmount * MolarMass[res];
    }

    private static bool HasStringPrefix(string[] conds, string name)
    {
        foreach (string text in conds)
        {
            if (text != null && text.StartsWith(name, StringComparison.Ordinal) && (text.Length == name.Length || text[name.Length] == '='))
            {
                return true;
            }
        }
        return false;
    }

    // Find a stat in a cond string list ("Name=1.0xAmount"), optionally expanding the "DEFAULT"
    // save marker against the definition's starting conds. Returns -1 when absent.
    private static double FindStat(string[] conds, string stat, string[] defConds)
    {
        double num = -1.0;
        foreach (string text in conds)
        {
            if (text == null)
            {
                continue;
            }
            if (text == "DEFAULT" && defConds != null)
            {
                double num2 = FindStat(defConds, stat, null);
                if (num2 >= 0.0)
                {
                    num = ((num >= 0.0) ? (num + num2) : num2);
                }
                continue;
            }
            double num3 = ParseCond(text, stat);
            if (num3 >= 0.0)
            {
                num = ((num >= 0.0) ? (num + num3) : num3);
            }
        }
        return num;
    }

    private static double ParseCond(string entry, string stat)
    {
        if (!entry.StartsWith(stat, StringComparison.Ordinal) || entry.Length <= stat.Length || entry[stat.Length] != '=')
        {
            return -1.0;
        }
        string text = entry.Substring(stat.Length + 1);
        int num = text.IndexOf('x');
        if (num < 0)
        {
            return -1.0;
        }
        if (!double.TryParse(text.Substring(0, num), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return -1.0;
        }
        if (!double.TryParse(text.Substring(num + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var result2))
        {
            return -1.0;
        }
        return result * result2;
    }

    // Dock reconciliation (DESIGN §7): a ship we drained while shallow just loaded for real.
    // Subtract the ledger kg from the realized tanks, then zero the ledger. Runs AFTER BreakIn /
    // loot spawners (postfix on InitShip), BEFORE the delayed vanilla SyncFuel - which then only
    // has to drain cans toward the already-reduced fShallowRCSRemass, so no double-drain.
    public static void ReconcileOnLoad(Ship ship)
    {
        try
        {
            if (ship == null || ship.ShipCO == null || !HasAnyLedger(ship))
            {
                return;
            }
            string text = ship.strRegID;
            CondOwner shipCO = ship.ShipCO;
            double num = GetLedgerKg(shipCO, O2);
            double num2 = GetLedgerKg(shipCO, N2);
            double num3 = GetLedgerKg(shipCO, He3);
            double num4 = GetLedgerKg(shipCO, D2O);
            if (num > KgFloor)
            {
                SubtractGas(ship, "TIsRTAO2Installed", "O2", "StatGasMolO2", num / MolarMass[O2], bIncludeRCSCans: false);
            }
            if (num2 > KgFloor)
            {
                SubtractGas(ship, "TIsRTAN2Installed", "N2", "StatGasMolN2", num2 / MolarMass[N2], bIncludeRCSCans: true);
            }
            if (num3 > KgFloor)
            {
                SubtractLiquid(ship, "TIsCanisterLHe02Installed", "StatSolidHe3", num3);
            }
            if (num4 > KgFloor)
            {
                SubtractLiquid(ship, "TIsCanisterLH02Installed", "StatLiqD2O", num4);
            }
            for (int i = 0; i < Names.Length; i++)
            {
                shipCO.ZeroCondAmount("StatTankDrain" + Names[i]);
            }
            shipCO.ZeroCondAmount("StatTankDrainH2O");
            Invalidate(text);
            Plugin.Log.LogInfo($"[TankStations] Reconciled drain ledger on {text}: O2 {num:0.##} kg, N2 {num2:0.##} kg, He3 {num3:0.##} kg, D2O {num4:0.##} kg.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError("[TankStations] ReconcileOnLoad failed: " + ex);
        }
    }

    private static void SubtractGas(Ship ship, string ctName, string species, string statMol, double mols, bool bIncludeRCSCans)
    {
        if (mols <= 0.0)
        {
            return;
        }
        List<CondOwner> list = new List<CondOwner>();
        CondTrigger condTrigger = DataHandler.GetCondTrigger(ctName);
        if (condTrigger != null)
        {
            List<CondOwner> iCOs = ship.GetICOs1(condTrigger, bSubObjects: true, bAllowDocked: false, bAllowLocked: true);
            if (iCOs != null)
            {
                list.AddRange(iCOs);
            }
        }
        if (bIncludeRCSCans)
        {
            foreach (CondOwner item in ship.GetRCSCans())
            {
                if (item != null && !list.Contains(item))
                {
                    list.Add(item);
                }
            }
        }
        double num = mols;
        foreach (CondOwner item2 in list)
        {
            if (num <= 0.0)
            {
                break;
            }
            if (!(item2 == null) && !item2.bDestroyed && !(item2.GasContainer == null))
            {
                double num2 = Math.Min(item2.GetCondAmount(statMol), num);
                if (num2 > 0.0)
                {
                    item2.GasContainer.AddGasMols(species, 0.0 - num2);
                    num -= num2;
                }
            }
        }
        if (num > 0.0)
        {
            Plugin.Log.LogInfo($"[TankStations] Reconcile: {ship.strRegID} had {num:0.##} mol less {species} than the ledger (BreakIn/salvage variance).");
        }
    }

    private static void SubtractLiquid(Ship ship, string ctName, string stat, double kg)
    {
        if (kg <= 0.0)
        {
            return;
        }
        CondTrigger condTrigger = DataHandler.GetCondTrigger(ctName);
        if (condTrigger == null)
        {
            return;
        }
        List<CondOwner> iCOs = ship.GetICOs1(condTrigger, bSubObjects: true, bAllowDocked: false, bAllowLocked: true);
        if (iCOs == null)
        {
            return;
        }
        double num = kg;
        foreach (CondOwner item in iCOs)
        {
            if (num <= 0.0)
            {
                break;
            }
            if (!(item == null) && !item.bDestroyed)
            {
                double num2 = Math.Min(item.GetCondAmount(stat), num);
                if (num2 > 0.0)
                {
                    item.AddCondAmount(stat, 0.0 - num2);
                    num -= num2;
                }
            }
        }
        if (num > 0.0)
        {
            Plugin.Log.LogInfo($"[TankStations] Reconcile: {ship.strRegID} had {num:0.##} kg less {stat} than the ledger (BreakIn/salvage variance).");
        }
    }
}
