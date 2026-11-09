using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using SDModKit.Game;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Services;
using Tools.Build;
using Tools.Build.Model;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using System;
using TMPro;
using UI.Common;
using UI.ToolBar;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CablesManagementMod;

public static class CablesManagementAlignment
{
    private static LineRenderer _line;
    private static Material _material;
    private static Texture2D _stripes;
    private static Color _hologramColor = new Color(.15f, .7f, 1f);
    private static float _lastPreview = -1f;
    private static bool _shaderReported;
    private static readonly List<CablesManagementAlignmentMath.Guide> Guides = new List<CablesManagementAlignmentMath.Guide>();
    private static readonly List<FloatingPartPlacement> Placements = new List<FloatingPartPlacement>();
    private static readonly List<Vector2> Footprints = new List<Vector2>();
    public const float Spacing = .05f;
    internal static Material GuideMaterial => _material != null ? _material : (_material = CreateGuideMaterial());

    internal static bool Held
    {
        get
        {
            return !CablesManagementControls.MenuOpen && CablesManagementControls.Held("Align");
        }
    }

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PartPreviewDetection), nameof(PartPreviewDetection.CalculateSnapDetectionUpdate)),
            postfix: new HarmonyMethod(typeof(CablesManagementAlignment), nameof(AfterDetection)) { priority = Priority.Last });
    }

    public static void Update()
    {
        if (!Held || Time.unscaledTime - _lastPreview > .2f) Hide();
    }

    private static void AfterDetection(SnapDetectionState state, ref SnapDetectionResults __result)
    {
        if (!Held || state.PartSettings == null || !CablesManagementMod.IsClip(state.PartSettings.id))
        {
            Hide();
            return;
        }
        _lastPreview = Time.unscaledTime;
        if (!__result.IsSnapped || __result.shipPartsProxy == null || !(__result.placement is FloatingPartPlacement placement))
        {
            Hide();
            return;
        }
        var ships = GameRefs.Service<ShipsClientTracker>();
        if (ships == null || !ships.TryGetShip(__result.shipPartsProxy.ShipId, out IShipStateRead ship))
        {
            Hide();
            return;
        }
        Guides.Clear();
        Placements.Clear();
        Footprints.Clear();
        foreach (var part in ship.StatefulParts)
        {
            if (!CablesManagementMod.IsClip(part.Settings.id) || !(part.Placement is FloatingPartPlacement reference)
                || part.State is IPickablePartState held && held.IsHeldByPlayer) continue;
            Quaternion rotation = part.LocalRotation;
            Guides.Add(new CablesManagementAlignmentMath.Guide
            {
                Position = part.LocalPosition, Normal = -(rotation * Vector3.forward), Axis = rotation * Vector3.up
            });
            Placements.Add(reference);
            Vector3 size = part.Settings.size;
            Footprints.Add(new Vector2(size.x * .5f, size.y * .5f));
        }
        var grid = __result.shipPartsProxy;
        var bounds = state.PartSettings.Bounds;
        Vector3 normal = -(placement.Rotation * Vector3.forward);
        if (!CablesManagementAlignmentMath.TryFind(placement.Coord, normal, Guides, (index, point, axis) =>
        {
            FloatingPartPlacement candidate = Placements[index];
            PartOrientation? up = CablesManagementSurfaceOrientation.SnapUp(axis, candidate.ForwardOrientation);
            if (!up.HasValue || Vector3.Dot(up.Value.GetVector3(), axis.normalized) < .999f) return false;
            candidate.UpOrientation = up.Value;
            candidate.Coord = point;
            return grid.IsFloatingPlacementFree(candidate, bounds, state.IgnoredPart);
        }, out CablesManagementAlignmentMath.Match match))
        {
            Hide();
            return;
        }
        FloatingPartPlacement referencePlacement = Placements[match.Index];
        PartOrientation? alignedUp = CablesManagementSurfaceOrientation.SnapUp(match.Axis, referencePlacement.ForwardOrientation);
        if (alignedUp.HasValue) referencePlacement.UpOrientation = alignedUp.Value;
        Vector3 anchor = Guides[match.Index].Position;
        if (match.Snapped)
        {
            if (CablesManagementAlignmentMath.TrySpacing(anchor, match.Position, match.Axis, normal, Spacing, out Vector3 spaced))
            {
                FloatingPartPlacement candidate = referencePlacement;
                candidate.Coord = spaced;
                if (grid.IsFloatingPlacementFree(candidate, bounds, state.IgnoredPart)) match.Position = spaced;
                else match.Snapped = false;
            }
            else match.Snapped = false;
        }
        if (match.Snapped)
        {
            referencePlacement.Coord = match.Position;
            __result.placement = referencePlacement;
            __result.pos = grid.ToWorldPos(referencePlacement);
            __result.pivot = null;
        }

        if (!CablesManagementAlignmentMath.TryGetSegment(anchor, match.Position, normal,
            CablesManagementLayout.ChannelHeight + .01f,
            CablesManagementAlignmentMath.OutlineInset(match.Axis, Guides[match.Index].Axis, normal, Footprints[match.Index]),
            state.PartSettings.size.y * .5f,
            out Vector3 localStart, out Vector3 localEnd))
        {
            Hide();
            return;
        }
        referencePlacement.Coord = localStart;
        Vector3 start = grid.ToWorldPos(referencePlacement);
        referencePlacement.Coord = localEnd;
        Vector3 end = grid.ToWorldPos(referencePlacement);
        ShowLine(start, end, match.Snapped);
    }

    private static void Hide()
    {
        if (_line != null) _line.enabled = false;
    }

    private static void ShowLine(Vector3 start, Vector3 end, bool snapped)
    {
        if (_line == null)
        {
            _material = GuideMaterial;
            if (_material == null) return;
            var root = new GameObject("CablesManagementAlignmentGuide") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(root);
            _line = root.AddComponent<LineRenderer>();
            _line.sharedMaterial = _material;
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.startWidth = _line.endWidth = .014f;
            _line.numCapVertices = 6;
            _line.alignment = LineAlignment.View;
            _line.textureMode = LineTextureMode.Tile;
            _line.textureScale = new Vector2(8f, 1f);
            _line.generateLightingData = true;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows = false;
        }

        _line.startWidth = _line.endWidth = snapped ? .018f : .014f;
        _line.startColor = _line.endColor = Color.white;
        _line.SetPosition(0, start);
        _line.SetPosition(1, end);
        _line.enabled = true;
    }

    private static Material CreateGuideMaterial()
    {

        var hologramService = GameRefs.Service<IShipHologramService>();
        Material template = hologramService == null ? null :
            Traverse.Create(hologramService).Field("hologramMat").GetValue<Material>();
        bool native = template != null && template.shader != null && template.shader.isSupported;
        Material material;
        if (native)
        {
            material = new Material(template);
            _hologramColor = Traverse.Create(hologramService).Field("validColor").GetValue<Color>();
        }
        else
        {
            Shader shader = null;
            foreach (string name in new[] { "Universal Render Pipeline/Unlit", "Unlit/Texture", "Sprites/Default", "UI/Default" })
            {
                Shader candidate = Shader.Find(name);
                if (candidate != null && candidate.isSupported) { shader = candidate; break; }
            }
            if (shader == null)
            {
                if (!_shaderReported) { MelonLogger.Warning("Cables Management: alignment line shader unavailable."); _shaderReported = true; }
                return null;
            }
            material = new Material(shader);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.One);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.Zero);
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)RenderQueue.Geometry + 20;
        }
        material.name = "CablesManagementHologramGuide";
        material.hideFlags = HideFlags.HideAndDontSave;
        _stripes = new Texture2D(32, 16, TextureFormat.RGBA32, false)
        {
            name = "CablesManagementHologramStripes", hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear
        };
        bool textureTint = !material.HasProperty("_Color") && !material.HasProperty("_BaseColor");
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
            {

                float shade = ((x + y) % 16) < 6 ? .48f : 1f;
                _stripes.SetPixel(x, y, textureTint
                    ? new Color(_hologramColor.r * shade, _hologramColor.g * shade, _hologramColor.b * shade, 1f)
                    : new Color(shade, shade, shade, 1f));
            }
        _stripes.Apply(false, true);
        foreach (string property in new[] { "_Albedo", "_BaseMap", "_MainTex" })
            if (material.HasProperty(property)) material.SetTexture(property, _stripes);
        if (material.HasProperty("_ColorPalette")) material.SetTexture("_ColorPalette", Texture2D.whiteTexture);
        if (material.HasProperty("_UseArray")) material.SetFloat("_UseArray", 0f);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", _hologramColor);
        if (material.HasProperty("_Color")) material.SetColor("_Color", _hologramColor);
        if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", native ? 0f : 1f);
        if (material.HasProperty("_ZTest")) material.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)CullMode.Off);
        if (material.HasProperty("_CullMode")) material.SetFloat("_CullMode", (float)CullMode.Off);
        return material;
    }

    public static void Remove()
    {
        Hide();
        if (_line != null) Object.Destroy(_line.gameObject);
        if (_material != null) Object.Destroy(_material);
        if (_stripes != null) Object.Destroy(_stripes);
        _line = null;
        _material = null;
        _stripes = null;
        Guides.Clear();
        Placements.Clear();
        Footprints.Clear();
    }
}

public static class CablesManagementKeyTip
{
    private static ToolKeyTip _tip;
    private static InputActionReference _action;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ToolKeyTipsUpdater), "GenerateTips"),
            prefix: new HarmonyMethod(typeof(CablesManagementKeyTip), nameof(BeforeGenerate)));
        harmony.Patch(AccessTools.Method(typeof(ToolKeyTipUi), nameof(ToolKeyTipUi.Setup)),
            prefix: new HarmonyMethod(typeof(CablesManagementKeyTip), nameof(BeforeTipSetup)),
            postfix: new HarmonyMethod(typeof(CablesManagementKeyTip), nameof(AfterTipSetup)));
    }

    private static ToolKeyTip GetTip()
    {
        if (_tip != null) return _tip;

        _action = CablesManagementControls.Reference("Align");
        _tip = new ToolKeyTip { action = _action, actionDesc = "Align" };
        return _tip;
    }

    private static void BeforeGenerate(bool ____buildToolIsActive, bool ____menuIsOpened,
        ushort? ____buildToolSelectedPartId, ref IReadOnlyCollection<ToolKeyTip> toolKeyTips)
    {
        if (!____buildToolIsActive || ____menuIsOpened || !____buildToolSelectedPartId.HasValue
            || !CablesManagementMod.IsClip(____buildToolSelectedPartId.Value)) return;
        ToolKeyTip tip = GetTip();
        var updated = new List<ToolKeyTip>(toolKeyTips);
        foreach (ToolKeyTip current in updated) if (ReferenceEquals(current, tip)) return;
        updated.Add(tip);
        toolKeyTips = updated;
    }

    private static void BeforeTipSetup(ToolKeyTip toolKeyTip, KeyTipIcon ___icon)
    {
        if (!ReferenceEquals(toolKeyTip, _tip) || ___icon == null) return;
        Traverse.Create(___icon).Field("actionIndex").SetValue(0);
    }

    private static void AfterTipSetup(ToolKeyTipUi __instance, ToolKeyTip toolKeyTip, KeyTipIcon ___icon)
    {
        if (!ReferenceEquals(toolKeyTip, _tip) || ___icon == null) return;
        TMP_Text keyText = Traverse.Create(___icon).Field("keyText").GetValue<TMP_Text>();
        if (keyText != null) keyText.text = CablesManagementControls.Label("Align");
        LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.transform as RectTransform);
    }

    public static void Remove()
    {
        _tip = null;
        _action = null;
    }

    public static void Refresh()
    {
        foreach (var hud in Resources.FindObjectsOfTypeAll<ToolKeyTipsUpdater>())
            if (hud != null && hud.isActiveAndEnabled) AccessTools.Method(typeof(ToolKeyTipsUpdater), "UpdateKeyTips").Invoke(hud, null);
    }
}

public static class CablesManagementAlignmentMath
{
    public static bool TrySpacing(Vector3 anchor, Vector3 position, Vector3 axis, Vector3 normal,
        float spacing, out Vector3 spaced)
    {
        spaced = position;
        if (float.IsNaN(spacing) || float.IsInfinity(spacing) || !Finite(anchor) || !Finite(position) || !Finite(axis) || !Finite(normal)
            || spacing < .05f || spacing > 2f || axis.sqrMagnitude < .000001f || normal.sqrMagnitude < .000001f) return false;
        normal = normal.normalized;
        axis = axis - normal * Vector3.Dot(axis, normal);
        if (axis.sqrMagnitude < .000001f) return false;
        axis = axis.normalized;
        Vector3 delta = position - anchor;
        float along = Vector3.Dot(delta, axis);
        double steps = Math.Round(along / spacing, MidpointRounding.AwayFromZero);
        if (steps == 0) return false;
        spaced = anchor + axis * ((float)steps * spacing) + normal * Vector3.Dot(delta, normal);
        return true;
    }

    private static bool Finite(Vector3 point) => !float.IsNaN(point.x) && !float.IsInfinity(point.x)
        && !float.IsNaN(point.y) && !float.IsInfinity(point.y) && !float.IsNaN(point.z) && !float.IsInfinity(point.z);

    public static bool TryGetSegment(Vector3 anchor, Vector3 projected, Vector3 normal,
        float height, float inset, out Vector3 start, out Vector3 end)
        => TryGetSegment(anchor, projected, normal, height, inset, inset, out start, out end);

    public static float OutlineInset(Vector3 direction, Vector3 up, Vector3 normal, Vector2 halfSize)
    {
        Vector3 side = Vector3.Cross(normal, up).normalized;
        float x = Math.Abs(Vector3.Dot(direction.normalized, side)), y = Math.Abs(Vector3.Dot(direction.normalized, up.normalized));
        return Math.Min(x > .000001f ? halfSize.x / x : float.PositiveInfinity,
            y > .000001f ? halfSize.y / y : float.PositiveInfinity);
    }

    public static bool TryGetSegment(Vector3 anchor, Vector3 projected, Vector3 normal,
        float height, float startInset, float endInset, out Vector3 start, out Vector3 end)
    {
        start = end = anchor;
        Vector3 delta = projected - anchor;
        if (normal.sqrMagnitude < .000001f || delta.magnitude <= startInset + endInset + .001f) return false;
        Vector3 direction = delta.normalized;
        Vector3 lift = normal.normalized * height;
        start = anchor + lift + direction * startInset;
        end = projected + lift - direction * endInset;
        return true;
    }

    public const float SnapDistance = .25f;
    public const float SearchDistance = 8f;
    public const float SurfaceTolerance = .02f;

    public struct Guide
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Axis;
    }

    public struct Match
    {
        public int Index;
        public Vector3 Position;
        public Vector3 Axis;
        public bool Snapped;
    }

    public static bool TryFind(Vector3 position, Vector3 normal, IReadOnlyList<Guide> guides,
        Func<int, Vector3, Vector3, bool> canPlace, out Match match)
    {
        match = default;
        if (normal.sqrMagnitude < .000001f) return false;
        normal = normal.normalized;
        bool found = false;
        float bestSideways = float.PositiveInfinity, bestDistance = float.PositiveInfinity;
        for (int i = 0; i < guides.Count; i++)
        {
            Guide guide = guides[i];
            if (guide.Normal.sqrMagnitude < .000001f || Vector3.Dot(normal, guide.Normal.normalized) < .999f)
                continue;
            Vector3 delta = position - guide.Position;
            float distance = delta.sqrMagnitude;
            float depth = Vector3.Dot(delta, normal);
            if (distance > SearchDistance * SearchDistance || Math.Abs(depth) > SurfaceTolerance) continue;
            Vector3 axis = guide.Axis - normal * Vector3.Dot(guide.Axis, normal);
            if (axis.sqrMagnitude < .000001f) continue;
            axis = axis.normalized;
            Vector3 across = Vector3.Cross(normal, axis).normalized;
            for (int direction = 0; direction < 2; direction++)
            {
                Vector3 line = direction == 0 ? axis : across;
                Vector3 projected = guide.Position + line * Vector3.Dot(delta, line) + normal * depth;
                float sideways = (projected - position).sqrMagnitude;
                bool snapped = sideways <= SnapDistance * SnapDistance && canPlace(i, projected, line);
                if (found)
                {
                    if (match.Snapped && !snapped) continue;
                    if (match.Snapped == snapped && (sideways > bestSideways + .000001f ||
                        Math.Abs(sideways - bestSideways) <= .000001f && distance >= bestDistance)) continue;
                }
                found = true;
                bestSideways = sideways;
                bestDistance = distance;
                match = new Match { Index = i, Position = projected, Axis = line, Snapped = snapped };
            }
        }
        return found;
    }
}
