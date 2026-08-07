using System;
using System.Collections.Generic;

namespace TankStations;

// Tank station behavior

internal static class TankStation
{
    private const double R = 0.008314000442624092;

    private const double DefaultTemp = 293.0;

    private const double FillFraction = 0.999;

    private const double GasFloorMols = 0.01;

    private const double KgFloor = 0.01;

    private const double Epsilon = 0.0001;

    private const float MaxCatchupSeconds = 86400f;

    private const double DensityHe3 = 129.11;

    private const double DensityD2O = 1107.0;

    private const string InstalledCT = "TIsTankStationInstalled";

    private const string DstO2CT = "TIsRTAO2Installed";

    private const string DstN2CT = "TIsRTAN2Installed";

    private const string DstHe3CT = "TIsCanisterLHe02Installed";

    private const string DstD2OCT = "TIsCanisterLH02Installed";

    private const string PoweredCond = "IsPowered";

    private const string DumpCond = "IsReverseOn";

    private static double _fLast = -1.0;

    private static readonly Dictionary<string, string> _lastStatus = new Dictionary<string, string>();

    private static CondTrigger _ctDstO2;

    private static CondTrigger _ctDstN2;

    private static CondTrigger _ctDstHe3;

    private static CondTrigger _ctDstD2O;

    private static bool _ctsTried;

    public static void Run()
    {
        Ship ship = CrewSim.coPlayer?.ship;
        if (ship == null)
        {
            _fLast = -1.0;
            _lastStatus.Clear();
            return;
        } // Ship exists
        CondTrigger condTrigger = DataHandler.GetCondTrigger(InstalledCT);
        if (condTrigger == null)
        {
            return;
        } // The mod properly defined the condtrig for an installed tank station
        List<CondOwner> iCOs = ship.GetICOs1(condTrigger, bSubObjects: false, bAllowDocked: false, bAllowLocked: true);
        if (iCOs == null || iCOs.Count == 0)
        {
            _fLast = StarSystem.fEpoch;
            return;
        } // At least one installed tank station exists
        double fEpoch = StarSystem.fEpoch;
        if (_fLast < 0.0)
        {
            _fLast = fEpoch;
            return;
        } // First Run: set last time and skip
        float num = (float)(fEpoch - _fLast);
        _fLast = fEpoch;
        if (num <= 0f)
        {
            return;
        } // We did not go backwards in time
        if (num > MaxCatchupSeconds) // If massive time skips occur, cap the maximum calculated flow
        {
            num = MaxCatchupSeconds;
        }
        foreach (CondOwner item in iCOs)
        {
            try
            {
                ProcessOne(item, num);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[TankStations] tick failed on " + item?.strID + ": " + ex);
            }
        }
    }

    private static void ProcessOne(CondOwner station, float dtGame)
    {
        if (station == null || station.bDestroyed)
        {
            return;
        }
        int num = TierOf(station);
        if (num == 0)
        {
            return;
        }
        if (station.GetCondAmount(PoweredCond) <= 0.0)
        {
            LogStatus(station, "IDLE: no power / switched OFF (wire it to power and set the panel to AUTO or ON)", 0, 0.0);
            return;
        }
        bool flag = station.HasCond(DumpCond);
        List<CondOwner> list = new List<CondOwner>();
        List<CondOwner> cOsSafe = station.GetCOsSafe(bAllowLocked: false);
        if (cOsSafe != null)
        {
            foreach (CondOwner item in cOsSafe)
            {
                if (AcceptedSource(item, num))
                {
                    list.Add(item);
                }
            }
        }
        EnsureCTs();
        List<CondOwner> list2 = new List<CondOwner>();
        List<CondOwner> list3 = new List<CondOwner>();
        List<CondOwner> list4 = new List<CondOwner>();
        List<CondOwner> list5 = new List<CondOwner>();
        Ship ship = station.ship;
        if (ship != null)
        {
            CollectDsts(ship, station, list2, list3, list4, list5);
        }
        double num2 = NeededGas(list2, "StatGasMolO2");
        double num3 = NeededGas(list3, "StatGasMolN2");
        double num4 = NeededLiquid(list4, "StatSolidHe3", DensityHe3);
        double num5 = NeededLiquid(list5, "StatLiqD2O", DensityD2O);
        if (flag)
        {
            num3 = (num4 = (num5 = 1000000000.0));
        }
        int num6 = 0;
        foreach (CondOwner item2 in list)
        {
            if (SourceActive(item2, num2, num3, num4, num5))
            {
                num6++;
            }
        }
        if (num6 == 0)
        {
            LogStatus(station, (list.Count == 0) ? "IDLE: no tanks loaded (drop salvaged tanks into the station's hopper)" : "IDLE: nothing to do (ship's tanks are full, or loaded tanks are empty)", list.Count, 0.0);
            return;
        }
        double num7 = (double)FlowForTier(num) * (double)dtGame / num6; // TODO: Consider lim (n -> inf) (1 + 1/n)^n
        double num8 = 0.0;
        double num9 = 0.0;
        double num10 = 0.0;
        double num11 = 0.0;
        foreach (CondOwner item3 in list)
        {
            if (item3 == null || item3.bDestroyed)
            {
                continue;
            }
            if (item3.HasCond("IsVesselO2") && num2 > Epsilon)
            {
                double num12 = MoveGas(item3, "O2", "StatGasMolO2", num7, num2, list2);
                num8 += num12;
                num2 -= num12;
            }
            else if (item3.HasCond("IsVesselN2") && num3 > Epsilon)
            {
                double num13 = MoveGas(item3, "N2", "StatGasMolN2", num7, num3, list3);
                num9 += num13;
                num3 -= num13;
            }
            else if (item3.HasCond("IsVesselHe3") && num4 > Epsilon)
            {
                double num14 = MoveLiquid(item3, "StatSolidHe3", DensityHe3, num7, num4, list4);
                num10 += num14;
                num4 -= num14;
            }
            else if (item3.HasCond("IsVesselH2") && num5 > Epsilon)
            {
                double num15 = MoveLiquid(item3, "StatLiqD2O", DensityD2O, num7, num5, list5);
                num11 += num15;
                num5 -= num15;
            }
        }
        double num16 = num8 + num9 + num10 + num11;
        if (num16 > Epsilon)
        {
            LogStatus(station, $"PUMPING O2 +{num8:0.###} mol, N2 +{num9:0.###} mol, He3 +{num10:0.###} kg, D2O +{num11:0.###} kg" + (flag ? " (DUMP on)" : ""), list.Count, num16);
        }
        else
        {
            LogStatus(station, "IDLE: loaded tanks are nearly empty", list.Count, 0.0);
        }
    }

    private static int TierOf(CondOwner co)
    {
        if (co.HasCond("IsTankStationT1"))
        {
            return 1;
        }
        if (co.HasCond("IsTankStationT2"))
        {
            return 2;
        }
        if (co.HasCond("IsTankStationT3"))
        {
            return 3;
        }
        if (co.HasCond("IsTankStationT4"))
        {
            return 4;
        }
        return 0;
    }

    private static float FlowForTier(int tier)
    {
        return tier switch
        {
            1 => Plugin.FlowT1.Value,
            2 => Plugin.FlowT2.Value,
            3 => Plugin.FlowT3.Value,
            4 => Plugin.FlowT4.Value,
            _ => 0f,
        };
    }

    private static bool AcceptedSource(CondOwner co, int tier)
    {
        if (co == null || co.bDestroyed)
        {
            return false;
        }
        if (co.HasCond("IsRTA") && (co.HasCond("IsVesselO2") || co.HasCond("IsVesselN2")))
        {
            return true;
        }
        if (tier >= 2 && (co.HasCond("IsVesselHe3") || co.HasCond("IsVesselH2")))
        {
            return true;
        }
        return false;
    }

    private static bool SourceActive(CondOwner co, double needO2, double needN2, double needHe3, double needD2O)
    {
        if (co == null || co.bDestroyed)
        {
            return false;
        }
        if (co.HasCond("IsVesselO2"))
        {
            return needO2 > Epsilon && co.GetCondAmount("StatGasMolO2") > GasFloorMols + Epsilon;
        }
        if (co.HasCond("IsVesselN2"))
        {
            return needN2 > Epsilon && co.GetCondAmount("StatGasMolN2") > GasFloorMols + Epsilon;
        }
        if (co.HasCond("IsVesselHe3"))
        {
            return needHe3 > Epsilon && co.GetCondAmount("StatSolidHe3") > KgFloor + Epsilon;
        }
        if (co.HasCond("IsVesselH2"))
        {
            return needD2O > Epsilon && co.GetCondAmount("StatLiqD2O") > KgFloor + Epsilon;
        }
        return false;
    }

    private static double MoveGas(CondOwner src, string species, string statMol, double liters, double needLeft, List<CondOwner> dsts)
    {
        GasContainer gasContainer = src.GasContainer;
        if (gasContainer == null)
        {
            return 0.0;
        }
        double num = src.GetCondAmount(statMol) - GasFloorMols;
        double condAmount = src.GetCondAmount("StatVolume");
        if (num <= Epsilon || condAmount <= 0.0)
        {
            return 0.0;
        }
        double num2 = liters / 1000.0 * (num / condAmount);
        double num3 = Math.Min(Math.Min(num2, num), Math.Max(needLeft, 0.0));
        if (num3 <= Epsilon)
        {
            return 0.0;
        }
        gasContainer.AddGasMols(species, 0.0 - num3);
        double num4 = num3;
        foreach (CondOwner dst in dsts)
        {
            num4 -= AddGas(dst, species, statMol, num4);
            if (num4 <= Epsilon)
            {
                break;
            }
        }
        return num3 - num4;
    }

    private static double AddGas(CondOwner dst, string species, string statMol, double mols)
    {
        if (dst == null || dst.bDestroyed)
        {
            return 0.0;
        }
        GasContainer gasContainer = dst.GasContainer;
        if (gasContainer == null)
        {
            return 0.0;
        }
        double num = GasRoomMols(dst, statMol);
        if (num <= Epsilon)
        {
            return 0.0;
        }
        double num2 = Math.Min(num, mols);
        gasContainer.AddGasMols(species, num2);
        return num2;
    }

    private static double GasRoomMols(CondOwner co, string statMol)
    {
        double num = GasCapMols(co);
        if (num <= 0.0)
        {
            return 0.0;
        }
        double condAmount = co.GetCondAmount("StatVolume");
        double num2 = co.GetCondAmount("StatGasTemp");
        if (num2 <= 0.0)
        {
            num2 = DefaultTemp;
        }
        double condAmount2 = co.GetCondAmount("StatGasPressureMax");
        double num3 = Math.Max(0.0, FillFraction * condAmount2 - co.GetCondAmount("StatGasPressure")) * condAmount / (R * num2);
        double num4 = Math.Max(0.0, num - co.GetCondAmount(statMol));
        return Math.Min(num3, num4);
    }

    private static double GasCapMols(CondOwner co)
    {
        double condAmount = co.GetCondAmount("StatGasPressureMax");
        double condAmount2 = co.GetCondAmount("StatVolume");
        double num = co.GetCondAmount("StatGasTemp");
        if (num <= 0.0)
        {
            num = DefaultTemp;
        }
        if (condAmount <= 0.0 || condAmount2 <= 0.0)
        {
            return 0.0;
        }
        return FillFraction * condAmount * condAmount2 / (R * num);
    }

    private static double MoveLiquid(CondOwner src, string stat, double density, double liters, double needLeft, List<CondOwner> dsts)
    {
        double num = src.GetCondAmount(stat) - KgFloor;
        if (num <= Epsilon)
        {
            return 0.0;
        }
        double val = liters / 1000.0 * density;
        double num2 = Math.Min(Math.Min(val, num), Math.Max(needLeft, 0.0));
        if (num2 <= Epsilon)
        {
            return 0.0;
        }
        src.AddCondAmount(stat, 0.0 - num2);
        double num3 = num2;
        foreach (CondOwner dst in dsts)
        {
            num3 -= AddLiquid(dst, stat, density, num3);
            if (num3 <= Epsilon)
            {
                break;
            }
        }
        return num2 - num3;
    }

    private static double AddLiquid(CondOwner dst, string stat, double density, double kg)
    {
        if (dst == null || dst.bDestroyed)
        {
            return 0.0;
        }
        double num = LiquidCapKg(dst, density) - dst.GetCondAmount(stat);
        if (num <= Epsilon)
        {
            return 0.0;
        }
        double num2 = Math.Min(num, kg);
        dst.AddCondAmount(stat, num2);
        return num2;
    }

    private static double LiquidCapKg(CondOwner co, double density)
    {
        double condAmount = co.GetCondAmount("StatVolume");
        if (condAmount <= 0.0)
        {
            return 0.0;
        }
        return condAmount * density;
    }

    private static double NeededGas(List<CondOwner> dsts, string statMol)
    {
        double num = 0.0;
        foreach (CondOwner dst in dsts)
        {
            if (dst != null && !dst.bDestroyed)
            {
                num += GasRoomMols(dst, statMol);
            }
        }
        return num;
    }

    private static double NeededLiquid(List<CondOwner> dsts, string stat, double density)
    {
        double num = 0.0;
        foreach (CondOwner dst in dsts)
        {
            if (dst != null && !dst.bDestroyed)
            {
                num += Math.Max(0.0, LiquidCapKg(dst, density) - dst.GetCondAmount(stat));
            }
        }
        return num;
    }

    private static void EnsureCTs()
    {
        if (_ctsTried)
        {
            return;
        }
        _ctsTried = true;
        _ctDstO2 = DataHandler.GetCondTrigger(DstO2CT);
        _ctDstN2 = DataHandler.GetCondTrigger(DstN2CT);
        _ctDstHe3 = DataHandler.GetCondTrigger(DstHe3CT);
        _ctDstD2O = DataHandler.GetCondTrigger(DstD2OCT);
    }

    private static void CollectDsts(Ship ship, CondOwner station, List<CondOwner> dstO2, List<CondOwner> dstN2, List<CondOwner> dstHe3, List<CondOwner> dstD2O)
    {
        // Installed tanks, anywhere on the ship, are valid destinations
        Collect(ship, station, _ctDstO2, dstO2, bGas: true);
        Collect(ship, station, _ctDstN2, dstN2, bGas: true); // Different validity for life support N2 than the refuel kiosk. The refuel kiosk does a check for air pumps and what's under them. This tolerates any installed N2 can, even those serving as RCS N2
        // TODO: I might want to make RCS N2 a higher fill priority than room pressurization N2. If your atmo cans run out your room still has an O2 / N2 mix and you have no immediate effects. If your RCS N2 runs out you're stranded.
        Collect(ship, station, _ctDstHe3, dstHe3, bGas: false);
        Collect(ship, station, _ctDstD2O, dstD2O, bGas: false);
        foreach (CondOwner item in ship.GetRCSCans()) // GetRCSCans allows loose N2 cans
        {
            if (item != null && !item.bDestroyed && !(item.ship != ship) && !IsInside(item, station) && item.GasContainer != null && !dstN2.Contains(item))
            {
                dstN2.Add(item);
            }
        }
    }

    private static void Collect(Ship ship, CondOwner station, CondTrigger ct, List<CondOwner> outp, bool bGas)
    {
        if (ct == null)
        {
            return;
        }
        List<CondOwner> iCOs = ship.GetICOs1(ct, bSubObjects: true, bAllowDocked: false, bAllowLocked: true);
        if (iCOs == null)
        {
            return;
        }
        foreach (CondOwner item in iCOs)
        {
            // Cans that are not destroyed (can hold pressure), on this ship, not inside the tank station's internal inventory, has a gas container if we're inserting O2 or N2, and not already in the list
            if (item != null && !item.bDestroyed && !(item.ship != ship) && !IsInside(item, station) && (!bGas || item.GasContainer != null) && !outp.Contains(item))
            {
                outp.Add(item);
            }
        }
    }

    private static bool IsInside(CondOwner co, CondOwner container)
    {
        for (CondOwner condOwner = co.objCOParent; condOwner != null; condOwner = condOwner.objCOParent)
        {
            if (condOwner == container)
            {
                return true;
            }
        }
        return false;
    }

    private static void LogStatus(CondOwner station, string text, int srcCount, double moved)
    {
        if (!Plugin.VerboseLogging.Value)
        {
            return;
        }
        string text2 = text + $"|srcs={srcCount} moved={moved:0.###}";
        if (!_lastStatus.TryGetValue(station.strID, out var value) || value != text2)
        {
            _lastStatus[station.strID] = text2;
            Plugin.Log.LogInfo("[TankStation " + station.strID + "] " + text);
        }
    }
}
