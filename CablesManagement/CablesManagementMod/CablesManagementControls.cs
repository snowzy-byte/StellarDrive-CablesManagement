using System;
using System.Collections.Generic;
using System.Collections;
using HarmonyLib;
using Players.LocalClient.Controls.Rebinding;
using UI.Common;
using SDModKit.Game;
using UnityEngine;
using UnityEngine.InputSystem;
using Players.Interface.Services;
using Players.LocalClient.Controls;
using TMPro;
using UI.Options;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CablesManagementMod;

public static class CablesManagementControls
{
    private const string Preference = "CablesManagement_KeybindOverrides";
    private static InputActionAsset _asset;
    private static readonly Dictionary<string, InputActionReference> References = new Dictionary<string, InputActionReference>();
    public static readonly string[] Names = { "Align", "Palette", "Trace", "Rename", "PaintRoute", "ToggleCurve" };
    public static readonly string[] Labels = { "Align clips", "Cable colors", "Trace cable route", "Rename cable route", "Paint route modifier", "Toggle cable curve" };
    public static bool MenuOpen
    {
        get
        {
            var tracker = GameRefs.Service<MenuTracker>();
            return tracker != null && Traverse.Create(tracker).Field("_activeMenus").GetValue<IList>()?.Count > 0;
        }
    }
    private const string Definition = @"{""maps"":[{""name"":""CablesManagement"",""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d180"",
        ""actions"": [
            {""name"":""Align"",""type"":""Button"",""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d181"",""expectedControlType"":""Button""},
            {""name"":""Palette"",""type"":""Button"",""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d182"",""expectedControlType"":""Button""},
            {""name"":""Trace"",""type"":""Button"",""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d183"",""expectedControlType"":""Button""},
            {""name"":""Rename"",""type"":""Button"",""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d184"",""expectedControlType"":""Button""},
            {""name"":""PaintRoute"",""type"":""Button"",""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d185"",""expectedControlType"":""Button""},
            {""name"":""ToggleCurve"",""type"":""Button"",""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d186"",""expectedControlType"":""Button""}],
        ""bindings"": [
            {""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d191"",""path"":""<Keyboard>/leftAlt"",""action"":""Align""},
            {""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d192"",""path"":""<Keyboard>/p"",""action"":""Palette""},
            {""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d193"",""path"":""<Keyboard>/h"",""action"":""Trace""},
            {""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d194"",""path"":""<Keyboard>/n"",""action"":""Rename""},
            {""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d195"",""path"":""<Keyboard>/leftShift"",""action"":""PaintRoute""},
            {""id"":""bb6ef445-f8d2-4bbd-bbad-1abbca34d196"",""path"":""<Keyboard>/f6"",""action"":""ToggleCurve""}]}]}";

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PlayerKeybindsService), "LoadBindings"),
            prefix: new HarmonyMethod(typeof(CablesManagementControls), nameof(Ensure)));
        harmony.Patch(AccessTools.Method(typeof(PlayerKeybindsService), nameof(PlayerKeybindsService.SaveKeybindings)),
            postfix: new HarmonyMethod(typeof(CablesManagementControls), nameof(AfterSave)));
        harmony.Patch(AccessTools.Method(typeof(PlayerKeybindsService), nameof(PlayerKeybindsService.ResetKeybindings)),
            prefix: new HarmonyMethod(typeof(CablesManagementControls), nameof(BeforeReset)));
        CablesManagementControlsMenu.Install(harmony);
    }

    public static void Update() => Ensure();

    private static void Ensure()
    {
        if (_asset != null) return;
        _asset = InputActionAsset.FromJson(Definition);
        _asset.name = "CablesManagementControls";
        _asset.hideFlags = HideFlags.HideAndDontSave;
        if (PlayerPrefs.HasKey(Preference))
        {
            try { _asset.LoadBindingOverridesFromJson(PlayerPrefs.GetString(Preference)); }
            catch (ArgumentException) { _asset.RemoveAllBindingOverrides(); }
        }
        _asset.Enable();
    }

    public static InputActionReference Reference(string name)
    {
        Ensure();
        if (References.TryGetValue(name, out var reference) && reference != null) return reference;
        reference = InputActionReference.Create(_asset.FindAction(name, true));
        reference.hideFlags = HideFlags.HideAndDontSave;
        References[name] = reference;
        return reference;
    }

    public static bool Held(string name)
    {
        if (!Application.isFocused || CableRouteView.EditorOpen || CablesManagementControlsMenu.Rebinding) return false;
        InputAction action = Reference(name).action;
        if (action.IsPressed()) return true;
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || action.bindings.Count == 0) return false;
        string path = action.bindings[0].effectivePath;
        return name == "Align" && path == "<Keyboard>/leftAlt" && keyboard.rightAltKey.isPressed
            || name == "PaintRoute" && path == "<Keyboard>/leftShift" && keyboard.rightShiftKey.isPressed;
    }

    public static bool Pressed(string name) => Application.isFocused && !CableRouteView.EditorOpen && !CablesManagementControlsMenu.Rebinding
        && Reference(name).action.WasPressedThisFrame();

    public static string Label(string name)
    {
        string label = KeyDisplayUtils.GetDisplayString(Reference(name));
        return string.IsNullOrEmpty(label) ? "Unbound" : label;
    }

    private static void AfterSave()
    {
        Ensure();
        PlayerPrefs.SetString(Preference, _asset.SaveBindingOverridesAsJson());
        PlayerPrefs.Save();
        CablesManagementKeyTip.Refresh();
        CablePaintKeyTips.Refresh();
        CablesManagementControlsMenu.Refresh();
    }

    private static void BeforeReset()
    {
        CablesManagementControlsMenu.CancelRebinding();
        Ensure();
        _asset.RemoveAllBindingOverrides();
    }

    public static void Remove()
    {
        CablesManagementControlsMenu.Remove();
        foreach (var reference in References.Values) if (reference != null) UnityEngine.Object.Destroy(reference);
        References.Clear();
        if (_asset != null) { _asset.Disable(); UnityEngine.Object.Destroy(_asset); }
        _asset = null;
    }
}

public sealed class CablesManagementControlRow : MonoBehaviour
{
    internal ControlKeybindInput Control;
    internal TMP_Text Key;
    internal string Action;
    private void OnDisable() => CablesManagementControlsMenu.Cancel(this);
}

public sealed class CablesManagementControlsSection : MonoBehaviour
{
    internal Transform Host;
    private void OnDestroy() => CablesManagementControlsMenu.Forget(Host);
}

public static class CablesManagementControlsMenu
{
    private static readonly string[] Actions = { "Align", "Palette", "Trace", "Rename", "PaintRoute", "ToggleCurve" };
    private static readonly string[] Labels = { "Align clips", "Cable colors", "Trace cable route", "Rename cable route", "Paint route modifier", "Toggle cable curve" };
    private static readonly Dictionary<Transform, GameObject> Sections = new Dictionary<Transform, GameObject>();
    private static InputActionRebindingExtensions.RebindingOperation _operation;
    private static CablesManagementControlRow _currentRow;
    private static InputAction _currentAction;
    private static bool _wasEnabled, _building;
    private static int _finishedFrame = -1;
    public static bool Rebinding => _operation != null || _finishedFrame == Time.frameCount;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ControlKeybindInput), "OnEnable"),
            postfix: new HarmonyMethod(typeof(CablesManagementControlsMenu), nameof(AfterRowEnabled)));
        harmony.Patch(AccessTools.Method(typeof(ControlKeybindInput), "Start"),
            postfix: new HarmonyMethod(typeof(CablesManagementControlsMenu), nameof(AfterRowEnabled)));
        harmony.Patch(AccessTools.Method(typeof(ControlKeybindInput), nameof(ControlKeybindInput.StartRebinding)),
            prefix: new HarmonyMethod(typeof(CablesManagementControlsMenu), nameof(BeforeRebinding)));
        harmony.Patch(AccessTools.Method(typeof(PlayerEscapeControl), nameof(PlayerEscapeControl.OnEscape)),
            prefix: new HarmonyMethod(typeof(CablesManagementControlsMenu), nameof(BeforeEscape)));
    }

    private static bool BeforeEscape() => !Rebinding;
    public static void CancelRebinding() => CancelCurrent();

    private static void AfterRowEnabled(ControlKeybindInput __instance)
    {
        if (_building || __instance == null || __instance.GetComponentInParent<CablesManagementControlsSection>() != null) return;
        Transform row = FindRow(__instance, out TMP_Text sourceLabel);
        if (row == null || row.parent == null || sourceLabel == null) return;
        Transform host = row.parent;
        ScrollRect scroll = row.GetComponentInParent<ScrollRect>();
        if (scroll != null && scroll.content != null && row.IsChildOf(scroll.content)) host = scroll.content;
        if (Sections.TryGetValue(host, out GameObject existing) && existing != null) return;
        Build(host, row, sourceLabel);
    }

    private static Transform FindRow(ControlKeybindInput control, out TMP_Text label)
    {
        label = null;
        TMP_Text key = Traverse.Create(control).Field("keyText").GetValue<TMP_Text>();
        Transform best = null;
        for (Transform node = control.transform; node != null; node = node.parent)
        {
            if (node.GetComponent<Canvas>() != null || node.GetComponent<ScrollRect>() != null) break;
            if (node.GetComponentsInChildren<ControlKeybindInput>(true).Length != 1) break;
            foreach (TMP_Text candidate in node.GetComponentsInChildren<TMP_Text>(true))
                if (candidate != key && candidate.GetComponentInParent<TMP_InputField>() == null)
                {
                    best = node;
                    label = candidate;
                    break;
                }
        }
        return best;
    }

    private static void Build(Transform host, Transform template, TMP_Text sourceLabel)
    {
        var sourceRect = template as RectTransform;
        if (sourceRect == null) return;
        float rowHeight = Mathf.Max(36f, sourceRect.rect.height);
        float height = 40f + Actions.Length * rowHeight;
        var section = new GameObject("CablesManagementControls", typeof(RectTransform));
        section.SetActive(false);
        section.layer = template.gameObject.layer;
        section.transform.SetParent(host, false);
        var marker = section.AddComponent<CablesManagementControlsSection>();
        marker.Host = host;
        Sections[host] = section;
        _building = true;
        try
        {
            var rect = (RectTransform)section.transform;
            rect.anchorMin = new Vector2(0, 1); rect.anchorMax = new Vector2(1, 1); rect.pivot = new Vector2(.5f, 1);
            rect.sizeDelta = new Vector2(0, height);
            var layout = section.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;
            var element = section.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            MakeHeader(section.transform, sourceLabel);
            for (int i = 0; i < Actions.Length; i++) MakeRow(section.transform, template, Actions[i], Labels[i], rowHeight);
            if (host.GetComponent<LayoutGroup>() == null) PositionManually(host as RectTransform, rect, height);
            section.SetActive(true);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            if (host is RectTransform parentRect) LayoutRebuilder.MarkLayoutForRebuild(parentRect);
        }
        catch
        {
            Sections.Remove(host);
            UnityEngine.Object.Destroy(section);
            throw;
        }
        finally { _building = false; }
    }

    private static void MakeHeader(Transform parent, TMP_Text source)
    {
        var root = new GameObject("CablesManagementHeading", typeof(RectTransform));
        root.layer = parent.gameObject.layer;
        root.transform.SetParent(parent, false);
        var label = root.AddComponent<TextMeshProUGUI>();
        label.font = source.font; label.fontSharedMaterial = source.fontSharedMaterial;
        label.fontSize = source.fontSize; label.color = source.color;
        label.fontStyle = source.fontStyle | FontStyles.Bold;
        label.alignment = TextAlignmentOptions.MidlineLeft; label.extraPadding = true;
        label.raycastTarget = false; label.text = "Cables Management";
        var layout = root.AddComponent<LayoutElement>(); layout.minHeight = layout.preferredHeight = 40f;
    }

    private static void MakeRow(Transform parent, Transform template, string action, string label, float height)
    {
        GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
        clone.name = "CablesManagementBinding_" + action;
        clone.SetActive(false);
        var control = clone.GetComponentInChildren<ControlKeybindInput>(true);
        var key = Traverse.Create(control).Field("keyText").GetValue<TMP_Text>();
        foreach (MonoBehaviour component in clone.GetComponentsInChildren<MonoBehaviour>(true))
            if (component.GetType().FullName == "UnityEngine.Localization.Components.LocalizeStringEvent" || component is KeyTipStringUpdater)
                component.enabled = false;
        foreach (TMP_Text text in clone.GetComponentsInChildren<TMP_Text>(true))
            if (text != key) { text.text = label; text.richText = false; }
        control.actionRef = CablesManagementControls.Reference(action);
        control.mirroredActions = new List<InputActionReference>();
        Traverse.Create(control).Field("ignoreMouse").SetValue(true);
        Traverse.Create(control).Field("useBindingIndex").SetValue(true);
        Traverse.Create(control).Field("bindingIndex").SetValue(0);
        var row = control.gameObject.AddComponent<CablesManagementControlRow>();
        row.Control = control; row.Key = key; row.Action = action;
        Button button = control.GetComponent<Button>() ?? control.GetComponentInChildren<Button>(true) ?? clone.GetComponentInChildren<Button>(true);
        if (button == null || key == null) throw new InvalidOperationException("Native controls row has no key binding button.");
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(control.StartRebinding);
        var element = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = height;
        clone.SetActive(true);
        Refresh(row);
    }

    private static void PositionManually(RectTransform host, RectTransform section, float height)
    {
        if (host == null) return;
        float bottom = 0;
        var corners = new Vector3[4];
        foreach (RectTransform child in host)
        {
            if (child == section) continue;
            child.GetWorldCorners(corners);
            for (int i = 0; i < corners.Length; i++) bottom = Mathf.Max(bottom, -host.InverseTransformPoint(corners[i]).y + host.rect.yMax);
        }
        section.anchoredPosition = new Vector2(0, -bottom - 12f);
        host.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(host.rect.height, bottom + height + 24f));
    }

    private static bool BeforeRebinding(ControlKeybindInput __instance)
    {
        var row = __instance.GetComponent<CablesManagementControlRow>();
        if (row == null) return true;
        BeginRebinding(row);
        return false;
    }

    private static void BeginRebinding(CablesManagementControlRow row)
    {
        CancelCurrent();
        InputAction action = row.Control.actionRef == null ? null : row.Control.actionRef.action;
        if (action == null || action.bindings.Count == 0 || GameRefs.Service<IPlayerKeybinds>() == null) return;
        string previous = action.bindings[0].overridePath;
        _currentRow = row; _currentAction = action; _wasEnabled = action.enabled;
        action.Disable(); row.Key.text = "...";
        _operation = action.PerformInteractiveRebinding(0)
            .WithCancelingThrough("<Keyboard>/escape")
            .WithControlsExcluding("Mouse")
            .OnMatchWaitForAnother(.1f)
            .OnCancel(FinishCancelled)
            .OnComplete(operation =>
            {
                bool duplicate = Duplicate(action);
                if (duplicate)
                {
                    if (previous == null) action.RemoveBindingOverride(0);
                    else action.ApplyBindingOverride(0, previous);
                }
                Finish(operation);
                if (!duplicate) GameRefs.Service<IPlayerKeybinds>()?.SaveKeybindings();
                Refresh();
                if (duplicate && row != null && row.Key != null) row.Key.text = "Key in use";
            });
        _operation.Start();
    }

    private static bool Duplicate(InputAction action)
    {
        string path = action.bindings[0].effectivePath;
        if (string.IsNullOrEmpty(path)) return false;
        foreach (string name in Actions)
        {
            var reference = CablesManagementControls.Reference(name);
            InputAction other = reference == null ? null : reference.action;
            if (other != null && other != action && other.bindings.Count > 0
                && (string.Equals(path, other.bindings[0].effectivePath, StringComparison.OrdinalIgnoreCase)
                    || DefaultAlias(action.name, path, other.bindings[0].effectivePath)
                    || DefaultAlias(other.name, other.bindings[0].effectivePath, path))) return true;
        }
        return false;
    }

    private static bool DefaultAlias(string action, string path, string other)
    {
        string modifier = action == "Align" ? "Alt" : action == "PaintRoute" ? "Shift" : null;
        return modifier != null && string.Equals(path, "<Keyboard>/left" + modifier, StringComparison.OrdinalIgnoreCase)
            && string.Equals(other, "<Keyboard>/right" + modifier, StringComparison.OrdinalIgnoreCase);
    }

    private static void FinishCancelled(InputActionRebindingExtensions.RebindingOperation operation)
    {
        Finish(operation);
        Refresh();
    }

    private static void Finish(InputActionRebindingExtensions.RebindingOperation operation)
    {
        if (_operation != operation) return;
        InputAction action = _currentAction;
        bool enable = _wasEnabled;
        _operation = null; _currentAction = null; _currentRow = null;
        _finishedFrame = Time.frameCount;
        operation.Dispose();
        if (enable && action != null) action.Enable();
    }

    private static void CancelCurrent()
    {
        var operation = _operation;
        if (operation == null) return;
        operation.Cancel();
        if (_operation == operation) Finish(operation);
    }

    internal static void Cancel(CablesManagementControlRow row)
    {
        if (_currentRow == row) CancelCurrent();
    }

    private static void Refresh(CablesManagementControlRow row)
    {
        if (row == null || row.Key == null || row.Control.actionRef == null || row.Control.actionRef.action == null) return;
        row.Key.text = row.Control.actionRef.action.GetBindingDisplayString(0, InputBinding.DisplayStringOptions.DontIncludeInteractions);
    }

    public static void Refresh()
    {
        foreach (GameObject section in Sections.Values)
            if (section != null)
                foreach (CablesManagementControlRow row in section.GetComponentsInChildren<CablesManagementControlRow>(true))
                    if (row != _currentRow) Refresh(row);
    }

    internal static void Forget(Transform host)
    {
        if (!ReferenceEquals(host, null)) Sections.Remove(host);
    }

    public static void Remove()
    {
        CancelCurrent();
        var sections = new List<GameObject>(Sections.Values);
        Sections.Clear();
        foreach (GameObject section in sections) if (section != null) UnityEngine.Object.Destroy(section);
    }
}
