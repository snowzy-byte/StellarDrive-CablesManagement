using System.Collections.Generic;
using HarmonyLib;
using Ships.Cables.VisualObjects;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using SDModKit.Game;
using UnityEngine;
using UnityEngine.Rendering;

namespace CablesManagementMod;

public sealed class CablePaintAppearance : MonoBehaviour
{
    private uint _id, _revision;
    private bool _painted;
    private uint _rgb;
    private MeshRenderer _signalRenderer;
    private SignalCableVisual _signalVisual;
    private Color _originalEmission;
    private Color _negativeColor;
    private CablesClientTracker _cables;
    private MeshRenderer[] _renderers;
    private MaterialPropertyBlock[] _original;
    private CablePaintStripe[] _stripes;
    private Color _neutralColor;
    private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
    private static readonly Dictionary<uint, Texture2D> Palette = new Dictionary<uint, Texture2D>();

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CableVisual), nameof(CableVisual.Setup)),
            postfix: new HarmonyMethod(typeof(CablePaintAppearance), nameof(AfterSetup)));
        harmony.Patch(AccessTools.Method(typeof(CableVisual), "UpdateCableMesh"),
            postfix: new HarmonyMethod(typeof(CablePaintAppearance), nameof(AfterMesh)));
    }

    private static void AfterSetup(CableVisual __instance, CableData data)
    {
        var view = __instance.GetComponent<CablePaintAppearance>() ?? __instance.gameObject.AddComponent<CablePaintAppearance>();
        view._id = data.Id;
        view._cables = GameRefs.Service<CablesClientTracker>();
        if (__instance is SignalCableVisual signal)
        {
            if(view._signalVisual==null)
                view._originalEmission=Traverse.Create(signal).Field("fullSignalEmissionColor").GetValue<Color>();
            view._signalVisual=signal;
            view._signalRenderer=Traverse.Create(signal).Field("signalRenderer").GetValue<MeshRenderer>();
            view._negativeColor=Traverse.Create(signal).Field("negativeSignalColor").GetValue<Color>();
            view._neutralColor=Traverse.Create(signal).Field("neutralColor").GetValue<Color>();
        }
        var renderers = new List<MeshRenderer>();
        if (__instance.Cord != null)
            foreach (MeshRenderer renderer in __instance.Cord.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material material = renderer.sharedMaterial;
                if (renderer.GetComponent<CablePaintStripe>() != null) continue;
                if (material == null || material.shader == null) continue;
                string shader = material.shader.name;

                if (shader == "Custom/PlanetObject" || shader == "Custom/PlanetCablePower") renderers.Add(renderer);
            }
        if (view._renderers != null) { view.Refresh(); return; }
        view._renderers = renderers.ToArray();
        view._original = new MaterialPropertyBlock[view._renderers.Length];
        view._stripes = new CablePaintStripe[view._renderers.Length];
        for (int i = 0; i < view._renderers.Length; i++)
        {
            view._original[i] = new MaterialPropertyBlock();
            view._renderers[i].GetPropertyBlock(view._original[i]);
        }
        view.Refresh();
    }

    private static void AfterMesh(CableVisual __instance)
    {
        var view = __instance.GetComponent<CablePaintAppearance>();
        if (view == null || view._stripes == null) return;
        foreach (var stripe in view._stripes)
            if (stripe != null) { stripe.Rebuild(); stripe.SetVisible(view._painted); }
    }

    private void LateUpdate()
    {
        if (_revision != CablePaintStore.Revision) Refresh();
        if (_painted && _signalRenderer!=null && _cables!=null && _cables.TryGetSignalCableValue(_id,out double value))
        {

            _signalRenderer.GetPropertyBlock(_block);
            _block.SetTexture("_ColorPalette",Texture2D.whiteTexture);
            _block.SetColor("_Color",Color.Lerp(_negativeColor,ColorOf(_rgb).linear,1f-Mathf.Clamp01(-(float)value)));
            _signalRenderer.SetPropertyBlock(_block);
        }
    }

    private void Refresh()
    {
        _revision = CablePaintStore.Revision;
        bool painted = CablePaintStore.TryGet(_id, out uint rgb);
        _rgb=rgb;
        if(_signalVisual!=null)
        {
            Color emission=_originalEmission;
            if(painted)
            {
                float peak=Mathf.Max(_originalEmission.r,Mathf.Max(_originalEmission.g,_originalEmission.b));
                emission=ColorOf(CablePaintGlow.LighterColor(rgb)).linear*peak;
                emission.a=_originalEmission.a;
            }

            Traverse.Create(_signalVisual).Field("fullSignalEmissionColor").SetValue(emission);
        }
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
        {
            MeshRenderer renderer = _renderers[i];
            if (renderer == null) continue;
            if (!painted)
            {
                if (_painted) renderer.SetPropertyBlock(_original[i]);
                if (_stripes[i] != null) _stripes[i].SetVisible(false);
                continue;
            }
            if (_stripes[i] == null)
                _stripes[i] = CablePaintStripe.Create(renderer, _original[i], renderer == _signalRenderer ? (Color?)_neutralColor : null);
            _stripes[i].SetVisible(true);
            Material material = renderer.sharedMaterial;
            renderer.GetPropertyBlock(_block);
            if (material.HasProperty("_ColorPalette"))
            {
                _block.SetTexture("_ColorPalette", ColorTexture(rgb));
                if (material.HasProperty("_Color")) _block.SetColor("_Color", Color.white);
                if (material.HasProperty("_BaseColor")) _block.SetColor("_BaseColor", Color.white);
            }
            else if (material.HasProperty("_Color")) _block.SetColor("_Color", ColorOf(rgb).linear);
            renderer.SetPropertyBlock(_block);
        }
        _painted = painted;
    }

    public static Color ColorOf(uint rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
    public static uint RgbOf(Color color) => (uint)(Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f) << 16 |
        Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f) << 8 | Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f));

    private static Texture2D ColorTexture(uint rgb)
    {
        if (Palette.TryGetValue(rgb, out Texture2D texture) && texture != null) return texture;
        texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            name = "CablePaint_" + CablePaintStore.Hex(rgb), hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixel(0, 0, ColorOf(rgb));
        texture.Apply(false, true);
        Palette[rgb] = texture;
        return texture;
    }

    public static void Remove()
    {
        foreach (Texture2D texture in Palette.Values) if (texture != null) Object.Destroy(texture);
        Palette.Clear();
    }
}

public sealed class CablePaintStripe : MonoBehaviour
{
    private MeshFilter _source;
    private Mesh _mesh;
    private Material _material;
    private MeshRenderer _renderer;

    internal static CablePaintStripe Create(MeshRenderer source, MaterialPropertyBlock original, Color? nativeTint)
    {
        var root = new GameObject("CableOriginalColorStripe") { layer = source.gameObject.layer };
        root.transform.SetParent(source.transform, false);
        var stripe = root.AddComponent<CablePaintStripe>();
        stripe._source = source.GetComponent<MeshFilter>();
        stripe._mesh = new Mesh { name = "CableOriginalColorStripe", hideFlags = HideFlags.HideAndDontSave };
        root.AddComponent<MeshFilter>().sharedMesh = stripe._mesh;
        stripe._material = new Material(source.sharedMaterial) { name = "CableOriginalColorStripe", hideFlags = HideFlags.HideAndDontSave };

        foreach (string property in new[] { "_EmissionColor", "_ScrollEmissionColor", "_EmissiveColor" })
            if (stripe._material.HasProperty(property)) stripe._material.SetColor(property, Color.black);
        stripe._material.DisableKeyword("_EMISSION");
        stripe._renderer = root.AddComponent<MeshRenderer>();
        stripe._renderer.sharedMaterial = stripe._material;
        stripe._renderer.shadowCastingMode = ShadowCastingMode.Off;
        stripe._renderer.receiveShadows = true;
        stripe._renderer.SetPropertyBlock(original);
        var block = new MaterialPropertyBlock();
        stripe._renderer.GetPropertyBlock(block);
        foreach (string property in new[] { "_EmissionColor", "_ScrollEmissionColor", "_EmissiveColor" })
            if (stripe._material.HasProperty(property)) block.SetColor(property, Color.black);
        if (nativeTint.HasValue) block.SetColor("_Color", nativeTint.Value);
        stripe._renderer.SetPropertyBlock(block);
        stripe.Rebuild();
        return stripe;
    }

    internal void SetVisible(bool visible) => _renderer.enabled = visible && _mesh.vertexCount > 0;

    internal void Rebuild()
    {
        Mesh source = _source != null ? _source.sharedMesh : null;
        if (source == null) { _mesh.Clear(); return; }
        Vector3[] positions = source.vertices, normals = source.normals;
        Vector2[] uv = source.uv, palette = source.uv2;
        int[] triangles = source.triangles;
        if (uv.Length != positions.Length || normals.Length != positions.Length) { _mesh.Clear(); return; }
        var clipped = new List<CablePaintStripeMath.Vertex>();
        for (int i = 0; i + 2 < triangles.Length; i += 3)
            CablePaintStripeMath.ClipTriangle(Vertex(triangles[i]), Vertex(triangles[i + 1]), Vertex(triangles[i + 2]), clipped);
        var vertices = new List<Vector3>(clipped.Count);
        var outNormals = new List<Vector3>(clipped.Count);
        var outUv = new List<Vector2>(clipped.Count);
        var outPalette = new List<Vector2>(clipped.Count);
        var indices = new List<int>(clipped.Count);
        var colors = new List<Color>(clipped.Count);
        for (int i = 0; i < clipped.Count; i++)
        {
            var point = clipped[i];
            Vector3 normal = point.Normal.normalized;
            vertices.Add(point.Position + normal * .00035f);
            outNormals.Add(normal); outUv.Add(point.Uv); outPalette.Add(point.Palette);
            indices.Add(i); colors.Add(Color.white);
        }
        _mesh.Clear();
        if (vertices.Count > 65535) _mesh.indexFormat = IndexFormat.UInt32;
        _mesh.SetVertices(vertices); _mesh.SetNormals(outNormals);
        _mesh.SetUVs(0, outUv); _mesh.SetUVs(1, outPalette);
        _mesh.SetColors(colors); _mesh.SetTriangles(indices, 0);
        _mesh.RecalculateBounds();

        CablePaintStripeMath.Vertex Vertex(int index) => new CablePaintStripeMath.Vertex(positions[index], normals[index], uv[index],
            palette.Length == positions.Length ? palette[index] : Vector2.zero);
    }

    private void OnDestroy()
    {
        if (_mesh != null) Object.Destroy(_mesh);
        if (_material != null) Object.Destroy(_material);
    }
}

public static class CablePaintStripeMath
{
    public const float Min = .44f;
    public const float Max = .56f;

    public struct Vertex
    {
        public Vector3 Position, Normal;
        public Vector2 Uv, Palette;
        public Vertex(Vector3 position, Vector3 normal, Vector2 uv, Vector2 palette)
        { Position = position; Normal = normal; Uv = uv; Palette = palette; }
    }

    public static void ClipTriangle(Vertex a, Vertex b, Vertex c, List<Vertex> output)
    {
        var polygon = new List<Vertex>(5) { a, b, c };
        polygon = Clip(polygon, Min, true);
        polygon = Clip(polygon, Max, false);
        for (int i = 1; i + 1 < polygon.Count; i++)
        {
            output.Add(polygon[0]); output.Add(polygon[i]); output.Add(polygon[i + 1]);
        }
    }

    private static List<Vertex> Clip(List<Vertex> polygon, float edge, bool lower)
    {
        var result = new List<Vertex>(5);
        if (polygon.Count == 0) return result;
        Vertex previous = polygon[polygon.Count - 1];
        bool wasInside = lower ? previous.Uv.y >= edge : previous.Uv.y <= edge;
        foreach (Vertex current in polygon)
        {
            bool inside = lower ? current.Uv.y >= edge : current.Uv.y <= edge;
            if (inside != wasInside)
            {
                float t = (edge - previous.Uv.y) / (current.Uv.y - previous.Uv.y);
                result.Add(new Vertex(previous.Position + (current.Position - previous.Position) * t,
                    previous.Normal + (current.Normal - previous.Normal) * t,
                    previous.Uv + (current.Uv - previous.Uv) * t,
                    previous.Palette + (current.Palette - previous.Palette) * t));
            }
            if (inside) result.Add(current);
            previous = current; wasInside = inside;
        }
        return result;
    }
}
