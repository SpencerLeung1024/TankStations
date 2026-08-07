using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TankStations;

[BepInPlugin("com.avixg.tankstations", "Tank Stations", "0.1.0")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.avixg.tankstations";

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

    public static ConfigEntry<float> T4FullRangeKm;

    public static ConfigEntry<float> T4MaxRangeKm;

    public static ConfigEntry<bool> VerboseLogging;

    private float _timer;

    private void Awake()
    {
        Log = Logger;
        FlowT1 = Config.Bind("Balance", "Tier 1 Flow L/s", 5f, new ConfigDescription("Mk I transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(0.1f, 2000f)));
        FlowT2 = Config.Bind("Balance", "Tier 2 Flow L/s", 100f, new ConfigDescription("Mk II transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(0.1f, 4000f)));
        FlowT3 = Config.Bind("Balance", "Tier 3 Flow L/s", 200f, new ConfigDescription("Mk III transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(0.1f, 8000f)));
        FlowT4 = Config.Bind("Balance", "Tier 4 Flow L/s", 300f, new ConfigDescription("Mk IV transfer rate in swept source liters per game-second.", new AcceptableValueRange<float>(0.1f, 12000f)));
        TickIntervalSeconds = Config.Bind("Plumbing", "Tick Seconds", 2f, new ConfigDescription("How often (in real seconds) stations pump.", new AcceptableValueRange<float>(0.5f, 30f)));
        KioskT2Chance = Config.Bind("Kiosk", "Tier 2 Stock Chance", 0.25f, new ConfigDescription("Chance (0-1) that a supplies/furnishings kiosk restock includes a Tank Station Mk II. Mk I is always stocked.", new AcceptableValueRange<float>(0f, 1f)));
        DerelictT1Chance = Config.Bind("Salvage", "Derelict Tier 1 Chance", 0.3f, new ConfigDescription("Chance (0-1) that a generated derelict ship has a Tank Station Mk I installed.", new AcceptableValueRange<float>(0f, 1f)));
        DerelictT2Chance = Config.Bind("Salvage", "Derelict Tier 2 Chance", 0.03f, new ConfigDescription("Chance (0-1) that a generated derelict ship has a Tank Station Mk II installed.", new AcceptableValueRange<float>(0f, 1f)));
        T4FullRangeKm = Config.Bind("Tier 4", "Full Flow Range Km", 50f, new ConfigDescription("Mk IV remote drain runs at full flow out to this range.", new AcceptableValueRange<float>(1f, 10000f)));
        T4MaxRangeKm = Config.Bind("Tier 4", "Max Range Km", 500f, new ConfigDescription("Mk IV remote drain stops entirely beyond this range.", new AcceptableValueRange<float>(10f, 100000f)));
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
