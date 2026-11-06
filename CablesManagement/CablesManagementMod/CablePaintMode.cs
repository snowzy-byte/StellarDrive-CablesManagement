using System.Linq;
using HarmonyLib;
using Players.Interface.Model;
using Players.Interface.Services;
using Players.LocalClient.Controls;
using Players.Services;
using Players.Inventory;
using SDModKit.Game;
using Ships.Cables;
using Ships.Interface.Model.Parts.Common;
using UI.Common;
using UI.Interface.Model;
using UI.Interface.Services;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Reflection;
using UI.ToolBar;
using System;
using Object = UnityEngine.Object;

namespace CablesManagementMod;

public sealed class CablePaintMenu : MonoBehaviour, IClosableMenu
{
    private MenuTracker _tracker;
    public bool IsOpen { get; private set; }
    public bool Open()
    {
        _tracker = GameRefs.Service<MenuTracker>();
        if (_tracker == null) return false;
        IsOpen = true;
        _tracker.TrackMenu(this);
        return true;
    }
    public void Dismiss()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (_tracker != null) _tracker.UntrackMenu(this);
    }
    public void Close() => CablePaintMode.Exit();
    private void OnDestroy() => Dismiss();
}

public static class CablePaintMode
{
    public static bool Active { get; private set; }
    public static bool PaletteOpen => _menu != null && _menu.IsOpen;
    private static CablePaintMenu _menu;
    private static uint _selected = 0x268BEB;
    private static bool _restore;
    private static uint _hovered, _lastPainted;
    private static float _lastPaintTime;
    private static bool _heldClick, _waitForRelease;
    private static int _exitFrame = -1;
    internal static uint SelectedColor => _selected;
    internal static bool OriginalSelected => _restore;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PlayerPrimaryActionControl), nameof(PlayerPrimaryActionControl.OnPrimaryAction)),
            prefix: new HarmonyMethod(typeof(CablePaintMode), nameof(BeforePrimary)));
        harmony.Patch(AccessTools.Method(typeof(PlayerSecondaryActionControl), nameof(PlayerSecondaryActionControl.OnSecondaryAction)),
            prefix: new HarmonyMethod(typeof(CablePaintMode), nameof(BeforeOtherAction)));
        harmony.Patch(AccessTools.Method(typeof(PlayerTertiaryControl), nameof(PlayerTertiaryControl.OnTertiaryAction)),
            prefix: new HarmonyMethod(typeof(CablePaintMode), nameof(BeforeOtherAction)));
        harmony.Patch(AccessTools.Method(typeof(PlayerEscapeControl), nameof(PlayerEscapeControl.OnEscape)),
            prefix: new HarmonyMethod(typeof(CablePaintMode), nameof(BeforeEscape)));
        harmony.Patch(AccessTools.Method(typeof(PlayerLookControls), nameof(PlayerLookControls.OnLook)),
            prefix: new HarmonyMethod(typeof(CablePaintMode), nameof(BeforeLook)));
        harmony.Patch(AccessTools.Method(typeof(ToolBelt), nameof(ToolBelt.SelectTool)),
            prefix: new HarmonyMethod(typeof(CablePaintMode), nameof(BeforeSelectTool)));
    }

    private static bool BeforePrimary(InputAction.CallbackContext context)
    {
        if (Active && !PaletteOpen && context.performed && CablesManagementNativeMovement.HoveringClip())
        {
            Exit();
            return true;
        }
        return !Active || context.canceled;
    }
    private static bool BeforeOtherAction() => !Active;
    private static bool BeforeLook() => !PaletteOpen;
    private static void BeforeSelectTool() { if (Active) Exit(); }
    private static bool BeforeEscape(InputAction.CallbackContext context)
    {
        if (!Active && _exitFrame == Time.frameCount) return false;
        if (!Active || !context.performed) return true;
        Exit();
        return false;
    }

    private static bool PlayerReady()
    {
        var players = GameRefs.Service<PlayersClientTracker>();
        return players != null && players.TryGetLocalPlayerState(out var state) &&
            (state.Type == PlayerStateType.Walking || state.Type == PlayerStateType.CheatFlying);
    }

    public static void Update()
    {
        CablePaintNetwork.Update();
        if (!PlayerReady()) { if (Active) Exit(); return; }
        Keyboard keyboard = Keyboard.current;
        if (!Application.isFocused || keyboard == null) return;
        if (keyboard.escapeKey.wasPressedThisFrame && Active) { Exit(); return; }
        var menus = GameRefs.Service<IClosableMenuService>();
        if (CablesManagementControls.Pressed("Palette"))
        {
            if (PaletteOpen) ClosePalette();
            else if (menus != null && !CablesManagementControls.MenuOpen) OpenPalette();
        }
        if (!Active || PaletteOpen) return;
        if (CablesManagementControls.MenuOpen) { Exit(); return; }
        var hover = GameRefs.Service<IPlayerCableHoverProvider>();
        var cables = GameRefs.Service<CablesClientTracker>();
        _hovered = hover != null && hover.HasHoveredCable && cables != null &&
            cables.TryGetCableState(hover.HoveredCableId, out CableState state) && !state.PlugA.IsHeldByPlayer && !state.PlugB.IsHeldByPlayer
            ? hover.HoveredCableId : 0;
        Mouse mouse = Mouse.current;
        if (mouse == null) return;
        if (mouse.rightButton.wasPressedThisFrame) { OpenPalette(); return; }
        if (mouse.middleButton.wasPressedThisFrame && _hovered != 0)
        {
            if (CablePaintStore.TryGet(_hovered, out uint rgb)) Select(rgb);
            else { _restore = true; CablePaintPanel.Refresh(); }
        }
        bool held = mouse.leftButton.isPressed;
        if (_waitForRelease)
        {
            if (!held) { _waitForRelease=false; _heldClick=false; }
            return;
        }
        if (!held) _lastPainted = 0;
        if (held && _hovered != 0 && (!_heldClick || _hovered != _lastPainted) && Time.unscaledTime - _lastPaintTime >= .12f)
        {
            bool route = CablesManagementControls.Held("PaintRoute");
            uint? color = _restore ? (uint?)null : _selected;
            if (route ? CableRoutes.PaintRoute(_hovered, color) : CablePaintNetwork.Send(_hovered, color)) _lastPainted = _hovered;
            _lastPaintTime = Time.unscaledTime;
        }
        _heldClick = held;
    }

    private static void OpenPalette()
    {
        CablePaintPanel.Prepare();
        if (_menu == null)
        {
            var root = new GameObject("CablePaintPalette") { hideFlags=HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(root);
            _menu = root.AddComponent<CablePaintMenu>();
        }

        GameRefs.Service<LocalSelectedToolTracker>()?.SelectedTool?.PrimaryActionEnd();

        var controls=Resources.FindObjectsOfTypeAll<PlayerPrimaryActionControl>();
        var control=controls.FirstOrDefault(c=>c!=null && c.isActiveAndEnabled);
        if(control!=null) Traverse.Create(control).Field("toolBelt").GetValue<ToolBelt>()?.ClearSelection();
        Active = true;
        if (!_menu.Open()) { Active=false; CablePaintPanel.Tick(); return; }
        _heldClick = true;
        _waitForRelease = true;
        _lastPainted = 0;
        CablePaintPanel.Open();
        CablePaintKeyTips.Refresh();
    }

    private static void ClosePalette()
    {
        _menu?.Dismiss();
        _heldClick = true;
        _waitForRelease = true;

        _lastPaintTime = Time.unscaledTime;
        CablePaintPanel.Tick();
        CablePaintKeyTips.Refresh();
    }

    public static void Exit()
    {
        Active=false; _hovered=0; _lastPainted=0; _heldClick=false;
        _waitForRelease=false; _exitFrame=Time.frameCount;
        _menu?.Dismiss();
        CablePaintPanel.Tick();
        CablePaintKeyTips.Refresh();
    }

    private static void Select(uint rgb)
    {
        _selected=rgb; _restore=false;
        _lastPainted=0;
        CablePaintPanel.Refresh();
    }

    internal static void ChooseColor(uint rgb,bool close)
    {
        Select(rgb);
        if(close) ClosePalette();
    }
    internal static void ChooseOriginal()
    {
        _restore=true;
        CablePaintPanel.Refresh();
        ClosePalette();
    }

    public static void Remove()
    {
        Exit();
        if(_menu!=null) Object.Destroy(_menu.gameObject);
        CablePaintPanel.Remove();
        _menu=null;
    }
}

public static class CablePaintKeyTips
{
    private static readonly MethodInfo UpdateTips=AccessTools.Method(typeof(ToolKeyTipsUpdater),"UpdateKeyTips");
    private static InputActionAsset _asset;
    private static ToolKeyTip[] _tips;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ToolKeyTipsUpdater),"GenerateTips"),
            prefix:new HarmonyMethod(typeof(CablePaintKeyTips),nameof(BeforeGenerate)));
    }
    private static void BeforeGenerate(ref IReadOnlyCollection<ToolKeyTip> toolKeyTips)
    {
        if(!CablePaintMode.Active || CablePaintMode.PaletteOpen) return;
        if(_tips==null)
        {
            _asset=ScriptableObject.CreateInstance<InputActionAsset>(); _asset.name="CablePaintKeyTips"; _asset.hideFlags=HideFlags.HideAndDontSave;
            var map=new InputActionMap("CablePaint"); _asset.AddActionMap(map);
            _tips=new[]{Tip(map,"Paint","<Mouse>/leftButton"),Tip(map,"Pick color","<Mouse>/middleButton"),
                new ToolKeyTip { action=CablesManagementControls.Reference("PaintRoute"),actionDesc="Paint route + LMB" },
                new ToolKeyTip { action=CablesManagementControls.Reference("Trace"),actionDesc="Trace" },
                new ToolKeyTip { action=CablesManagementControls.Reference("Palette"),actionDesc="Colors" },Tip(map,"Exit","<Keyboard>/escape")};
        }
        toolKeyTips=_tips;
    }
    private static ToolKeyTip Tip(InputActionMap map,string name,string binding)
    {
        var action=map.AddAction(name,InputActionType.Button,binding);
        var reference=InputActionReference.Create(action); reference.hideFlags=HideFlags.HideAndDontSave;
        return new ToolKeyTip{ action=reference,actionDesc=name };
    }
    public static void Refresh()
    {
        foreach(var hud in Resources.FindObjectsOfTypeAll<ToolKeyTipsUpdater>())
            if(hud!=null && hud.isActiveAndEnabled) UpdateTips.Invoke(hud,null);
    }
    public static void Remove()
    {
        if(_tips!=null) foreach(var tip in _tips)
            if(tip.action!=null && tip.action.action?.actionMap?.asset==_asset) Object.Destroy(tip.action);
        if(_asset!=null) Object.Destroy(_asset);
        _tips=null; _asset=null;
    }
}

public static class CablePaintMath
{
    public static float DistanceSquared(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 delta = b - a;
        float length = delta.sqrMagnitude;
        if (length < .000001f) return (point - a).sqrMagnitude;
        float t = Vector3.Dot(point - a, delta) / length;
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        return (point - (a + delta * t)).sqrMagnitude;
    }
}

public static class CablePaintGlow
{

    public static uint LighterColor(uint rgb)
    {
        return Lift((rgb>>16)&255)<<16 | Lift((rgb>>8)&255)<<8 | Lift(rgb&255);
    }

    private static uint Lift(uint channel)
    {
        uint gap=255-channel;
        return channel+(gap==0 ? 0 : Math.Max(1u,(gap+2)/5));
    }
}

public static class CablePaintPanelScale
{
    public const float Width = 376f;
    public const float CollapsedHeight = 196f;
    public const float ExpandedHeight = 354f;

    public static float ForScreen(int width, int height, bool expanded)
    {
        if (width <= 0 || height <= 0) return 1f;
        float panelHeight = expanded ? ExpandedHeight : CollapsedHeight;
        float requested = Math.Max(1f, height / 720f);
        float fitWidth = Math.Max(1f, width - 32f) / Width;
        float fitHeight = Math.Max(1f, height - 32f) / panelHeight;
        return Math.Min(requested, Math.Min(fitWidth, fitHeight));
    }
}
