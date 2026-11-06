using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Ships.Interface.Model.Parts;
using SDModKit.Game;
using Ships.Cables;
using Ships.Interface.Model.Parts.Common;
using Ships.Parts.Common;
using Ships.Parts.Common.Model;
using Ships.Parts.Common.Utils;
using Ships.Cables.VisualObjects;
using HarmonyLib;
using Core.Utils;
using Players.LocalClient.Interaction;
using Players.LocalClient.Interaction.Filters;
using Players.Services;
using WorldTracking.Interface.Values;
using Object = UnityEngine.Object;

namespace CablesManagementMod;

public static class CablesManagementModel
{
    private static List<CablesManagementObj.Piece> _pieces;
    private static readonly Dictionary<string, Mesh> Meshes = new Dictionary<string, Mesh>();
    private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

    public static GameObject Create(Material donor, string name)
    {
        if (_pieces == null)
        {
            using (Stream stream = typeof(CablesManagementModel).Assembly.GetManifestResourceStream("CablesManagementMod.stylized_cable_holder.obj"))
            {
                if (stream == null) throw new InvalidOperationException("CablesManagement embedded OBJ resource is missing.");
                using (var reader = new StreamReader(stream)) _pieces = CablesManagementObj.Read(reader);
            }
        }
        var root = new GameObject(name);
        try
        {
            foreach (CablesManagementObj.Piece piece in _pieces)
            {
                var child = new GameObject(piece.Name);
                child.transform.SetParent(root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = MeshFor(piece);
                var renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = MaterialFor(piece.MaterialName, donor);
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            return root;
        }
        catch
        {
            UnityEngine.Object.Destroy(root);
            throw;
        }
    }

    public static GameObject CreateCross(Material donor)
    {
        var root = new GameObject("CablesManagementJunctionModel");
        try
        {
            foreach (var piece in CablesManagementJunctionShape.Build())
            {
                var child = new GameObject(piece.Name);
                child.transform.SetParent(root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = MeshFor(piece);
                var renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = MaterialFor(piece.MaterialName, donor);
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            return root;
        }
        catch
        {
            UnityEngine.Object.Destroy(root);
            throw;
        }
    }

    private static Mesh MeshFor(CablesManagementObj.Piece piece)
    {
        string key = piece.Name + "\n" + piece.MaterialName;
        if (Meshes.TryGetValue(key, out Mesh mesh) && mesh != null) return mesh;
        mesh = new Mesh { name = "CablesManagement_" + piece.Name, hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(piece.Vertices);
        mesh.SetNormals(piece.Normals);
        mesh.SetTriangles(piece.Triangles, 0);
        var uv = new List<Vector2>();
        var palette = new List<Vector2>();
        var colors = new List<Color>();
        var tangents = new List<Vector4>();
        Vector2 palettePoint = PaletteFor(piece.MaterialName);
        foreach (Vector3 normal in piece.Normals)
        {
            uv.Add(new Vector2(.5f, .5f));
            palette.Add(palettePoint);
            colors.Add(Color.white);
            Vector3 tangent = Vector3.Cross(Mathf.Abs(normal.y) < .9f ? Vector3.up : Vector3.right, normal).normalized;
            tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
        }
        mesh.SetUVs(0, uv);
        mesh.SetUVs(1, palette);
        mesh.SetColors(colors);
        mesh.SetTangents(tangents);
        mesh.RecalculateBounds();
        Meshes[key] = mesh;
        return mesh;
    }

    private static Material MaterialFor(string materialName, Material donor)
    {
        string key = materialName + "\n" + (donor != null ? donor.GetInstanceID() : 0);
        if (Materials.TryGetValue(key, out Material material) && material != null) return material;
        material = CreateSurfaceMaterial(donor, "CablesManagement_" + materialName, ColorFor(materialName));
        Materials[key] = material;
        return material;
    }

    internal static Material CreateSurfaceMaterial(Material donor, string name, Color color)
    {

        if (donor == null || donor.shader == null || donor.shader.name != "Custom/PlanetObject" || !donor.shader.isSupported)
            throw new InvalidOperationException("CablesManagement requires the native PlanetObject surface material.");
        var material = new Material(donor) { name = name, hideFlags = HideFlags.HideAndDontSave };

        foreach (string property in new[] { "_Albedo", "_BaseMap", "_MainTex", "_ColorPalette" })
            if (material.HasProperty(property)) material.SetTexture(property, Texture2D.whiteTexture);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.black);
        if (material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", null);
        if (material.HasProperty("_UseArray")) material.SetFloat("_UseArray", 0f);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Metalness")) material.SetFloat("_Metalness", 0f);
        if (material.HasProperty("_Roughness")) material.SetFloat("_Roughness", 1f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .24f);
        if (material.HasProperty("_BumpScale")) material.SetFloat("_BumpScale", 0f);
        if (material.HasProperty("_VertexColorsEnabled")) material.SetFloat("_VertexColorsEnabled", 1f);
        if (material.HasProperty("_ReceiveShadows")) material.SetFloat("_ReceiveShadows", 1f);
        material.DisableKeyword("_NORMALMAP");
        material.DisableKeyword("_EMISSION");
        material.DisableKeyword("_RECEIVE_SHADOWS_OFF");
        return material;
    }

    private static Color ColorFor(string name)
    {
        switch (name)
        {
            case "DarkSlate_Plastic": return new Color(.18f, .24f, .28f);
            case "Industrial_LightGrey": return new Color(.64f, .68f, .71f);
            case "Bolt_LightMetal": return new Color(.79f, .82f, .85f);
            case "Ink_Black": return new Color(.025f, .03f, .035f);
            default: return Color.white;
        }
    }

    private static Vector2 PaletteFor(string name) => name == "Ink_Black"
        ? new Vector2(.015625f, .015625f)
        : name == "DarkSlate_Plastic" ? new Vector2(.078125f, .015625f)
        : new Vector2(.11365709f, .00599778f);
}

public static class CablesManagementJunctionShape
{
    public static List<CablesManagementObj.Piece> Build()
    {
        var plate = new CablesManagementObj.Piece("Junction_Baseplate", "Industrial_LightGrey");
        var seat = new CablesManagementObj.Piece("JunctionSeat", "Ink_Black");
        var body = new CablesManagementObj.Piece("JunctionBody", "DarkSlate_Plastic");
        var bolts = new CablesManagementObj.Piece("JunctionBolts", "Bolt_LightMetal");
        var sockets = new CablesManagementObj.Piece("BoltSockets", "Ink_Black");
        Block(plate, -.111f, -.111f, .111f, .111f, -.016f, 0f, .003f, .016f);
        Extrude(seat, new[] { new Vector2(-.027f,-.103f), new Vector2(.027f,-.103f), new Vector2(.027f,-.027f),
            new Vector2(.103f,-.027f), new Vector2(.103f,.027f), new Vector2(.027f,.027f),
            new Vector2(.027f,.103f), new Vector2(-.027f,.103f), new Vector2(-.027f,.027f),
            new Vector2(-.103f,.027f), new Vector2(-.103f,-.027f), new Vector2(-.027f,-.027f) }, -.019f, -.016f);
        foreach (int x in new[] { -1, 1 })
        foreach (int y in new[] { -1, 1 })
        {
            float minX = x < 0 ? -.087f : .028f, maxX = x < 0 ? -.028f : .087f;
            float minY = y < 0 ? -.087f : .028f, maxY = y < 0 ? -.028f : .087f;
            Block(body, minX, minY, maxX, maxY, -.108f, -.018f, .005f, .009f, .006f);
            Extrude(bolts, Hexagon(x * .096f, y * .096f, .007f), -.024f, -.015f);
            Extrude(sockets, Hexagon(x * .096f, y * .096f, .003f), -.0245f, -.024f);
        }
        return new List<CablesManagementObj.Piece> { plate, seat, body, bolts, sockets };
    }

    private static Vector2[] Outline(float left, float bottom, float right, float top, float corner)
    {
        return new[] { new Vector2(left + corner, bottom), new Vector2(right - corner, bottom),
            new Vector2(right, bottom + corner), new Vector2(right, top - corner),
            new Vector2(right - corner, top), new Vector2(left + corner, top),
            new Vector2(left, top - corner), new Vector2(left, bottom + corner) };
    }

    private static Vector2[] Hexagon(float x, float y, float radius)
    {
        var points = new Vector2[6];
        for (int i = 0; i < points.Length; i++)
        {
            double angle = Math.PI * i / 3;
            points[i] = new Vector2(x + (float)Math.Cos(angle) * radius, y + (float)Math.Sin(angle) * radius);
        }
        return points;
    }

    private static void Block(CablesManagementObj.Piece piece, float left, float bottom, float right, float top,
        float front, float back, float bevel, float corner, float taper = 0f)
    {
        Vector2[] outer = Outline(left, bottom, right, top, corner);
        Vector2[] rim = Outline(left + taper, bottom + taper, right - taper, top - taper, corner);
        Vector2[] cap = Outline(left + taper + bevel, bottom + taper + bevel,
            right - taper - bevel, top - taper - bevel, corner - bevel * .5f);
        Vector2[] backCap = Outline(left + bevel, bottom + bevel, right - bevel, top - bevel, corner - bevel * .5f);
        Cap(piece, cap, front, false);
        Join(piece, cap, front, rim, front + bevel);
        Join(piece, rim, front + bevel, outer, back - bevel);
        Join(piece, outer, back - bevel, backCap, back);
        Cap(piece, backCap, back, true);
    }

    private static void Extrude(CablesManagementObj.Piece piece, Vector2[] points, float front, float back)
    {
        Cap(piece, points, front, false);
        Join(piece, points, front, points, back);
        Cap(piece, points, back, true);
    }

    private static void Cap(CablesManagementObj.Piece piece, Vector2[] points, float z, bool reverse)
    {
        Vector2 center = Vector2.zero;
        foreach (var point in points) center += point * (1f / points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            var a = new Vector3(points[i].x, points[i].y, z);
            var b = new Vector3(points[(i + 1) % points.Length].x, points[(i + 1) % points.Length].y, z);
            var c = new Vector3(center.x, center.y, z);
            if (reverse) Triangle(piece, c, a, b);
            else Triangle(piece, c, b, a);
        }
    }

    private static void Join(CablesManagementObj.Piece piece, Vector2[] front, float frontZ, Vector2[] back, float backZ)
    {
        for (int i = 0; i < front.Length; i++)
        {
            int next = (i + 1) % front.Length;
            var a = new Vector3(front[i].x, front[i].y, frontZ);
            var b = new Vector3(front[next].x, front[next].y, frontZ);
            var c = new Vector3(back[next].x, back[next].y, backZ);
            var d = new Vector3(back[i].x, back[i].y, backZ);
            Triangle(piece, a, b, c);
            Triangle(piece, a, c, d);
        }
    }

    private static void Triangle(CablesManagementObj.Piece piece, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
        foreach (var point in new[] { a, b, c })
        {
            piece.Triangles.Add(piece.Vertices.Count);
            piece.Vertices.Add(point);
            piece.Normals.Add(normal);
        }
    }
}

public static class CablesManagementBadge
{
    private static readonly Dictionary<int,Mesh> Meshes=new Dictionary<int,Mesh>();
    private static readonly Dictionary<string,Material> Materials=new Dictionary<string,Material>();

    public static void Create(Transform parent,Material donor,CableType type,bool cross = false)
    {
        var kind=type==CableType.Power ? CablesManagementBadgeGeometry.Kind.Power :
            type==CableType.Fluid ? CablesManagementBadgeGeometry.Kind.Pipe : CablesManagementBadgeGeometry.Kind.Signal;
        foreach(bool plate in new[] { true,false })
        {
            int key=plate ? -1 : (int)kind;
            if(!Meshes.TryGetValue(key,out Mesh mesh) || mesh==null)
            {
                var data=CablesManagementBadgeGeometry.Build(kind,plate);
                mesh=new Mesh { name="CablesManagementBadge_"+key,hideFlags=HideFlags.HideAndDontSave };
                mesh.SetVertices(data.Vertices); mesh.SetTriangles(data.Triangles,0);
                var normals=new List<Vector3>(); var uv=new List<Vector2>(); var colors=new List<Color>();
                foreach(var vertex in data.Vertices) { normals.Add(-Vector3.forward); uv.Add(new Vector2(.5f,.5f)); colors.Add(Color.white); }
                mesh.SetNormals(normals); mesh.SetUVs(0,uv);
                Vector2 palettePoint = new Vector2((kind == CablesManagementBadgeGeometry.Kind.Signal ? 4.5f :
                    kind == CablesManagementBadgeGeometry.Kind.Power ? 5.5f : 6.5f) / 32f, .5f / 32f);
                var palette = new List<Vector2>();
                foreach (var vertex in data.Vertices) palette.Add(palettePoint);
                mesh.SetUVs(1,palette); mesh.SetColors(colors);
                mesh.RecalculateBounds(); Meshes[key]=mesh;
            }
            string materialKey=key+":"+donor.GetInstanceID();
            if(!Materials.TryGetValue(materialKey,out Material material) || material==null)
            {
                Color color=plate ? new Color(.035f,.05f,.06f) : Color.white;
                material=CablesManagementModel.CreateSurfaceMaterial(donor,"CablesManagementBadge_"+key,color);
                if (!plate) material.SetTexture("_ColorPalette", donor.GetTexture("_ColorPalette"));
                Materials[materialKey]=material;
            }
            var root=new GameObject(plate ? "CablesManagementTypeBadgePlate" : "CablesManagementType_"+kind);
            root.transform.SetParent(parent,false);
            if (cross)
            {
                root.transform.localScale = new Vector3(.75f, .75f, .75f);
                root.transform.localPosition = new Vector3(0f, .06f, -.095f);
            }
            root.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=root.AddComponent<MeshRenderer>(); renderer.sharedMaterial=material;
            renderer.shadowCastingMode=ShadowCastingMode.On; renderer.receiveShadows=true;
        }
    }
}

public sealed class CablesManagementVisual : DefaultShipPartVisuals, IContextAwarePart
{
    private sealed class Run
    {
        public MeshRenderer Renderer;
        public CablePaintStripe Stripe;
        public uint? Paint;
        public byte First, Second;
        public readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
    }

    private PartContext _context;
    private bool _hasContext;
    private float _nextCheck;
    private readonly List<Run> _runs = new List<Run>();

    public void SetContext(ShipPartContext context)
    {
        _context = context.Part;
        _hasContext = true;
        _runs.Clear();
        foreach (bool horizontal in new[] { false, true })
        {
            MeshRenderer renderer = transform.Find(horizontal ? "CablesManagementInternalRunHorizontal" : "CablesManagementInternalRun")?.GetComponent<MeshRenderer>();
            if (renderer != null) _runs.Add(new Run { Renderer = renderer, First = (byte)(horizontal ? 2 : 0), Second = (byte)(horizontal ? 3 : 1) });
        }
        _nextCheck = 0f;
    }

    private void LateUpdate()
    {
        if (!_hasContext || _runs.Count == 0 || Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + .2f;
        var connections = GameRefs.Service<ConnectionsClientTracker>();
        var cables = GameRefs.Service<CablesClientTracker>();
        foreach (Run run in _runs)
        {
            if (run.Renderer == null) continue;
            run.Renderer.enabled = connections != null &&
                (connections.IsSocketPhysicallyOccupied(new EntityPartSocket(_context.ShipId, _context.PartId, run.First)) ||
                 connections.IsSocketPhysicallyOccupied(new EntityPartSocket(_context.ShipId, _context.PartId, run.Second)));
            uint? color = null;
            if (cables != null)
                foreach (byte id in new[] { run.First, run.Second })
                    if (cables.TryGetCableId(new EntityPartSocket(_context.ShipId, _context.PartId, id), out uint cable)
                        && CablePaintStore.TryGet(cable, out uint rgb)) { color = rgb; break; }
            if (color != run.Paint)
            {
                run.Paint = color;
                if (!color.HasValue) run.Renderer.SetPropertyBlock(null);
                else
                {
                    if (run.Stripe == null) run.Stripe = CablePaintStripe.Create(run.Renderer, null, null);
                    run.Block.Clear();
                    run.Block.SetColor("_BaseColor", CablePaintAppearance.ColorOf(color.Value).linear);
                    run.Block.SetColor("_Color", CablePaintAppearance.ColorOf(color.Value).linear);
                    run.Renderer.SetPropertyBlock(run.Block);
                }
            }
            if (run.Stripe != null) run.Stripe.SetVisible(run.Renderer.enabled && color.HasValue);
        }
    }
}

public static class CablesManagementBridge
{
    private static readonly Dictionary<int, Mesh> Meshes = new Dictionary<int, Mesh>();
    private static readonly Dictionary<CableType, Material> Materials = new Dictionary<CableType, Material>();

    public static void Create(Transform parent, Material donor, CableType type, bool cross = false)
    {
        CreateRun(parent, donor, type, cross, false);
        if (cross) CreateRun(parent, donor, type, true, true);
    }

    private static void CreateRun(Transform parent, Material donor, CableType type, bool cross, bool horizontal)
    {
        var run = new GameObject(horizontal ? "CablesManagementInternalRunHorizontal" : "CablesManagementInternalRun");
        run.transform.SetParent(parent, false);
        if (horizontal) run.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        int key = (int)type + (cross ? 16 : 0);
        if (!Meshes.TryGetValue(key, out Mesh mesh) || mesh == null)
        {
            float radius = type == CableType.Fluid ? .021f : type == CableType.Power ? .012f : .008f;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uv = new List<Vector2>();
            const int sides = 12;
            for (int i = 0; i <= sides; i++)
            {
                float angle = i * 2f * Mathf.PI / sides;
                float x = Mathf.Cos(angle) * radius;
                float z = -CablesManagementLayout.ChannelHeight + Mathf.Sin(angle) * radius;
                float halfRun = cross ? CablesManagementLayout.CrossHalfRun : CablesManagementLayout.HalfRun;
                vertices.Add(new Vector3(x, -halfRun, z));
                vertices.Add(new Vector3(x, halfRun, z));
                uv.Add(new Vector2(0f, i / (float)sides));
                uv.Add(new Vector2(1f, i / (float)sides));
                if (i == sides) continue;
                int k = i * 2;
                triangles.AddRange(new[] { k, k + 1, k + 2, k + 2, k + 1, k + 3 });
            }
            mesh = new Mesh { name = "CablesManagementInternal_" + type + (cross ? "Junction" : ""), hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uv);
            var palette = new List<Vector2>();
            var colors = new List<Color>();
            for (int i = 0; i < vertices.Count; i++)
            {
                palette.Add(new Vector2(.078125f, .015625f));
                colors.Add(Color.white);
            }
            mesh.SetUVs(1, palette);
            mesh.SetColors(colors);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            Meshes[key] = mesh;
        }
        if (!Materials.TryGetValue(type, out Material material) || material == null)
        {
            Color color = type == CableType.Signal ? new Color(.10f, .17f, .11f) :
                type == CableType.Power ? new Color(.19f, .14f, .055f) : new Color(.10f, .16f, .19f);
            material = CablesManagementModel.CreateSurfaceMaterial(donor, "CablesManagementRun_" + type, color);
            Materials[type] = material;
        }
        run.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = run.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.enabled = false;
    }
}

public static class CablesManagementLayout
{
    public const float Scale = .005f;
    public const float ChannelHeight = .07825f;
    public const float HalfRun = .039f;
    public const float CrossScale = 1.5f;
    public const float CrossHalfRun = .075f * CrossScale;
    public static readonly Vector3 BoundsCenter = new Vector3(0f, 0f, -.067125f);
    public static readonly Vector3 BoundsSize = new Vector3(.155f, .095f, .13425f);
    public static readonly Vector3 CrossBoundsSize = new Vector3(.16f * CrossScale, .16f * CrossScale, .13425f);
    public static Vector3 Port(byte id) => new Vector3(0f, id == 0 ? -HalfRun : HalfRun, -ChannelHeight);
    public static Vector3 Inward(byte id) => id == 0 ? Vector3.up : Vector3.down;
    public static Vector3 CrossPort(byte id) => id < 2
        ? new Vector3(0f, id == 0 ? -CrossHalfRun : CrossHalfRun, -ChannelHeight)
        : new Vector3(id == 2 ? -CrossHalfRun : CrossHalfRun, 0f, -ChannelHeight);
    public static Vector3 CrossInward(byte id) => id < 2 ? Inward(id) : new Vector3(id == 2 ? 1f : -1f, 0f, 0f);
    public static byte[] RelayedPorts(byte id, bool cross)
    {
        int count = cross ? 4 : 2;
        if (id >= count) throw new System.ArgumentOutOfRangeException(nameof(id));
        return new[] { (byte)(id ^ 1) };
    }
}

public static class CablesManagementSocketInteraction
{
    public static void Configure(GameObject target, CableType type, bool cross = false)
    {

        target.tag = "Interactable";
        target.AddComponent<SocketInteractionObject>().cableType = type;
        var box = target.AddComponent<BoxCollider>();

        box.center = cross ? new Vector3(0f, .025f, -.005f * CablesManagementLayout.CrossScale) : new Vector3(0f, .025f, -.007f);
        box.size = cross ? new Vector3(.065f * CablesManagementLayout.CrossScale, .12f, .045f * CablesManagementLayout.CrossScale) : new Vector3(.07f, .08f, .07f);
    }

    public static void ConfigurePickup(GameObject target, bool cross)
    {
        var mountingPlate = target.AddComponent<BoxCollider>();
        mountingPlate.center = new Vector3(0f, 0f, -.008f);
        mountingPlate.size = cross ? new Vector3(CablesManagementLayout.CrossBoundsSize.x, CablesManagementLayout.CrossBoundsSize.y, .016f)
            : new Vector3(.155f, .095f, .016f);
        if (cross)
        {
            foreach (int x in new[] { -1, 1 })
            foreach (int y in new[] { -1, 1 })
            {
                var support = target.AddComponent<BoxCollider>();
                support.center = new Vector3(x * .0575f, y * .0575f, -.077f);
                support.size = new Vector3(.059f, .059f, .12f);
            }
            return;
        }
        for (int i = 0; i < 2; i++)
        {
            var jaw = target.AddComponent<BoxCollider>();
            jaw.center = new Vector3(i == 0 ? -.046f : .046f, 0f, -CablesManagementLayout.ChannelHeight);
            jaw.size = new Vector3(.025f, .078f, .12f);
        }
    }
}

public sealed class CablesManagementClipPicking : MonoBehaviour, IContextAwarePart
{
    private sealed class Surface
    {
        public MeshRenderer Renderer;
        public Bounds Bounds;
        public Vector3[] Vertices;
        public int[] Triangles;
        public bool Plate;
    }

    private static readonly HashSet<CablesManagementClipPicking> Loaded = new HashSet<CablesManagementClipPicking>();
    private static readonly RaycastHit[] Obstructions = new RaycastHit[100];
    private static readonly System.Reflection.FieldInfo Filters = AccessTools.Field(typeof(PlayerObjectInteractionDetector), "_interactionFilters");
    private static readonly System.Reflection.FieldInfo ActiveFilter = AccessTools.Field(typeof(PlayerObjectInteractionDetector), "_currentActiveFilter");
    private static readonly System.Reflection.MethodInfo ClearInteraction = AccessTools.Method(typeof(PlayerObjectInteractionDetector), "DisableRayDetected");
    private readonly List<Surface> _surfaces = new List<Surface>();
    private PartContext _context;
    private PartInteraction _interaction;
    private Collider _target;

    public void SetContext(ShipPartContext context)
    {
        _context = context.Part;
        _interaction = GetComponent<PartInteraction>();
        _target = GetComponent<Collider>();
        _surfaces.Clear();
        Transform visual = transform.parent.Find("CablesManagementVisual");
        if (visual == null || _interaction == null || _target == null) return;
        foreach (var renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (renderer.name.StartsWith("CablesManagementInternalRun") || renderer.GetComponent<CablePaintStripe>() != null) continue;
            Mesh mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) continue;
            _surfaces.Add(new Surface
            {
                Renderer = renderer, Bounds = mesh.bounds, Vertices = mesh.vertices, Triangles = mesh.triangles,
                Plate = renderer.name.EndsWith("Baseplate", StringComparison.Ordinal)
            });
        }
        if (isActiveAndEnabled) Loaded.Add(this);
    }

    private bool Hit(Vector3 origin, Vector3 direction, ref float distance, out bool plate)
    {
        bool found = false;
        plate = false;
        foreach (var surface in _surfaces)
        {
            if (surface.Renderer == null || !surface.Renderer.enabled || !surface.Renderer.gameObject.activeInHierarchy) continue;
            Transform frame = surface.Renderer.transform;
            Vector3 start = frame.InverseTransformPoint(origin), along = frame.InverseTransformVector(direction);
            if (!surface.Bounds.IntersectRay(new Ray(start, along))) continue;
            for (int i = 0; i + 2 < surface.Triangles.Length; i += 3)
            {
                if (!CablesManagementPickingMath.Triangle(start, along, surface.Vertices[surface.Triangles[i]],
                    surface.Vertices[surface.Triangles[i + 1]], surface.Vertices[surface.Triangles[i + 2]], distance, out float hit)) continue;
                distance = hit;
                plate = surface.Plate;
                found = true;
            }
        }
        return found;
    }

    internal static bool TrySelect(PlayerObjectInteractionDetector detector, CameraPhysicsData camera, PlayerCableHoverTracker hover)
    {
        if (Loaded.Count == 0) return false;
        var filters = Filters.GetValue(detector) as IObjectInteractionFilter[];
        IObjectInteractionFilter filter = null;
        if (filters != null)
            foreach (var candidate in filters)
                if (candidate is PlayerInteractableFilter) { filter = candidate; break; }
        if (filter == null) return false;
        Vector3 direction = camera.Direction.normalized;
        if (direction.sqrMagnitude < .5f) return false;
        float distance = float.PositiveInfinity;
        bool plate = false;
        CablesManagementClipPicking selected = null;
        foreach (var picker in Loaded)
        {
            if (picker == null || !picker.isActiveAndEnabled || picker._target == null || picker._interaction == null) continue;
            float reach = Mathf.Min(distance, picker._interaction.DetectionDistance);
            if (!picker.Hit(camera.CameraPos, direction, ref reach, out bool hitPlate) || !filter.IsInteractionRelevant(picker._target, reach)) continue;
            distance = reach;
            selected = picker;
            plate = hitPlate;
        }
        if (selected == null || CablesManagementPicking.HasCableBefore(camera.CameraPos, direction, distance)) return false;
        var ports = GameRefs.Service<PlayerShipSocketHoverTracker>();
        var socket = ports?.CurrentSocket ?? ports?.CurrentPlug;
        if (plate && socket != null && socket.EntityPartSocket.PartContext == selected._context) return false;
        int mask = LayerMask.GetMask("Default", "Ship", "Interactable", "WorldObject");
        int count = ClientScene.PhysicsScene.Raycast(camera.CameraPos, direction, Obstructions, distance, mask);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = Obstructions[i];
            if (hit.distance >= distance - .0015f || hit.collider != null && hit.collider.transform.IsChildOf(selected.transform.parent)) continue;
            return false;
        }
        if (!ReferenceEquals(ActiveFilter.GetValue(detector), filter)) ClearInteraction.Invoke(detector, null);
        filter.Activate(selected._target);
        ActiveFilter.SetValue(detector, filter);
        if (hover != null) { hover.HasHoveredCable = false; hover.HoveredCableId = 0; }
        return true;
    }

    private void OnEnable() { if (_surfaces.Count != 0) Loaded.Add(this); }
    private void OnDisable() => Loaded.Remove(this);
    private void OnDestroy() => Loaded.Remove(this);
    public static void Remove() => Loaded.Clear();
}

public static class CablesManagementBadgeGeometry
{
    public enum Kind { Signal, Power, Pipe }
    public sealed class Data
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<int> Triangles = new List<int>();
    }

    public static Data Build(Kind kind, bool plate)
    {
        var data = new Data();
        foreach (int side in new[] { -1, 1 })
        {
            if (plate)
            {
                Fan(data, side, true, new[] { new Vector2(-.8f,1), new Vector2(.8f,1), new Vector2(1,.8f),
                    new Vector2(1,-.8f), new Vector2(.8f,-1), new Vector2(-.8f,-1), new Vector2(-1,-.8f), new Vector2(-1,.8f) });
            }
            else if (kind == Kind.Power)
                Stroke(data, side, new[] { new Vector2(.45f,.9f), new Vector2(-.5f,.05f), new Vector2(.5f,.05f), new Vector2(-.45f,-.9f) }, .20f);
            else if (kind == Kind.Signal)
            {

                foreach(float radius in new[] { .48f,.96f,1.44f })
                {
                    for(int i=0;i<12;i++)
                    {
                        double a=Math.PI*(.25+.5*i/12), b=Math.PI*(.25+.5*(i+1)/12);
                        Quad(data,side,Arc(a,radius+.095f),Arc(a,radius-.095f),Arc(b,radius-.095f),Arc(b,radius+.095f));
                    }
                }
                Disc(data,side,new Vector2(0,-.82f),.15f);
            }
            else
            {

                Quad(data,side,new Vector2(-.75f,.82f),new Vector2(.75f,.82f),new Vector2(.75f,.58f),new Vector2(-.75f,.58f));
                Quad(data,side,new Vector2(-.75f,-.58f),new Vector2(.75f,-.58f),new Vector2(.75f,-.82f),new Vector2(-.75f,-.82f));
                Quad(data,side,new Vector2(-.49f,.58f),new Vector2(-.31f,.58f),new Vector2(-.31f,-.58f),new Vector2(-.49f,-.58f));
                Quad(data,side,new Vector2(.31f,.58f),new Vector2(.49f,.58f),new Vector2(.49f,-.58f),new Vector2(.31f,-.58f));
                Quad(data,side,new Vector2(-.065f,.35f),new Vector2(.065f,.35f),new Vector2(.065f,-.05f),new Vector2(-.065f,-.05f));
                Fan(data,side,false,new[]{new Vector2(0,-.35f),new Vector2(-.23f,-.05f),new Vector2(.23f,-.05f)});
            }
        }
        return data;
    }

    private static Vector2 Arc(double angle,float radius) => new Vector2((float)Math.Cos(angle)*radius,-.8f+(float)Math.Sin(angle)*radius*1.12f);

    private static void Stroke(Data data, int side, Vector2[] points, float width)
    {
        for (int i = 1; i < points.Length; i++)
        {
            Vector2 a = points[i-1], b = points[i];
            float dx = b.x-a.x, dy = b.y-a.y;
            float factor = width*.5f/(float)Math.Sqrt(dx*dx+dy*dy);
            var offset = new Vector2(-dy*factor,dx*factor);
            Quad(data,side,a-offset,a+offset,b+offset,b-offset);
        }
        foreach (Vector2 point in points) Disc(data,side,point,width*.5f);
    }

    private static void Disc(Data data,int side,Vector2 center,float radius)
    {
        var ring = new Vector2[10];
        for (int i=0;i<ring.Length;i++)
        {
            double angle=-2*Math.PI*i/ring.Length;
            ring[i]=center+new Vector2((float)Math.Cos(angle)*radius,(float)Math.Sin(angle)*radius);
        }
        Fan(data,side,false,ring);
    }

    private static Vector3 Point(int side, bool plate, Vector2 point) => new Vector3(
        side*.066f+point.x*(plate ? .009f : .006f),point.y*(plate ? .022f : .014f),plate ? -.020f : -.0208f);

    private static void Quad(Data data,int side,Vector2 a,Vector2 b,Vector2 c,Vector2 d)
    {
        int first=data.Vertices.Count;
        foreach(Vector2 point in new[] { a,b,c,d }) data.Vertices.Add(Point(side,false,point));
        data.Triangles.AddRange(new[] { first,first+1,first+2,first,first+2,first+3 });
    }

    private static void Fan(Data data,int side,bool plate,Vector2[] ring)
    {
        Vector2 center=new Vector2();
        foreach(Vector2 point in ring) center+=point*(1f/ring.Length);
        int first=data.Vertices.Count;
        data.Vertices.Add(Point(side,plate,center));
        foreach(Vector2 point in ring) data.Vertices.Add(Point(side,plate,point));
        for(int i=0;i<ring.Length;i++) data.Triangles.AddRange(new[] { first,first+1+i,first+1+(i+1)%ring.Length });
    }
}
