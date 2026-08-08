using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TankStations;

// Allows finetuning of tank station gameplay through the BepInEx configuration manager in-game

[BepInPlugin("com.ostranauts.tankstations", "Tank Stations", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.ostranauts.tankstations";

    public const string PluginName = "Tank Stations";

    public const string PluginVersion = "0.1.0";

    public static ManualLogSource Log;

    public static ConfigEntry<float> FlowT1;

    public static ConfigEntry<float> FlowT2;

    public static ConfigEntry<float> FlowT3;

    public static ConfigEntry<float> FlowT4;

    public static ConfigEntry<float> TickIntervalSeconds;

    public static ConfigEntry<float> KioskT2Chance;

    public static ConfigEntry<float> DerelictT1Chance;

    public static ConfigEntry<float> DerelictT2Chance;

    public static ConfigEntry<float> HaulerT3Chance;

    public static ConfigEntry<float> PirateT4Chance;

    public static ConfigEntry<float> T4FullRangeKm;

    public static ConfigEntry<float> T4MaxRangeKm;

    public static ConfigEntry<bool> KeepDrainVictims;

    public static ConfigEntry<bool> VerboseLogging;

    private float _timer;

    private void Awake()
    {
        Log = Logger;
        FlowT1 = Config.Bind("Balance", "Tier 1 Flow L/s", 5f, new ConfigDescription("Mk I transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(1e-3f, 1e6f)));
        FlowT2 = Config.Bind("Balance", "Tier 2 Flow L/s", 100f, new ConfigDescription("Mk II transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(1e-3f, 1e6f)));
        FlowT3 = Config.Bind("Balance", "Tier 3 Flow L/s", 200f, new ConfigDescription("Mk III transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(1e-3f, 1e6f)));
        FlowT4 = Config.Bind("Balance", "Tier 4 Flow L/s", 300f, new ConfigDescription("Mk IV transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(1e-3f, 1e6f)));
        TickIntervalSeconds = Config.Bind("Plumbing", "Tick Seconds", 2f, new ConfigDescription("How often (in real seconds) stations pump.", new AcceptableValueRange<float>(0.5f, 30f)));
        KioskT2Chance = Config.Bind("Kiosk", "Tier 2 Stock Chance", 0.3f, new ConfigDescription("Chance (0-1) that a supplies kiosk restock includes a Tank Station Mk II. Mk I is always stocked.", new AcceptableValueRange<float>(0f, 1f)));
        DerelictT1Chance = Config.Bind("Salvage", "Derelict Tier 1 Chance", 0.3f, new ConfigDescription("Chance (0-1) that a generated derelict ship has a Tank Station Mk I installed.", new AcceptableValueRange<float>(0f, 1f)));
        DerelictT2Chance = Config.Bind("Salvage", "Derelict Tier 2 Chance", 0.07f, new ConfigDescription("Chance (0-1) that a generated derelict ship has a Tank Station Mk II installed.", new AcceptableValueRange<float>(0f, 1f)));
        HaulerT3Chance = Config.Bind("Salvage", "Hauler Tier 3 Chance", 1f, new ConfigDescription("Chance (0-1) that a generated hauler ship has a Tank Station Mk III installed.", new AcceptableValueRange<float>(0f, 1f)));
        PirateT4Chance = Config.Bind("Salvage", "Pirate Tier 4 Chance", 0.05f, new ConfigDescription("Chance (0-1) that a generated Ceres pirate ship has a Tank Station Mk IV installed.", new AcceptableValueRange<float>(0f, 1f)));
        T4FullRangeKm = Config.Bind("Balance", "Full Flow Range Km", 50f, new ConfigDescription("Mk IV remote drain runs at full flow out to this range.", new AcceptableValueRange<float>(1f, 1e6f)));
        T4MaxRangeKm = Config.Bind("Balance", "Max Range Km", 500f, new ConfigDescription("Mk IV remote drain stops entirely beyond this range.", new AcceptableValueRange<float>(1f, 1e6f)));
        KeepDrainVictims = Config.Bind("Balance", "Keep Drain Victims", true, "Ships drained by a Mk IV are kept alive (blocked from AI fuel-despawn cleanup) so you can board them. Disable to let them despawn normally.");
        VerboseLogging = Config.Bind("Debug", "Verbose Logging", true, "Log per-station status changes to the BepInEx log. Safe to turn off once everything is confirmed working.");
        Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, PluginGuid);
        KioskStock.Install();
        Log.LogInfo($"{PluginName} {PluginVersion} loaded. T1 {FlowT1.Value} L/s, T2 {FlowT2.Value} L/s, tick every {TickIntervalSeconds.Value}s.");
    }

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f)
        {
            return;
        }
        _timer = TickIntervalSeconds.Value;
        try
        {
            TankStation.Run();
        }
        catch (Exception ex)
        {
            Log.LogError("Tank station tick failed: " + ex);
        }
    }
}
