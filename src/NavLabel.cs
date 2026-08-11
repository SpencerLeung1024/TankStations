using System;
using System.Globalization;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace TankStations;

// Nav-map siphon readout (DESIGN §11): when the player's ship carries an installed, powered Mk
// IV and the nav crosshair is on a ship, show a small label beside the target with its remaining
// drainable contents and the current drain rate. Pattern copied from OrbitMarkers'
// ClosestApproachRenderer / DrawClosestApproachPatch: postfix on GUIOrbitDraw.DrawSystem, label
// instantiated from the "GUIShip/lblOrbit" prefab under the orbit panel, positioned via
// SolarToCanvas + vCanvasOffset, destroyed on the GUI's OnDestroy.
[HarmonyPatch(typeof(GUIOrbitDraw), "DrawSystem")]
internal static class Patch_DrawSystem_NavLabel
{
    private static void Postfix(GUIOrbitDraw __instance, RectTransform ___rectDrawPanel, GameObject ___goOrbitPanel)
    {
        NavLabel.Draw(__instance, ___rectDrawPanel, ___goOrbitPanel);
    }
}

[HarmonyPatch(typeof(GUIOrbitDraw), "OnDestroy")]
internal static class Patch_OnDestroy_NavLabel
{
    private static void Prefix()
    {
        NavLabel.Destroy();
    }
}

internal static class NavLabel
{
    private const string T4InstalledCT = "TIsTankStationT4Installed";

    private const string PoweredCond = "IsPowered";

    private static readonly Color LabelColor = new Color(1f, 0.62f, 0.25f, 0.95f); // siphon orange

    private static RectTransform _labelTransform;

    private static TMP_Text _label;

    private static CanvasGroup _canvasGroup;

    private static Transform _labelPrefab;

    private static bool _loggedFailure;

    internal static void Draw(GUIOrbitDraw orbitDraw, RectTransform drawPanel, GameObject orbitPanel)
    {
        try
        {
            Ship ship = orbitDraw?.COSelf?.ship;
            Ship ship2 = GUIOrbitDraw.CrossHairTarget?.Ship;
            if (!Plugin.NavLabelEnabled.Value || ship == null || ship.objSS == null || ship2 == null || ship2 == ship || ship2.bDestroyed || ship2.objSS == null || drawPanel == null || orbitPanel == null || !HasPoweredT4(ship))
            {
                SetActive(false);
                return;
            }
            double num = ship.objSS.GetRangeTo(ship2.objSS) * (double)CrewSim.KM_PER_AU;
            float value = Plugin.T4FullRangeKm.Value;
            float value2 = Plugin.T4MaxRangeKm.Value;
            string text = BuildText(ship2, num, value, value2);
            if (text == null)
            {
                SetActive(false);
                return;
            }
            EnsureCreated(orbitPanel);
            if (_label == null)
            {
                return;
            }
            GUIOrbitDraw.CrossHairTarget.GetSXY(out var sx, out var sy);
            orbitDraw.SolarToCanvas(sx, sy, out var cx, out var cy);
            if (cx < 0.0 || cx > (double)drawPanel.rect.width || cy < 0.0 || cy > (double)drawPanel.rect.height)
            {
                SetActive(false);
                return;
            }
            _label.text = text;
            // Try to place to the right of the crosshair. Personal preference
            cx += 120.0;
            _label.alignment = TextAlignmentOptions.Left;
            // Clamp to within the panel
            float cxclamped = Mathf.Clamp((float)cx, 80f, drawPanel.rect.width - 80f);
            float cyclamped = Mathf.Clamp((float)cy, 60f, drawPanel.rect.height - 60f);
            _labelTransform.anchoredPosition = new Vector2(cxclamped - orbitDraw.vCanvasOffset.x, cyclamped - orbitDraw.vCanvasOffset.y);
            SetActive(true);
        }
        catch (Exception ex)
        {
            SetActive(false);
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.Log.LogError("[TankStations] Nav label draw failed. Further errors will be suppressed.\n" + ex);
            }
        }
    }

    internal static void Destroy()
    {
        if (_labelTransform != null)
        {
            UnityEngine.Object.Destroy(_labelTransform.gameObject);
        }
        _labelTransform = null;
        _label = null;
        _canvasGroup = null;
    }

    private static bool HasPoweredT4(Ship ship)
    {
        CondTrigger condTrigger = DataHandler.GetCondTrigger(T4InstalledCT);
        if (condTrigger == null)
        {
            return false;
        }
        System.Collections.Generic.List<CondOwner> iCOs = ship.GetICOs1(condTrigger, bSubObjects: false, bAllowDocked: false, bAllowLocked: true);
        if (iCOs == null)
        {
            return false;
        }
        foreach (CondOwner item in iCOs)
        {
            if (item != null && !item.bDestroyed && item.GetCondAmount(PoweredCond) > 0.0)
            {
                return true;
            }
        }
        return false;
    }

    // Label text, or null when there's nothing worth showing. Rates come from TankStation's
    // last-tick record; remaining from ShallowFuel's template/ledger model.
    private static string BuildText(Ship target, double distKm, float fullKm, float maxKm)
    {
        if (target.IsStation())
        {
            return "SIPHON OFF - STATION";
        }
        if (TankStation.RemoteSiphonBlocked(target))
        {
            return "SIPHON OFF - UNVISITED DERELICT\n(DISABLED IN CONFIG)";
        }
        if (target.LoadState >= Ship.Loaded.Edit)
        {
            return "SIPHON OFF - DOCKED SHIP";
        }
        ShallowFuel.TemplateInv inventory = ShallowFuel.GetInventory(target);
        if (inventory == null)
        {
            return null;
        }
        StringBuilder stringBuilder = new StringBuilder();
        if (distKm > (double)maxKm)
        {
            stringBuilder.Append("OUT OF RANGE - ").Append(Fmt(distKm)).Append(" km");
        }
        else
        {
            // This is a static, calculated by TankStation.Run, and sums effective L/s for all T4 tank stations
            stringBuilder.Append("SIPHON ").Append(Fmt(TankStation.EffectiveLPerSec)).Append(" L/s - ").Append(Fmt(distKm)).Append(" km");
        }

        bool flag = TankStation.RemoteTargetRegID == target.strRegID;
        bool flag2 = false;
        for (int i = 0; i < 4; i++)
        {
            // 2026-08-09: Disable O2 remote drain. This change here is to drop line count from 5 to 4.
            if (i == 0)
            {
                continue;
            }
            double num = ShallowFuel.AvailableKg(target, inventory, i);
            double num2 = (flag ? TankStation.RemoteKgPerSec[i] : 0.0);
            if (num > 0.1 || num2 > 0.001)
            {
                flag2 = true;
                stringBuilder.Append("\n").Append(ShallowFuel.Names[i]).Append(' ').Append(Fmt(num)).Append(" kg");
                if (num2 > 0.001)
                {
                    stringBuilder.Append("  -").Append(Fmt(num2)).Append(" kg/s");
                }
            }
        }
        if (!flag2)
        {
            stringBuilder.Append("\nTARGET DRY");
        }

        // Investigate NPC behavior
        // 2026-08-11: Clean up for release
        /*
        stringBuilder.Append("\nfShallowRCSRemass ").Append(Fmt(target.fShallowRCSRemass)).Append(" kg");
        stringBuilder.Append("\nfShallowRCSRemassMax ").Append(Fmt(target.fShallowRCSRemassMax)).Append(" kg");
        stringBuilder.Append("\nfShallowFusionRemain ").Append(Fmt(target.fShallowFusionRemain)).Append(" s");
        stringBuilder.Append("\nbFusionReactorRunning ").Append(target.bFusionReactorRunning ? "true" : "false");

        if (target.objSS == null)
        {
            stringBuilder.Append("\nobjSS null");
        }
        else
        {
            if (!target.objSS.HasNavData())
            {
                stringBuilder.Append("\nNavData null");
            }
            else
            {
                Ostranauts.ShipGUIs.Utilities.NavDataPoint start = target.objSS.NavData.Origin;
                Ostranauts.ShipGUIs.Utilities.NavDataPoint end = target.objSS.NavData.Destination;
                stringBuilder.Append("\nArrivalTime ").Append(Fmt(start.ArrivalTime - StarSystem.fEpoch)).Append(" -> ").Append(Fmt(end.ArrivalTime - StarSystem.fEpoch));
                stringBuilder.Append("\nFuelLevel ").Append(Fmt(start.FuelLevel)).Append(" -> ").Append(Fmt(end.FuelLevel));
                stringBuilder.Append("\nTorchFuelLevel ").Append(Fmt(start.TorchFuelLevel)).Append(" -> ").Append(Fmt(end.TorchFuelLevel));

                // There is also public List<Vector2> GetPoints(GUIOrbitDraw gorb) but I have no idea how to render the line
            }
        }
        */

        return stringBuilder.ToString();
    }

    private static string Fmt(double kg)
    {
        if (kg >= 1000.0)
        {
            return kg.ToString("#,##0", CultureInfo.InvariantCulture);
        }
        if (kg >= 10.0)
        {
            return kg.ToString("0.#", CultureInfo.InvariantCulture);
        }
        return kg.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static void EnsureCreated(GameObject orbitPanel)
    {
        if (_labelTransform != null)
        {
            return;
        }
        if (_labelPrefab == null)
        {
            _labelPrefab = Resources.Load<Transform>("GUIShip/lblOrbit");
        }
        if (_labelPrefab == null)
        {
            return;
        }
        _labelTransform = UnityEngine.Object.Instantiate(_labelPrefab, orbitPanel.transform) as RectTransform;
        if (_labelTransform == null)
        {
            return;
        }
        _labelTransform.gameObject.name = "TankStationsSiphonOverlay";
        _label = _labelTransform.GetComponent<TMP_Text>();
        _canvasGroup = _labelTransform.GetComponent<CanvasGroup>();
        if (_label != null)
        {
            _label.color = LabelColor;
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.overflowMode = TextOverflowModes.Overflow;
            _label.rectTransform.sizeDelta = new Vector2(160f, _label.rectTransform.sizeDelta.y);
        }
        SetActive(false);
    }

    private static void SetActive(bool active)
    {
        if (_canvasGroup != null)
        {
            _canvasGroup.alpha = (active ? 1f : 0f);
        }
        else if (_labelTransform != null)
        {
            _labelTransform.gameObject.SetActive(active);
        }
    }
}
