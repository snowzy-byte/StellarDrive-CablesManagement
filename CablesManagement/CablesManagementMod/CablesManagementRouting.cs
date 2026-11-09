using System;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using Imports.CablesMesh;
using SDModKit.Game;
using Ships.Cables.VisualObjects;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Services;
using UnityEngine;
using Core.Utils;
using Players.LocalClient.Interaction;
using Ships.Cables;
using Ships.Interface.Services;
using Ships.Parts.Common.Model;
using WorldTracking.Interface.Values;
using Object = UnityEngine.Object;
using Players.Interface.Model;
using Players.Services;

namespace CablesManagementMod;

public sealed class CablesManagementCableTag : MonoBehaviour
{
    public bool A;
    public bool B;
    public uint Id;
    private uint _revision;
    private bool _curved;
    public bool Curved => CableRoutingStore.IsCurved(Id);

    private void LateUpdate()
    {
        if (_revision == CableRoutingStore.Revision) return;
        _revision = CableRoutingStore.Revision;
        bool curved = Curved;
        if (_curved == curved) return;
        _curved = curved;
        CablesManagementRouting.Refresh(GetComponent<CableVisual>());
    }
}

public static class CablesManagementRouting
{
    private static readonly MethodInfo UpdateCable = AccessTools.Method(typeof(CableVisual), "UpdateCable");

    public static void Update()
    {
        if (CablesManagementControls.MenuOpen || !CablesManagementControls.Pressed("ToggleCurve")) return;
        var players = GameRefs.Service<PlayersClientTracker>();
        if (players == null || !players.TryGetLocalPlayerState(out var state)
            || state.Type != PlayerStateType.WalkingHoldingCable && state.Type != PlayerStateType.CheatFlyingHoldingCable) return;
        CablePaintNetwork.SendRouting(state.EntityId, !CableRoutingStore.IsCurved(state.EntityId));
    }

    internal static void Refresh(CableVisual cable)
    {
        if (cable != null) UpdateCable.Invoke(cable, null);
    }

    public static void Install(HarmonyLib.Harmony harmony)
    {
        CablesManagementMeshVisibility.Install(harmony);
        CablesManagementPicking.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(CableVisual), nameof(CableVisual.Setup)),
            postfix: new HarmonyMethod(typeof(CablesManagementRouting), nameof(AfterSetup)));
        harmony.Patch(AccessTools.Method(typeof(CableVisual), nameof(CableVisual.GetSpline)),
            postfix: new HarmonyMethod(typeof(CablesManagementRouting), nameof(AfterGetSpline)));
        harmony.Patch(AccessTools.Method(typeof(CableVisual), "UpdateCableMesh"),
            prefix: new HarmonyMethod(typeof(CablesManagementRouting), nameof(BeforeUpdateMesh)));
    }

    private static bool IsGuide(CablePlugSocket socket)
    {
        if (socket.IsHeldByPlayer) return false;
        var ships = GameRefs.Service<ShipsClientTracker>();
        return ships != null && ships.TryGetStatefulPart(socket.EntityPartSocket.PartContext, out StatefulPart part)
            && CablesManagementMod.IsClip(part.Settings.id);
    }

    private static void AfterSetup(CableVisual __instance, CableData data)
    {
        bool a = IsGuide(data.State.PlugA), b = IsGuide(data.State.PlugB);
        var tag = __instance.gameObject.GetComponent<CablesManagementCableTag>();
        if (tag == null) tag = __instance.gameObject.AddComponent<CablesManagementCableTag>();
        tag.A = a;
        tag.B = b;
        tag.Id = data.Id;
        if (a) HidePlug(__instance, __instance.PlugA);
        if (b) HidePlug(__instance, __instance.PlugB);
        UpdateCable.Invoke(__instance, null);
    }

    private static void HidePlug(CableVisual cable, Transform plug)
    {
        foreach (MeshRenderer renderer in plug.GetComponentsInChildren<MeshRenderer>(true))
            if (cable.Cord == null || !renderer.transform.IsChildOf(cable.Cord.transform)) renderer.enabled = false;
    }

    private static void AfterGetSpline(CableVisual __instance, ref ValueTuple<SimpleBezier.Knot, SimpleBezier.Knot> __result)
    {
        var tag = __instance.GetComponent<CablesManagementCableTag>();
        if (tag == null) return;
        Transform root = __instance.transform;
        SimpleBezier.Knot a = __result.Item1, b = __result.Item2;
        if (tag.A) a.Position = root.InverseTransformPoint(__instance.PlugA.position);
        if (tag.B) b.Position = root.InverseTransformPoint(__instance.PlugB.position);
        if (tag.Curved) { __result = (a, b); return; }
        Vector3 chord = b.Position - a.Position;

        a.Tangent = b.Tangent = CablesManagementCurve.StraightHandle(chord);
        Quaternion frame = SafeFrame(chord, root.InverseTransformDirection(__instance.PlugA.up), a.Rotation);
        a.Rotation = b.Rotation = frame;
        __result = (a, b);
    }

    private static bool BeforeUpdateMesh(CableVisual __instance, Mesh[] ___cableBaseGeometry,
        MeshFilter[] ___cableMesh, float ___segmentLength, float ___uvScaling, ref Mesh[] ____cableDynamicMesh)
    {
        var tag = __instance.GetComponent<CablesManagementCableTag>();
        if (tag == null || tag.Curved) return true;
        var generator = GameRefs.Service<ExtrudableCableMeshGenerator>();
        if (generator == null) return true;
        Transform root = __instance.transform;
        var spline = __instance.GetSpline();
        Vector3 startDirection = root.InverseTransformDirection(-__instance.PlugA.forward);
        Vector3 endDirection = root.InverseTransformDirection(__instance.PlugB.forward);
        Vector3 start = spline.Item1.Position, end = spline.Item2.Position;

        float overlap = CablesManagementCurve.MouthOverlap(end - start);
        if (tag.A) start -= startDirection * overlap;
        if (tag.B) end += endDirection * overlap;
        Vector3 upA = root.InverseTransformDirection(__instance.PlugA.up);
        Vector3 upB = root.InverseTransformDirection(__instance.PlugB.up);
        List<CablesManagementPath.Sample> path = CablesManagementPath.Build(start, end, startDirection, endDirection, tag.A, tag.B);
        if (path.Count < 2) return true;
        Quaternion[] frames = CablesManagementFrames.Build(path, upA, upB, tag.A, tag.B);
        int rollSteps = Mathf.Max(1, Mathf.CeilToInt(Quaternion.Angle(frames[0], frames[frames.Length - 1]) / 15f));
        if (rollSteps > 1)
        {
            path = CablesManagementPath.SubdivideStraight(path, rollSteps);
            frames = CablesManagementFrames.Build(path, upA, upB, tag.A, tag.B);
        }
        var points = new List<ExtrudableCableMeshGenerator.PointOnSpline>(path.Count);
        float total = 0f;
        for (int i = 1; i < path.Count; i++) total += (path[i].Position - path[i - 1].Position).magnitude;
        float travelled = 0f;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) travelled += (path[i].Position - path[i - 1].Position).magnitude;
            float t = total > .000001f ? travelled / total : 0f;
            var sample = path[i];
            points.Add(new ExtrudableCableMeshGenerator.PointOnSpline
            {
                T = t, Knot = new SimpleBezier.Knot(sample.Position, sample.Direction, frames[i])
            });
        }
        if (____cableDynamicMesh == null)
        {
            ____cableDynamicMesh = new Mesh[___cableBaseGeometry.Length];
            for (int i = 0; i < ____cableDynamicMesh.Length; i++)
            {
                ____cableDynamicMesh[i] = new Mesh();
                ___cableMesh[i].sharedMesh = ____cableDynamicMesh[i];
            }
        }

        for (int i = 0; i < ___cableBaseGeometry.Length; i++)
            generator.GenerateCableMesh(___cableBaseGeometry[i], ___segmentLength, ___uvScaling, points, ____cableDynamicMesh[i]);
        return false;
    }

    private static Quaternion SafeFrame(Vector3 tangent, Vector3 up, Quaternion fallback)
    {
        if (tangent.sqrMagnitude < .00000001f) return fallback;
        if (Vector3.Cross(tangent, up).sqrMagnitude < .00000001f)
            up = Math.Abs(tangent.normalized.y) < .9f ? Vector3.up : Vector3.right;
        return Quaternion.LookRotation(tangent, up);
    }
}

public static class CablesManagementMeshVisibility
{
    private static readonly MethodInfo UpdateMesh = AccessTools.Method(typeof(CableVisual), "UpdateCableMesh");
    private static readonly MethodInfo DestroyMesh = AccessTools.Method(typeof(CableVisual), "DestroyCableMesh");

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CableVisual), "UpdateCable"),
            prefix: new HarmonyMethod(typeof(CablesManagementMeshVisibility), nameof(BeforeUpdate)));
    }

    private static bool BeforeUpdate(CableVisual __instance, MeshFilter[] ___cableMesh)
    {
        var tag = __instance.GetComponent<CablesManagementCableTag>();
        if (tag == null) return true;

        var spline = __instance.GetSpline();

        bool visible = (spline.Item2.Position - spline.Item1.Position).sqrMagnitude >= 1e-10f;
        if (visible) UpdateMesh.Invoke(__instance, null);
        else DestroyMesh.Invoke(__instance, null);
        foreach (MeshFilter layer in ___cableMesh)
            if (layer != null) layer.gameObject.SetActive(visible);
        return false;
    }
}

public sealed class CablesManagementPicking : MonoBehaviour
{
    private sealed class Surface
    {
        public MeshRenderer Renderer;
        public MeshFilter Filter;
        public Mesh Mesh;
        public Bounds Bounds;
        public Vector3[] Vertices;
        public int[] Triangles;
    }

    private static readonly HashSet<CablesManagementPicking> Loaded = new HashSet<CablesManagementPicking>();
    private static readonly RaycastHit[] Obstructions = new RaycastHit[100];
    private static readonly MethodInfo ClearInteraction = AccessTools.Method(typeof(PlayerObjectInteractionDetector), "DisableRayDetected");
    private static int _mask;
    private uint _id;
    private readonly List<Surface> _surfaces = new List<Surface>();

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CableVisual), nameof(CableVisual.Setup)),
            postfix: new HarmonyMethod(typeof(CablesManagementPicking), nameof(AfterSetup)));
        harmony.Patch(AccessTools.Method(typeof(CableVisual), "UpdateCableMesh"),
            postfix: new HarmonyMethod(typeof(CablesManagementPicking), nameof(AfterMesh)));
        harmony.Patch(AccessTools.Method(typeof(PlayerObjectInteractionDetector), nameof(PlayerObjectInteractionDetector.UpdateDetection)),
            postfix: new HarmonyMethod(typeof(CablesManagementPicking), nameof(AfterDetection)) { priority = Priority.Last });
    }

    private static void AfterSetup(CableVisual __instance, CableData data)
    {
        var picker = __instance.GetComponent<CablesManagementPicking>() ?? __instance.gameObject.AddComponent<CablesManagementPicking>();
        picker._id = data.Id;
        Loaded.Add(picker);
        picker.Refresh(__instance);
    }

    private static void AfterMesh(CableVisual __instance)
    {
        var picker = __instance.GetComponent<CablesManagementPicking>();
        if (picker != null) picker.Refresh(__instance);
    }

    private void Refresh(CableVisual cable)
    {
        _surfaces.Clear();
        if (cable.Cord == null) return;
        foreach (var renderer in cable.Cord.GetComponentsInChildren<MeshRenderer>(true))
        {
            var material = renderer.sharedMaterial;
            if (renderer.GetComponent<CablePaintStripe>() != null || material == null || material.shader == null) continue;
            string shader = material.shader.name;
            if (shader != "Custom/PlanetObject" && shader != "Custom/PlanetCablePower") continue;
            var filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) continue;
            mesh.RecalculateBounds();
            _surfaces.Add(new Surface
            {
                Renderer = renderer, Filter = filter, Mesh = mesh, Bounds = mesh.bounds,
                Vertices = mesh.vertices, Triangles = mesh.triangles
            });
        }
    }

    private bool Hit(Vector3 origin, Vector3 direction, ref float distance)
    {
        bool found = false;
        foreach (var surface in _surfaces)
        {
            if (surface.Renderer == null || !surface.Renderer.enabled || !surface.Renderer.gameObject.activeInHierarchy
                || surface.Filter == null || surface.Mesh == null || surface.Filter.sharedMesh != surface.Mesh) continue;
            Transform frame = surface.Renderer.transform;
            Vector3 localOrigin = frame.InverseTransformPoint(origin);
            Vector3 localDirection = frame.InverseTransformVector(direction);
            if (!surface.Bounds.IntersectRay(new Ray(localOrigin, localDirection))) continue;
            for (int i = 0; i + 2 < surface.Triangles.Length; i += 3)
            {
                if (!CablesManagementPickingMath.Triangle(localOrigin, localDirection,
                    surface.Vertices[surface.Triangles[i]], surface.Vertices[surface.Triangles[i + 1]],
                    surface.Vertices[surface.Triangles[i + 2]], distance, out float hit)) continue;
                distance = hit;
                found = true;
            }
        }
        return found;
    }

    private static void AfterDetection(PlayerObjectInteractionDetector __instance, CameraPhysicsData cameraPhysics,
        PlayerCableHoverTracker ____cableLabelTracker)
    {
        if (!__instance.isActiveAndEnabled || CablesManagementControls.MenuOpen) return;
        if (CablesManagementClipPicking.TrySelect(__instance, cameraPhysics, ____cableLabelTracker)) return;
        if (Loaded.Count == 0) return;
        var cables = GameRefs.Service<CablesClientTracker>();
        if (cables == null || ____cableLabelTracker == null) return;
        if (____cableLabelTracker.HasHoveredCable && cables.TryGetCableState(____cableLabelTracker.HoveredCableId, out _)) return;
        Vector3 direction = cameraPhysics.Direction.normalized;
        if (direction.sqrMagnitude < .5f) return;
        float distance = 3f;
        uint selected = 0;
        CableState selectedState = default;
        foreach (var picker in Loaded)
        {
            if (picker == null || !cables.TryGetCableState(picker._id, out CableState state)
                || state.PlugA.IsHeldByPlayer || state.PlugB.IsHeldByPlayer || !picker.Hit(cameraPhysics.CameraPos, direction, ref distance)) continue;
            selected = picker._id;
            selectedState = state;
        }
        if (selected == 0 || Occluded(cameraPhysics.CameraPos, direction, distance, selectedState)) return;
        ClearInteraction.Invoke(__instance, null);
        ____cableLabelTracker.HoveredCableId = selected;
        ____cableLabelTracker.HasHoveredCable = true;
    }

    private static bool Occluded(Vector3 origin, Vector3 direction, float distance, CableState cable)
    {
        if (_mask == 0) _mask = LayerMask.GetMask("Default", "Ship", "Interactable", "WorldObject");
        int count = ClientScene.PhysicsScene.Raycast(origin, direction, Obstructions, distance, _mask);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = Obstructions[i];
            if (hit.distance >= distance - .0015f) continue;
            if (hit.collider != null && hit.collider.CompareTag("ShipFloatingPart"))
            {
                var part = hit.collider.GetComponentInParent<ShipPartIdentifier>();
                var grid = hit.collider.GetComponentInParent<IShipPartPlacements>();
                if (part != null && grid != null && ConnectedClip(cable, grid.ShipId, part.Id)) continue;
            }
            return true;
        }
        return false;
    }

    private static bool ConnectedClip(CableState cable, uint ship, ushort id)
    {
        var ships = GameRefs.Service<ShipsClientTracker>();
        var context = new PartContext(ship, id);
        return (cable.PlugA.EntityPartSocket.PartContext == context || cable.PlugB.EntityPartSocket.PartContext == context)
            && ships != null && ships.TryGetStatefulPart(context, out var part) && CablesManagementMod.IsClip(part.Settings.id);
    }

    private void OnDestroy() => Loaded.Remove(this);
    internal static bool HasCableBefore(Vector3 origin, Vector3 direction, float distance)
    {
        var cables = GameRefs.Service<CablesClientTracker>();
        if (cables == null) return false;
        foreach (var picker in Loaded)
            if (picker != null && cables.TryGetCableState(picker._id, out _) && picker.Hit(origin, direction, ref distance)) return true;
        return false;
    }

    public static void Remove() { Loaded.Clear(); _mask = 0; CablesManagementClipPicking.Remove(); }
}

public static class CablesManagementPickingMath
{
    public static bool Triangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        float maximum, out float distance)
    {
        distance = 0f;
        Vector3 edge = b - a, other = c - a;
        Vector3 cross = Vector3.Cross(direction, other);
        float determinant = Vector3.Dot(edge, cross);
        float scale = edge.sqrMagnitude * cross.sqrMagnitude;
        if (scale <= 0f || determinant * determinant <= scale * 1e-12f) return false;
        float inverse = 1f / determinant;
        Vector3 offset = origin - a;
        float u = Vector3.Dot(offset, cross) * inverse;
        if (u < -1e-6f || u > 1.000001f) return false;
        Vector3 side = Vector3.Cross(offset, edge);
        float v = Vector3.Dot(direction, side) * inverse;
        if (v < -1e-6f || u + v > 1.000001f) return false;
        float hit = Vector3.Dot(other, side) * inverse;
        if (!(hit >= 0f && hit <= maximum)) return false;
        distance = hit;
        return true;
    }
}
