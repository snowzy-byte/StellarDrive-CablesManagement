using HarmonyLib;
using Players.Interface.Services;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Visuals;
using Tools.Build;
using Tools.Build.Model;
using Tools.Build.SnapDetectors.Model;
using UnityEngine;
using System;
using System.Runtime.CompilerServices;
using Ships.Cables;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Settings;
using Object = UnityEngine.Object;

namespace CablesManagementMod;

public static class CablesManagementSocketPositions
{
    private sealed class Anchor
    {
        public Vector3 Local;
    }
    private static ConditionalWeakTable<SocketObject, Anchor> Anchors = new ConditionalWeakTable<SocketObject, Anchor>();

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ConnectionsTracker), "GetSocketPosition"),
            prefix: new HarmonyMethod(typeof(CablesManagementSocketPositions), nameof(BeforePosition)));
    }

    internal static void Track(SocketObject socket, Vector3 local)
    {
        Anchors.Remove(socket);
        Anchors.Add(socket, new Anchor { Local = local });
    }

    private static bool BeforePosition(IPartPlacement placement, SocketObject socketObject, ref Vector3 __result)
    {
        if (socketObject == null || !Anchors.TryGetValue(socketObject, out Anchor anchor)) return true;
        __result = placement.LocalPosition + placement.Rotation * anchor.Local;
        return false;
    }

    public static void Remove() => Anchors = new ConditionalWeakTable<SocketObject, Anchor>();
}

public static class CablesManagementPlacement
{
    public static void Install(HarmonyLib.Harmony harmony)
    {
        CablesManagementRotation.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(PartPreviewDetection), "TryFindSnapInRayHit"),
            prefix: new HarmonyMethod(typeof(CablesManagementPlacement), nameof(BeforeFindSnap)));
        harmony.Patch(AccessTools.Method(typeof(ShipPartInstancer), nameof(ShipPartInstancer.InstantiatePartPreview)),
            postfix: new HarmonyMethod(typeof(CablesManagementPlacement), nameof(AfterPreview)));
    }

    private static bool BeforeFindSnap(SnapDetectionState state, RaycastHit hit,
        ref Pivot? pivot, ref IPartPlacement partPlacement, ref bool __result)
    {
        if (state.PartSettings == null || !CablesManagementMod.IsClip(state.PartSettings.id)) return true;
        pivot = null;
        partPlacement = null;
        __result = false;
        IShipPartPlacements grid = hit.collider.GetComponentInParent<IShipPartPlacements>();
        if (grid == null) return false;
        var localHit = grid.ConvertHitToLocalHit(hit);
        PartOrientation forward = PartOrientationHelper.SnapAnyOrientation(-localHit.Normal);

        Vector3 forwardVector = forward.GetVector3();
        if (Mathf.Abs(forwardVector.x) > .01f && Mathf.Abs(forwardVector.y) > .01f && Mathf.Abs(forwardVector.z) > .01f)
            return false;
        Vector3 normal = -forwardVector;
        Quaternion desired = grid.ToLocalRotation(state.PartRotation);
        Vector3 up = Vector3.ProjectOnPlane(desired * Vector3.up, normal);
        if (up.sqrMagnitude < .01f) up = Vector3.ProjectOnPlane(desired * Vector3.forward, normal);
        PartOrientation? snappedUp = CablesManagementSurfaceOrientation.SnapUp(up, forward);
        if (!snappedUp.HasValue) return false;
        Quaternion rotation = Quaternion.LookRotation(forward.GetVector3(), snappedUp.Value.GetVector3());
        Vector3 right = rotation * Vector3.right;
        Vector3 tangent = rotation * Vector3.up;

        Vector3 coord = right * (Mathf.Round(Vector3.Dot(localHit.Pos, right) * 32f) / 32f)
            + tangent * (Mathf.Round(Vector3.Dot(localHit.Pos, tangent) * 32f) / 32f)
            + normal * (Vector3.Dot(localHit.Pos, normal) + .002f);
        var placement = new FloatingPartPlacement(coord, forward, snappedUp.Value);
        if (!grid.IsFloatingPlacementFree(placement, state.PartSettings.Bounds, state.IgnoredPart)) return false;
        partPlacement = placement;
        __result = true;
        return false;
    }

    private static void AfterPreview(Ships.Interface.Settings.PartSettings partSettings, GameObject __result)
    {
        if (partSettings == null || !CablesManagementMod.IsClip(partSettings.id) || __result == null) return;
        foreach (Collider collider in __result.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (Rigidbody body in __result.GetComponentsInChildren<Rigidbody>(true))
        {
            body.detectCollisions = false;
            body.isKinematic = true;
        }
    }
}

public static class CablesManagementSurfaceOrientation
{
    private static readonly PartOrientation[] Directions = (PartOrientation[])Enum.GetValues(typeof(PartOrientation));

    public static PartOrientation? SnapUp(Vector3 direction, PartOrientation forward)
    {
        if (direction.sqrMagnitude < .000001f) return null;
        direction = direction.normalized;
        Vector3 normal = forward.GetVector3();
        PartOrientation? best = null;
        float bestScore = -2f;
        foreach (PartOrientation candidate in Directions)
        {
            Vector3 tangent = candidate.GetVector3();
            if (Mathf.Abs(Vector3.Dot(tangent, normal)) > .001f) continue;
            float score = Vector3.Dot(direction, tangent);
            if (score > bestScore) { best = candidate; bestScore = score; }
        }
        return best;
    }
}

public static class CablesManagementRotation
{
    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PartPreview), nameof(PartPreview.Rotate)),
            prefix: new HarmonyMethod(typeof(CablesManagementRotation), nameof(BeforeRotate)));
    }

    private static bool BeforeRotate(PartPreview __instance, float direction,
        PartSettings ____actualPreviewSettings, SnappedPreviewPlacement ____snappedPlacement,
        GameObject ____objectPreview, ref Vector3 ____currentRotationAxis)
    {
        if (____actualPreviewSettings == null || !CablesManagementMod.IsClip(____actualPreviewSettings.id)
            || ____snappedPlacement == null || !____snappedPlacement.FloatingPartPlacement.HasValue
            || ____objectPreview == null || !____objectPreview.activeSelf) return true;

        Transform preview = ____objectPreview.transform;
        Vector3 normal = -(preview.rotation * Vector3.forward);

        ____currentRotationAxis = normal;
        preview.rotation = Quaternion.AngleAxis(direction * 45f, normal) * preview.rotation;
        __instance.ResetSnap();
        return false;
    }
}
