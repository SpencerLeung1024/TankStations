using System;
using System.Collections.Generic;
using System.Text;

namespace TankStations;

// Tank station behavior
//
// Every 2 real seconds (configurable) Plugin.Update calls Run(), which finds every installed,
// undamaged tank station on the player's ship and pumps for it. Each station pumps from its
// "sources" (salvaged tanks loaded into its hopper by the player) to "destinations" (the ship's
// own installed tanks, plus any cans feeding RCS intake regulators). Rates are in swept source
// liters per game-second: gases move mols = L/1000 x (mols of that species / StatVolume), so a
// source slows exponentially as it empties; liquids move kg = L/1000 x density, constant rate.
// Destinations are filled to 99.9% of rated max pressure (gases) or volume x density (liquids)
// so a destination tank can never burst from our pumping.

internal static class TankStation
{
    private const double R = 0.008314000442624092; // ideal gas constant, kPa m^3 / (mol K)

    private const double DefaultTemp = 293.0;

    private const double FillFraction = 0.999; // of StatGasPressureMax; burst margin

    private const double GasFloorMols = 0.01; // sources below this read as empty

    private const double KgFloor = 0.01;

    private const double Epsilon = 0.0001;

    private const float MaxCatchupSeconds = 86400f;

    private const string InstalledCT = "TIsTankStationInstalled";

    private const string PoweredCond = "IsPowered";

    private const string SlowCond = "IsSlowMode";

    private const string ReverseCond = "IsReverse";

    private const string DumpCond = "IsTankStationDump"; // set/cleared by the right-click Dump Mode interactions

    private const string PumpingCond = "IsTankStationPumping"; // while set, the powerinfo override is active

    // One row per transferable resource. Adding a resource (e.g. Ship's Water StatLiqH2O, or
    // anything the devs add later) means appending one row here instead of another copy of the
    // four-parallel-variables pattern.
    private class ResSpec
    {
        public string Name; // short label for logs: "O2"

        public string VesselCond; // cond marking a source tank of this resource: "IsVesselO2"

        public string Species; // gas species for GasContainer.AddGasMols; null means liquid (plain cond move)

        public string Stat; // contents cond: "StatGasMolO2" (mols) or "StatSolidHe3" (kg)

        public double Density; // liquids only: kg per m^3 (capacity = StatVolume x Density)

        public string DstCT; // vanilla condtrigger naming this resource's installed destination tanks

        public bool DumpForced; // dump mode forces need to ~infinite (all but O2: NPCs never store O2 at RCS intakes)

        public int MinTier; // stations below this tier won't accept the resource (backstop; the container fit CTs are the real gate)
    }

    private static readonly ResSpec[] Res = new ResSpec[4]
    {
        new ResSpec { Name = "O2", VesselCond = "IsVesselO2", Species = "O2", Stat = "StatGasMolO2", DstCT = "TIsRTAO2Installed", DumpForced = false, MinTier = 1 },
        new ResSpec { Name = "N2", VesselCond = "IsVesselN2", Species = "N2", Stat = "StatGasMolN2", DstCT = "TIsRTAN2Installed", DumpForced = true, MinTier = 1 },
        new ResSpec { Name = "He3", VesselCond = "IsVesselHe3", Species = null, Stat = "StatSolidHe3", Density = 129.11, DstCT = "TIsCanisterLHe02Installed", DumpForced = true, MinTier = 2 },
        new ResSpec { Name = "D2O", VesselCond = "IsVesselH2", Species = null, Stat = "StatLiqD2O", Density = 1107.0, DstCT = "TIsCanisterLH02Installed", DumpForced = true, MinTier = 2 },
    };

    private static double _fLast = -1.0;

    private static readonly Dictionary<string, string> _lastStatus = new Dictionary<string, string>();

    private static readonly CondTrigger[] _ctDsts = new CondTrigger[Res.Length];

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
        int tier = TierOf(station);
        if (tier == 0)
        {
            return;
        }
        if (station.GetCondAmount(PoweredCond) <= 0.0)
        {
            LogStatus(station, "IDLE: no power / switched OFF (wire it to power and set the panel to AUTO or ON)", 0, 0.0);
            return;
        }
        bool reverse = station.GetCondAmount(ReverseCond) > 0.0;
        bool dump = station.HasCond(DumpCond);

        // Sources: tanks the player loaded into the station's hopper, filtered to what this tier accepts.
        List<CondOwner> srcs = new List<CondOwner>();
        List<CondOwner> cOsSafe = station.GetCOsSafe(bAllowLocked: false);
        if (cOsSafe != null)
        {
            foreach (CondOwner item in cOsSafe)
            {
                if (SpecOfSource(item, tier) >= 0)
                {
                    srcs.Add(item);
                }
            }
        }

        // Destinations: one list per resource, aligned with Res[].
        EnsureCTs();
        List<CondOwner>[] dsts = new List<CondOwner>[Res.Length];
        for (int i = 0; i < Res.Length; i++)
        {
            dsts[i] = new List<CondOwner>();
        }
        Ship ship = station.ship;
        if (ship != null)
        {
            CollectDsts(ship, station, dsts);
        }

        // If in reverse mode, swap srcs and dsts
        if (reverse)
        {
            // For dsts -> srcs, just flatten the list
            List<CondOwner> reversedsrcs = new List<CondOwner>();
            foreach (List<CondOwner> list in dsts)
            {
                if (list != null)
                {
                    reversedsrcs.AddRange(list);
                }
            }
            // For srcs -> dsts, we need to place each source into the dst of the right type
            List<CondOwner>[] reverseddsts = new List<CondOwner>[Res.Length];
            for (int residx = 0; residx < Res.Length; residx++)
            {
                reverseddsts[residx] = new List<CondOwner>();
            }
            for (int oldsrcidx = 0; oldsrcidx < srcs.Count; oldsrcidx++)
            {
                CondOwner oldsrc = srcs[oldsrcidx];
                bool placed = false;
                for (int residx = 0; residx < Res.Length; residx++)
                {
                    if (oldsrc.HasCond(Res[residx].VesselCond))
                    {
                        reverseddsts[residx].Add(oldsrc);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    Plugin.Log.LogWarning($"Tank station {station.strID} reverse mode: source {oldsrc.strID} has no matching resource type; skipping it.");
                }
            }
        }

        // How much of each resource the ship can currently take (mols for gases, kg for liquids).
        double[] need = new double[Res.Length];
        for (int j = 0; j < Res.Length; j++)
        {
            need[j] = Needed(dsts[j], Res[j]);
            if (dump && Res[j].DumpForced)
            {
                need[j] = 1000000000.0; // dump mode: pretend the ship can take forever; excess is vented
            }
        }

        int numActive = 0;
        foreach (CondOwner src in srcs)
        {
            if (SourceActive(src, tier, need))
            {
                numActive++;
            }
        }
        if (numActive == 0)
        {
            SetPumping(station, false);
            LogStatus(station, (srcs.Count == 0) ? "IDLE: no tanks loaded (drop salvaged tanks into the station's hopper)" : "IDLE: nothing to do (ship's tanks are full, or loaded tanks are empty)", srcs.Count, 0.0);
            return;
        }

        // The station's flow is split evenly between active sources
        // Volume of source is irrelevant. Small gas cans and large liquid tanks are drawn from equally
        // Use the linear step (k * dt) instead of the exact exponential decay (1 - e^(-k * dt))
        // For very large time steps, you can end up moving 100% of the source which is physically impossible
        // But in gameplay terms it doesn't matter and it saves us doing another loop to stake per-tank station flow rates
        double slowMultiplier = (station.GetCondAmount(SlowCond) > 0.0) ? 0.1 : 1.0;
        double litersEach = (double)FlowForTier(tier) * slowMultiplier * (double)dtGame / numActive;
        double[] moved = new double[Res.Length];
        foreach (CondOwner src in srcs)
        {
            if (src == null || src.bDestroyed)
            {
                continue;
            }
            int k = SpecOfSource(src, tier);
            if (k < 0 || need[k] <= Epsilon)
            {
                continue;
            }
            double added = (Res[k].Species != null) ? MoveGas(src, Res[k], litersEach, need[k], dsts[k]) : MoveLiquid(src, Res[k], litersEach, need[k], dsts[k]);
            moved[k] += added;
            need[k] -= added;
        }

        double total = 0.0;
        for (int m = 0; m < Res.Length; m++)
        {
            total += moved[m];
        }
        if (total > Epsilon)
        {
            SetPumping(station, true);
            StringBuilder stringBuilder = new StringBuilder("PUMPING");
            for (int n = 0; n < Res.Length; n++)
            {
                if (moved[n] > Epsilon)
                {
                    stringBuilder.Append(string.Format(" {0} +{1:0.###} {2}", Res[n].Name, moved[n], (Res[n].Species != null) ? "mol" : "kg"));
                }
            }
            if (dump)
            {
                stringBuilder.Append(" (DUMP on)");
            }
            LogStatus(station, stringBuilder.ToString(), srcs.Count, total);
        }
        else
        {
            SetPumping(station, false);
            LogStatus(station, "IDLE: loaded tanks are nearly empty", srcs.Count, 0.0);
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

    // Which resource a source tank holds, or -1 if this tier doesn't accept it. Gas sources must
    // also be RTA cans (matches the container fit CTs); liquids are the big non-RTA canisters.
    private static int SpecOfSource(CondOwner co, int tier)
    {
        if (co == null || co.bDestroyed)
        {
            return -1;
        }
        for (int i = 0; i < Res.Length; i++)
        {
            if (tier < Res[i].MinTier || !co.HasCond(Res[i].VesselCond))
            {
                continue;
            }
            if (Res[i].Species != null && !co.HasCond("IsRTA"))
            {
                continue;
            }
            return i;
        }
        return -1;
    }

    private static bool SourceActive(CondOwner co, int tier, double[] need)
    {
        int num = SpecOfSource(co, tier);
        if (num < 0 || need[num] <= Epsilon)
        {
            return false;
        }
        double num2 = (Res[num].Species != null) ? GasFloorMols : KgFloor;
        return co.GetCondAmount(Res[num].Stat) > num2 + Epsilon;
    }

    // Remove up to `liters` swept-volume worth of gas from the source (mols, partial-pressure
    // proportional so flow decays as the source empties), then fill destinations in order.
    // Anything no destination can hold is vented. Returns the amount actually added (not vented).
    private static double MoveGas(CondOwner src, ResSpec res, double liters, double needLeft, List<CondOwner> dsts)
    {
        GasContainer gasContainer = src.GasContainer;
        if (gasContainer == null)
        {
            return 0.0;
        }
        double num = src.GetCondAmount(res.Stat) - GasFloorMols;
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
        gasContainer.AddGasMols(res.Species, 0.0 - num3);
        double num4 = num3;
        foreach (CondOwner dst in dsts)
        {
            num4 -= AddGas(dst, res.Species, res.Stat, num4);
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

    // Room left in a destination, in mols: the lesser of the pressure headroom (mixture-safe -
    // accounts for other gases already in the tank) and the per-species capacity (guards a stale
    // or missing StatGasPressure cond). Never lets a tank past FillFraction of its rated max.
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

    private static double MoveLiquid(CondOwner src, ResSpec res, double liters, double needLeft, List<CondOwner> dsts)
    {
        double num = src.GetCondAmount(res.Stat) - KgFloor;
        if (num <= Epsilon)
        {
            return 0.0;
        }
        double val = liters / 1000.0 * res.Density;
        double num2 = Math.Min(Math.Min(val, num), Math.Max(needLeft, 0.0));
        if (num2 <= Epsilon)
        {
            return 0.0;
        }
        src.AddCondAmount(res.Stat, 0.0 - num2);
        double num3 = num2;
        foreach (CondOwner dst in dsts)
        {
            num3 -= AddLiquid(dst, res, num3);
            if (num3 <= Epsilon)
            {
                break;
            }
        }
        return num2 - num3;
    }

    private static double AddLiquid(CondOwner dst, ResSpec res, double kg)
    {
        if (dst == null || dst.bDestroyed)
        {
            return 0.0;
        }
        double num = LiquidCapKg(dst, res.Density) - dst.GetCondAmount(res.Stat);
        if (num <= Epsilon)
        {
            return 0.0;
        }
        double num2 = Math.Min(num, kg);
        dst.AddCondAmount(res.Stat, num2);
        return num2;
    }

    private static double LiquidCapKg(CondOwner co, double density)
    {
        double condAmount = co.GetCondAmount("StatVolume");
        if (condAmount <= 0.0)
        {
            return 0.0;
        }
        return FillFraction * condAmount * density;
    }

    private static double Needed(List<CondOwner> dsts, ResSpec res)
    {
        double num = 0.0;
        foreach (CondOwner dst in dsts)
        {
            if (dst != null && !dst.bDestroyed)
            {
                num += ((res.Species != null) ? GasRoomMols(dst, res.Stat) : Math.Max(0.0, LiquidCapKg(dst, res.Density) - dst.GetCondAmount(res.Stat)));
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
        for (int i = 0; i < Res.Length; i++)
        {
            _ctDsts[i] = DataHandler.GetCondTrigger(Res[i].DstCT);
        }
    }

    private static void CollectDsts(Ship ship, CondOwner station, List<CondOwner>[] dsts)
    {
        // Loose cans under RCS intake regulators are N2 destinations too, and get filled FIRST:
        // if your atmo cans run out, the room keeps its O2/N2 mix for a while
        // but if your RCS N2 runs out, you're stranded.
        int indexOfN2 = -1; // TODO: Consider caching this
        for (int i = 0; i < Res.Length; i++)
        {
            if (Res[i].Name == "N2")
            {
                indexOfN2 = i;
                break;
            }
        }
        foreach (CondOwner item in ship.GetRCSCans())
        {
            if (item == null || item.bDestroyed || item.ship != ship || IsInside(item, station) || item.GasContainer == null)
            {
                continue;
            }
            dsts[indexOfN2].Add(item);
        }
        // Installed tanks, anywhere on the ship, are valid destinations. Note this is more tolerant
        // than the refuel kiosk's N2 check (air pumps and what's under them): any installed N2 can
        // counts, even one serving as RCS remass.
        for (int i = 0; i < Res.Length; i++)
        {
            Collect(ship, station, _ctDsts[i], dsts[i], Res[i].Species != null);
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
            // Cans that are not destroyed (can hold pressure), on this ship, not inside the tank
            // station's internal inventory, has a gas container if we're inserting gas, and not
            // already in the list
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

    // Only have the full power draw when there is something to pump
    // Otherwise use a base amount of power for doing checks
    private static void SetPumping(CondOwner station, bool pumping)
    {
        bool flag = station.HasCond(PumpingCond);
        if (pumping && !flag)
        {
            station.SetCondAmount(PumpingCond, 1.0);
        }
        else if (!pumping && flag)
        {
            station.ZeroCondAmount(PumpingCond);
        }
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
