using System;
using System.Collections.Generic;
using Core.Utils;
using HarmonyLib;
using Imports.CablesMesh;
using Players.Interface.Model;
using Players.Interface.Services;
using Players.LocalClient.Controls;
using Players.LocalClient.Interaction;
using Players.Services;
using SDModKit.Game;
using SDModKit.UI;
using Ships.Cables;
using Ships.Cables.VisualObjects;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Services;
using TMPro;
using UI.Common;
using UI.Interface.Model;
using UI.Interface.Services;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace CablesManagementMod;

public sealed class CableNameMenu : MonoBehaviour, IClosableMenu
{
    private MenuTracker _tracker;
    public bool IsOpen { get; private set; }
    public bool Open()
    {
        if (IsOpen) return true;
        _tracker = GameRefs.Service<MenuTracker>();
        if (_tracker == null) return false;
        IsOpen = true;
        _tracker.TrackMenu(this);
        ModalInput.Capture();
        return true;
    }
    public void Close() => CableRouteView.CloseEditor();
    internal void Dismiss()
    {
        if (!IsOpen) return;
        IsOpen = false;
        try { if (_tracker != null) _tracker.UntrackMenu(this); }
        finally { ModalInput.Release(); }
    }
    private void LateUpdate() { if (IsOpen) ModalInput.Hold(); }
    private void OnDisable() => Dismiss();
    private void OnDestroy() => Dismiss();
}

public static class CableRouteView
{
    private sealed class GuideLine
    {
        public LineRenderer Renderer;
        public Vector3[] Points;
    }
    private static readonly List<GuideLine> Lines = new List<GuideLine>();
    private static Vector3 _cameraPosition;
    private static float _cameraAt = -100f;
    private static Vector3[] _preview;
    private static int _previewFrame = -10, _lineCount, _closedFrame = -1;
    private static uint _renameSeed, _previewCable;
    private static EntityPartSocket _previewTarget;
    private static EntityPartSocket _previewFixed;
    private static uint[] _previewExisting = Array.Empty<uint>();
    private static float _previewUntil;
    private static float _traceRefreshAt;
    private static readonly CableTraceSelection Selection = new CableTraceSelection();
    private static Material _traceMaterial, _previewMaterial;
    private static readonly HashSet<CableRouteLink> DrawnLinks = new HashSet<CableRouteLink>();
    private static GameObject _root, _tooltip, _editor;
    private static TMP_Text _label;
    private static TMP_InputField _name;
    private static CanvasScaler _scaler;
    private static CableNameMenu _menu;
    public static bool EditorOpen => _menu != null && _menu.IsOpen;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(CablesVisuals), "UpdateHeldCableVisuals"),
            postfix: new HarmonyMethod(typeof(CableRouteView), nameof(AfterHeldVisual)));
        harmony.Patch(AccessTools.Method(typeof(PlayerObjectInteractionDetector), nameof(PlayerObjectInteractionDetector.UpdateDetection)),
            postfix: new HarmonyMethod(typeof(CableRouteView), nameof(AfterDetection)));
        harmony.Patch(AccessTools.Method(typeof(PlayerLookControls), nameof(PlayerLookControls.OnLook)),
            prefix: new HarmonyMethod(typeof(CableRouteView), nameof(BeforeLook)));
        harmony.Patch(AccessTools.Method(typeof(PlayerSecondaryActionControl), nameof(PlayerSecondaryActionControl.OnSecondaryAction)),
            prefix: new HarmonyMethod(typeof(CableRouteView), nameof(BeforeLook)));
        harmony.Patch(AccessTools.Method(typeof(PlayerTertiaryControl), nameof(PlayerTertiaryControl.OnTertiaryAction)),
            prefix: new HarmonyMethod(typeof(CableRouteView), nameof(BeforeLook)));
        harmony.Patch(AccessTools.Method(typeof(PlayerEscapeControl), nameof(PlayerEscapeControl.OnEscape)),
            prefix: new HarmonyMethod(typeof(CableRouteView), nameof(BeforeEscape)));
    }

    private static bool BeforeLook() => !EditorOpen;
    private static void AfterDetection(CameraPhysicsData cameraPhysics)
    {
        _cameraPosition = cameraPhysics.CameraPos;
        _cameraAt = Time.unscaledTime;
    }
    private static bool BeforeEscape(InputAction.CallbackContext context)
    {
        if (_closedFrame == Time.frameCount) return false;
        if (!EditorOpen || !context.performed) return true;
        CloseEditor();
        return false;
    }

    public static void Update()
    {
        foreach (GuideLine line in Lines) if (line.Renderer != null) line.Renderer.enabled = false;
        _lineCount = 0;
        if (_root != null)
        {
            _tooltip.SetActive(false);
            _scaler.scaleFactor = Mathf.Min(Mathf.Max(1f, Screen.height / 720f),
                Mathf.Min(Mathf.Max(1f, Screen.width - 32f) / 420f, Mathf.Max(1f, Screen.height - 32f) / 192f));
        }
        var players = GameRefs.Service<PlayersClientTracker>();
        if (!Application.isFocused || players == null || !players.TryGetLocalPlayerState(out var state))
        {
            Selection.Clear();
            if (EditorOpen) CloseEditor();
            return;
        }
        bool walking = state.Type == PlayerStateType.Walking || state.Type == PlayerStateType.CheatFlying;
        bool holding = state.Type == PlayerStateType.WalkingHoldingCable || state.Type == PlayerStateType.CheatFlyingHoldingCable;
        if (!walking && !holding) { Selection.Clear(); if (EditorOpen) CloseEditor(); return; }
        Keyboard keyboard = Keyboard.current;
        if (EditorOpen)
        {
            Selection.Clear();
            if (!walking) { CloseEditor(); return; }
            if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)) SaveName();
            return;
        }
        var menus = GameRefs.Service<IClosableMenuService>();
        if (CablesManagementControls.MenuOpen) { Selection.Clear(); return; }
        var hover = GameRefs.Service<IPlayerCableHoverProvider>();
        var cables = GameRefs.Service<CablesClientTracker>();
        uint seed = walking && hover != null && hover.HasHoveredCable ? hover.HoveredCableId : 0;
        if (seed != 0 && (cables == null || !cables.TryGetCableState(seed, out CableState cable)
            || cable.PlugA.IsHeldByPlayer || cable.PlugB.IsHeldByPlayer)) seed = 0;
        if (seed != 0 && CablesManagementControls.Pressed("Rename"))
        {
            CablePaintMode.Exit();
            OpenEditor(seed);
            return;
        }
        uint[] selected = Selection.Update(CablesManagementControls.Held("Trace"), seed, CableRoutes.FindTrace);
        if (Selection.Seed != 0 && Time.unscaledTime >= _traceRefreshAt)
        {
            _traceRefreshAt = Time.unscaledTime + .2f;
            selected = Selection.Refresh(CableRoutes.FindTrace);
        }
        if (selected.Length > 0)
        {
            Trace(selected);
            string selectedName = CableRoutes.Name(Selection.Seed);
            ShowTooltip((string.IsNullOrEmpty(selectedName) ? "Cable route" : selectedName)
                + "  ·  " + selected.Length + " segments\nRelease " + CablesManagementControls.Label("Trace") + " to hide");
        }
        if (holding && Time.frameCount - _previewFrame <= 2 && _preview != null)
        {
            Draw(_preview, .014f, false);
            Trace(_previewExisting, true);
            ShowTooltip("Route preview  ·  " + (_previewExisting.Length + 1) + " segments\n" + RoutingTip(state.EntityId));
            return;
        }
        if (holding) { ShowTooltip(RoutingTip(state.EntityId)); return; }
        if (selected.Length > 0 || seed == 0) return;
        string name = CableRoutes.Name(seed);
        bool paintRoute = CablePaintMode.Active && CablesManagementControls.Held("PaintRoute");
        string info = string.IsNullOrEmpty(name) ? "Cable" : name;
        if (paintRoute)
        {
            uint[] route = CableRoutes.Find(seed);
            Trace(route);
            info += "  ·  " + route.Length + " segments";
        }
        info += CablePaintMode.Active ? "\n" + CablesManagementControls.Label("PaintRoute") + " + LMB  Paint route     "
            + CablesManagementControls.Label("Palette") + "  Colors" : "\n" + CablesManagementControls.Label("Rename") + "  Rename     "
            + CablesManagementControls.Label("Trace") + "  Trace";
        ShowTooltip(info);
    }

    private static string RoutingTip(uint cable) => "Cable routing: " + (CableRoutingStore.IsCurved(cable) ? "Curved" : "Straight")
        + "     " + CablesManagementControls.Label("ToggleCurve") + "  Toggle curve";

    private static void Trace(uint[] ids, bool preview = false)
    {
        var visuals = GameRefs.Service<CablesVisuals>();
        var cables = GameRefs.Service<CablesClientTracker>();
        var ships = GameRefs.Service<ShipsClientTracker>();
        if (visuals == null || cables == null || ships == null) return;
        DrawnLinks.Clear();
        foreach (uint id in ids)
        {
            if (visuals.TryGetCableVisual(id, out CableVisual visual)) Draw(Sample(visual), .016f, true);
        }
        foreach (var link in CableRoutes.TraceLinks(ids)) DrawLink(link, ships);
        if (preview) { AddBridge(_previewFixed, ships); AddBridge(_previewTarget, ships); }
    }

    private static void AddBridge(EntityPartSocket endpoint, ShipsClientTracker ships)
    {
        if (!ships.TryGetStatefulPart(endpoint.PartContext, out StatefulPart part) || !CablesManagementMod.IsClip(part.Settings.id)
            || endpoint.Socket.SocketId >= (CablesManagementMod.IsCross(part.Settings.id) ? 4 : 2)) return;
        var opposite = new EntityPartSocket(endpoint.EntityId, endpoint.Socket.PartId, (byte)(endpoint.Socket.SocketId ^ 1));
        DrawLink(new CableRouteLink(endpoint, opposite), ships);
    }

    private static void DrawLink(CableRouteLink link, ShipsClientTracker ships)
    {
        if (!DrawnLinks.Add(link)) return;
        if (link.A.PartContext == link.B.PartContext && (!ships.TryGetStatefulPart(link.A.PartContext, out StatefulPart part)
            || !CablesManagementMod.IsClip(part.Settings.id))) return;
        if (ships.TryGetVisualSocketTransform(link.A, out Transform a) && ships.TryGetVisualSocketTransform(link.B, out Transform b))
            Draw(new[] { a.position, b.position }, .016f, true);
    }

    private static Vector3[] Sample(CableVisual cable)
    {
        var spline = cable.GetSpline();
        var tag = cable.GetComponent<CablesManagementCableTag>();
        if (tag != null && !tag.Curved)
        {
            Transform root = cable.transform;
            Vector3 a = spline.Item1.Position, b = spline.Item2.Position;
            Vector3 da = root.InverseTransformDirection(-cable.PlugA.forward), db = root.InverseTransformDirection(cable.PlugB.forward);
            float overlap = CablesManagementCurve.MouthOverlap(b - a);
            if (tag.A) a -= da * overlap;
            if (tag.B) b += db * overlap;
            var path = CablesManagementPath.Build(a, b, da, db, tag.A, tag.B);
            var result = new Vector3[path.Count];
            for (int i = 0; i < path.Count; i++) result[i] = root.TransformPoint(path[i].Position);
            return result;
        }
        var positions = new Vector3[33];
        for (int i = 0; i < positions.Length; i++)
        {
            SimpleBezier.Evaluate(spline.Item1, spline.Item2, i / 32f, out var knot);
            positions[i] = cable.transform.TransformPoint(knot.Position);
        }
        return positions;
    }

    private static void AfterHeldVisual(CableData cableData, CableVisual cableVisual,
        IPlayersProviderClient ____players, ShipsClientTracker ____ships)
    {
        var state = cableData.State;
        bool heldA = state.PlugA.IsHeldByPlayer && ____players.IsLocalPlayer(state.PlugA.EntityId);
        bool heldB = state.PlugB.IsHeldByPlayer && ____players.IsLocalPlayer(state.PlugB.EntityId);
        if (!heldA && !heldB || heldA && heldB) return;
        if (!____players.TryGetLocalPlayerHoveredSocket(out uint shipId, out ushort partId, out byte socketId, out byte type)
            || type != (byte)state.Type) return;
        var target = new EntityPartSocket { EntityId = shipId, Socket = new PartSocket(partId, socketId) };
        if (!____ships.TryGetVisualSocketTransform(target, out Transform destination)) return;
        CablePlugSocket fixedPlug = heldA ? state.PlugB : state.PlugA;
        _previewFixed = fixedPlug.EntityPartSocket;
        bool targetGuide = ____ships.TryGetStatefulPart(target.PartContext, out StatefulPart targetPart) && CablesManagementMod.IsClip(targetPart.Settings.id);
        bool fixedGuide = !fixedPlug.IsHeldByPlayer && ____ships.TryGetStatefulPart(fixedPlug.EntityPartSocket.PartContext, out StatefulPart fixedPart)
            && CablesManagementMod.IsClip(fixedPart.Settings.id);
        if (!targetGuide && !fixedGuide) return;
        Transform a = heldA ? destination : cableVisual.PlugA;
        Transform b = heldB ? destination : cableVisual.PlugB;
        if (CableRoutingStore.IsCurved(cableData.Id)) _preview = CurvedPreview(cableVisual, destination, heldA, targetGuide);
        else
        {
            var path = CablesManagementPath.Build(a.position, b.position, -a.forward, b.forward,
                heldA ? targetGuide : fixedGuide, heldB ? targetGuide : fixedGuide);
            _preview = new Vector3[path.Count];
            for (int i = 0; i < path.Count; i++) _preview[i] = path[i].Position;
        }
        if (_previewCable != cableData.Id || _previewTarget != target || Time.unscaledTime >= _previewUntil)
        {
            _previewCable = cableData.Id;
            _previewTarget = target;
            _previewUntil = Time.unscaledTime + .1f;
            _previewExisting = CableRoutes.Preview(cableData.Id, target);
        }
        _previewFrame = Time.frameCount;
    }

    private static Vector3[] CurvedPreview(CableVisual cable, Transform destination, bool heldA, bool targetGuide)
    {
        var spline = cable.GetSpline();
        Transform root = cable.transform;
        SimpleBezier.Knot a = spline.Item1, b = spline.Item2;
        a.Position = root.TransformPoint(a.Position); b.Position = root.TransformPoint(b.Position);
        a.Tangent = root.TransformDirection(a.Tangent); b.Tangent = root.TransformDirection(b.Tangent);
        a.Rotation = root.rotation * a.Rotation; b.Rotation = root.rotation * b.Rotation;
        Transform held = heldA ? cable.PlugA : cable.PlugB;
        Quaternion rotation = destination.rotation * Quaternion.Inverse(held.rotation);
        SimpleBezier.Knot moving = heldA ? a : b;
        moving.Position = destination.position + (targetGuide ? Vector3.zero : rotation * (moving.Position - held.position));
        moving.Tangent = rotation * moving.Tangent;
        moving.Rotation = rotation * moving.Rotation;
        if (heldA) a = moving; else b = moving;
        float span = Mathf.Clamp01((b.Position - a.Position).magnitude);
        float handle = span * span * .35f + .05f;
        a.Tangent = a.Tangent.normalized * handle; b.Tangent = b.Tangent.normalized * handle;
        var points = new Vector3[33];
        for (int i = 0; i < points.Length; i++)
        {
            SimpleBezier.Evaluate(a, b, i / 32f, out var knot);
            points[i] = knot.Position;
        }
        return points;
    }

    private static void Draw(Vector3[] positions, float width, bool trace)
    {
        if (positions == null || positions.Length < 2) return;
        Material material = GuideMaterial(trace);
        if (material == null) return;
        if (_lineCount == Lines.Count)
        {
            var root = new GameObject("CableRouteGuide") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(root);
            var created = root.AddComponent<LineRenderer>();
            created.useWorldSpace = true;
            created.sharedMaterial = material;
            created.generateLightingData = true;
            created.numCornerVertices = 8;
            created.numCapVertices = 4;
            created.alignment = LineAlignment.View;
            created.textureMode = LineTextureMode.Tile;
            created.textureScale = new Vector2(8, 1);
            created.shadowCastingMode = ShadowCastingMode.Off;
            created.receiveShadows = false;
            Lines.Add(new GuideLine { Renderer = created });
        }
        GuideLine guide = Lines[_lineCount++];
        var line = guide.Renderer;
        line.sharedMaterial = material;
        if (guide.Points == null || guide.Points.Length != positions.Length) guide.Points = new Vector3[positions.Length];
        bool cameraReady = Time.unscaledTime - _cameraAt < .25f;
        for (int i = 0; i < positions.Length; i++)
            guide.Points[i] = positions[i] + (!trace && cameraReady ? (_cameraPosition - positions[i]).normalized * .03f : Vector3.zero);
        line.startWidth = line.endWidth = width;
        line.startColor = line.endColor = Color.white;
        line.positionCount = positions.Length;
        line.SetPositions(guide.Points);
        line.enabled = true;
    }

    private static Material GuideMaterial(bool trace)
    {
        Material cached = trace ? _traceMaterial : _previewMaterial;
        if (cached != null) return cached;
        Shader selected = null;
        foreach (string name in new[] { "Hidden/Internal-Colored", "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default", "UI/Default" })
        {
            Shader shader = Shader.Find(name);
            if (shader != null && shader.isSupported) { selected = shader; break; }
        }
        if (selected == null) return null;
        var material = new Material(selected)
        {
            name = trace ? "CableRouteTrace" : "CableRoutePreview",
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = trace ? (int)RenderQueue.Overlay : (int)RenderQueue.Transparent + 50
        };
        Color color = new Color(.15f, .8f, 1f, 1f);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        int depth = (int)(trace ? CompareFunction.Always : CompareFunction.LessEqual);
        material.SetInt("_SrcBlend", (int)BlendMode.One);
        material.SetInt("_DstBlend", (int)BlendMode.Zero);
        material.SetInt("_ZWrite", 0);
        material.SetInt("_ZTest", depth);
        material.SetInt("unity_GUIZTestMode", depth);
        material.SetInt("_Cull", (int)CullMode.Off);
        if (trace) _traceMaterial = material;
        else _previewMaterial = material;
        return material;
    }

    private static void Prepare()
    {
        if (_root != null) return;
        CablePaintPanel.Prepare();
        _root = new GameObject("CableRouteUI", typeof(RectTransform)) { layer = 5, hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 1001;
        _scaler = _root.AddComponent<CanvasScaler>(); _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        _root.AddComponent<GraphicRaycaster>();
        _menu = _root.AddComponent<CableNameMenu>();
        var tooltip = CablePaintPanel.Rect("CableLabel", _root.transform, 0, 0, 600, 56);
        tooltip.anchorMin = tooltip.anchorMax = new Vector2(.5f, .5f);
        tooltip.pivot = new Vector2(.5f, 1f); tooltip.anchoredPosition = new Vector2(0, -36);
        _tooltip = tooltip.gameObject;
        _label = CablePaintPanel.Text("Label", tooltip, "", 0, 0, 600, 56, 16, Color.white);
        _label.alignment = TextAlignmentOptions.Center; _label.richText = false;
        _label.outlineWidth = .16f; _label.outlineColor = new Color32(0, 0, 0, 220);
        _editor = CablePaintPanel.Rect("NameEditor", _root.transform, 0, 0, 420, 192).gameObject;
        var rect = (RectTransform)_editor.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = Vector2.zero;
        CablePaintPanel.Fill(rect, new Color(.075f, .095f, .11f, .98f)).raycastTarget = true;
        CablePaintPanel.Text("Title", rect, "Cable name", 18, 12, 310, 28, 20, Color.white);
        CablePaintPanel.Button(rect, "Close", "X", 372, 10, 30, 28, CloseEditor, false);
        var field = CablePaintPanel.Rect("Name", rect, 18, 53, 384, 36);
        var background = CablePaintPanel.Fill(field, new Color(.035f, .05f, .06f)); background.raycastTarget = true;
        var viewport = CablePaintPanel.Rect("Viewport", field, 8, 4, 368, 28);
        var text = CablePaintPanel.Text("Text", viewport, "", 0, 0, 368, 28, 17, Color.white);
        text.richText = false;
        _name = field.gameObject.AddComponent<TMP_InputField>();
        _name.targetGraphic = background; _name.textViewport = viewport; _name.textComponent = (TextMeshProUGUI)text;
        _name.characterLimit = 64; _name.lineType = TMP_InputField.LineType.SingleLine; _name.richText = false;
        CablePaintPanel.Configure(_name);
        CablePaintPanel.Text("Hint", rect, "Applies to this route. Stops at branches.", 18, 98, 384, 24, 14, new Color(.64f, .69f, .72f));
        CablePaintPanel.Button(rect, "Clear", "Clear", 18, 140, 100, 32, () => { _name.text = ""; SaveName(); }, true);
        CablePaintPanel.Button(rect, "Cancel", "Cancel", 178, 140, 100, 32, CloseEditor, true);
        CablePaintPanel.Button(rect, "Save", "Save", 294, 140, 108, 32, SaveName, true);
        _editor.SetActive(false); _tooltip.SetActive(false);
    }

    private static void ShowTooltip(string text)
    {
        Prepare(); _tooltip.SetActive(true); _label.text = text;
    }

    internal static void OpenEditor(uint seed)
    {
        Prepare();
        GameRefs.Service<LocalSelectedToolTracker>()?.SelectedTool?.PrimaryActionEnd();
        if (!_menu.Open()) return;
        _renameSeed = seed; _editor.SetActive(true); _tooltip.SetActive(false);
        _name.SetTextWithoutNotify(CableRoutes.Name(seed) ?? ""); _name.ActivateInputField();
    }

    private static void SaveName()
    {
        if (EditorOpen && CableRoutes.RequestName(_renameSeed, _name.text)) CloseEditor();
    }

    public static void CloseEditor()
    {
        _menu?.Dismiss();
        if (_editor != null) _editor.SetActive(false);
        _renameSeed = 0; _closedFrame = Time.frameCount;
    }

    public static void Remove()
    {
        CloseEditor();
        foreach (GuideLine line in Lines) if (line.Renderer != null) UnityEngine.Object.Destroy(line.Renderer.gameObject);
        Lines.Clear();
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = _tooltip = _editor = null; _menu = null; _name = null; _label = null; _scaler = null;
        _preview = null; _previewFrame = -10;
        _previewExisting = Array.Empty<uint>(); _previewCable = 0; _previewUntil = 0f;
        Selection.Clear();
        DrawnLinks.Clear();
        if (_traceMaterial != null) UnityEngine.Object.Destroy(_traceMaterial);
        if (_previewMaterial != null) UnityEngine.Object.Destroy(_previewMaterial);
        _traceMaterial = _previewMaterial = null;
        _cameraAt = -100f;
    }
}
