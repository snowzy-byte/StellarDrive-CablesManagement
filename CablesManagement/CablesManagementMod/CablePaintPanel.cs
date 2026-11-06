using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UI.ToolBar;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CablesManagementMod;

public static class CablePaintPanel
{
    private static GameObject _root, _palette, _custom, _hud;
    private static RectTransform _panel;
    private static CanvasScaler _scaler;
    private static TMP_FontAsset _font;
    private static TMP_Text _hudText;
    private static Image _currentColor, _hudColor;
    private static Image[] _borders;
    private static Slider[] _sliders;
    private static TMP_Text[] _values;
    private static TMP_InputField _hex;
    private static Button _apply, _customButton, _originalButton;
    private static bool _customOpen, _syncing;
    private static readonly Color Ink = new Color(.94f,.96f,.97f);
    private static readonly Color Muted = new Color(.64f,.69f,.72f);
    private static readonly Color ButtonColor = new Color(.16f,.19f,.21f);
    private static readonly uint[] Colors = {
        0xEF5350,0xFF8C32,0xFFD54F,0x78CE55,0x26A69A,0x35C7DF,0x268BEB,0x5B64DE,
        0xA16AE8,0xE66FB0,0xA87A50,0xF5E9CC,0xFFFFFF,0xB0BEC5,0x53616D,0x20252A
    };

    public static void Prepare()
    {
        if(_root!=null) return;
        var native=Resources.FindObjectsOfTypeAll<ToolKeyTipUi>().FirstOrDefault(t=>t!=null && t.isActiveAndEnabled)
            ?? Resources.FindObjectsOfTypeAll<ToolKeyTipUi>().FirstOrDefault(t=>t!=null);
        TMP_Text sample=native!=null ? Traverse.Create(native).Field("label").GetValue<TMP_Text>() : null;
        _font=sample!=null && sample.font!=null ? sample.font : TMP_Settings.defaultFontAsset;
        _root=new GameObject("CablePaintUI",typeof(RectTransform)) { hideFlags=HideFlags.HideAndDontSave, layer=5 };
        _root.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas=_root.AddComponent<Canvas>(); canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=1000;
        _scaler=_root.AddComponent<CanvasScaler>();
        _scaler.uiScaleMode=CanvasScaler.ScaleMode.ConstantPixelSize;
        _scaler.referencePixelsPerUnit=100f;
        UpdateScale();
        _root.AddComponent<GraphicRaycaster>();
        _palette=Stretch("Palette",_root.transform).gameObject;

        var blocker=_palette.AddComponent<Image>(); blocker.color=Color.clear; blocker.raycastTarget=true;
        _panel=Rect("ColorPanel",_palette.transform,0,0,CablePaintPanelScale.Width,CablePaintPanelScale.CollapsedHeight);
        _panel.anchorMin=_panel.anchorMax=new Vector2(.5f,.5f); _panel.pivot=new Vector2(.5f,.5f);
        _panel.anchoredPosition=Vector2.zero;
        Fill(_panel,new Color(.48f,.55f,.58f,.45f));
        var inside=Stretch("Background",_panel,1); Fill(inside,new Color(.075f,.095f,.11f,.98f));
        Text("Title",_panel,"Cable color",18,12,250,28,20,Ink);
        _currentColor=Fill(Rect("CurrentColor",_panel,298,17,16,16),Color.white);
        Button(_panel,"Close","X",334,10,28,30,CablePaintMode.Exit,false);
        Fill(Rect("Divider",_panel,18,45,340,1),new Color(1,1,1,.1f));

        _borders=new Image[Colors.Length];
        for(int i=0;i<Colors.Length;i++)
        {
            uint color=Colors[i]; float x=23+(i%8)*42, y=56+(i/8)*42;
            _borders[i]=Fill(Rect("ColorBorder",_panel,x-2,y-2,40,40),new Color(1,1,1,.1f));
            var rect=Rect("Color_"+CablePaintStore.Hex(color),_panel,x,y,36,36);
            var image=Fill(rect,CablePaintAppearance.ColorOf(color)); image.raycastTarget=true;
            var button=rect.gameObject.AddComponent<Button>(); button.targetGraphic=image; Configure(button);
            button.onClick.AddListener(()=>CablePaintMode.ChooseColor(color,true));
        }
        _customButton=Button(_panel,"Custom","Custom...",23,150,150,28,ToggleCustom,true);
        _originalButton=Button(_panel,"Original","Original",203,150,150,28,CablePaintMode.ChooseOriginal,true);
        _custom=Rect("CustomColor",_panel,0,194,376,148).gameObject;
        Fill(Rect("Divider",_custom.transform,18,0,340,1),new Color(1,1,1,.1f));
        Text("HexLabel",_custom.transform,"#",23,9,18,28,16,Muted);
        MakeHex();
        _apply=Button(_custom.transform,"Apply","Apply",226,8,127,30,ApplyCustom,true);
        _sliders=new Slider[3]; _values=new TMP_Text[3];
        for(int i=0;i<3;i++)
        {
            float y=51+i*34;
            Text("Channel",_custom.transform,new[]{"R","G","B"}[i],23,y,25,22,15,Muted);
            _values[i]=Text("Value",_custom.transform,"0",312,y,41,22,14,Ink);
            _values[i].alignment=TextAlignmentOptions.MidlineRight;
            var track=Rect("Slider",_custom.transform,55,y,245,22);
            Fill(track,Color.clear).raycastTarget=true;
            Fill(Rect("Track",track,0,9,245,4),new Color(.22f,.26f,.28f));
            var fillArea=Rect("FillArea",track,0,9,245,4);
            var fill=Stretch("Fill",fillArea); Fill(fill,new Color(.58f,.64f,.67f));
            var handleArea=Stretch("HandleArea",track);
            var handle=Rect("Handle",handleArea,0,0,8,16);
            handle.anchorMin=handle.anchorMax=new Vector2(0,.5f); handle.pivot=new Vector2(.5f,.5f); handle.anchoredPosition=Vector2.zero;
            var handleImage=Fill(handle,Ink); handleImage.raycastTarget=true;
            var slider=track.gameObject.AddComponent<Slider>(); slider.fillRect=fill; slider.handleRect=handle;
            slider.targetGraphic=handleImage; slider.direction=Slider.Direction.LeftToRight;
            slider.minValue=0; slider.maxValue=255; slider.wholeNumbers=true; Configure(slider);
            _sliders[i]=slider; slider.onValueChanged.AddListener(_=>ChangedRgb());
        }
        _hud=Rect("PaintColor",_root.transform,0,0,170,28).gameObject;
        var hudRect=(RectTransform)_hud.transform;
        hudRect.anchorMin=hudRect.anchorMax=new Vector2(.5f,0); hudRect.pivot=new Vector2(.5f,0); hudRect.anchoredPosition=new Vector2(0,76);
        Fill(hudRect,new Color(.075f,.095f,.11f,.8f));
        _hudColor=Fill(Rect("Color",hudRect,8,6,16,16),Color.white);
        _hudText=Text("Value",hudRect,"",34,2,130,24,14,Ink);
        SetCustom(false);
        Refresh();
    }

    public static void Open()
    {
        Prepare(); SetCustom(false); Refresh(); Tick();
    }

    public static void Tick()
    {
        if(_root==null) return;
        UpdateScale();
        _root.SetActive(CablePaintMode.Active);
        _palette.SetActive(CablePaintMode.PaletteOpen);
        _hud.SetActive(CablePaintMode.Active && !CablePaintMode.PaletteOpen);
    }

    public static void Refresh()
    {
        if(_root==null || _hex==null || _sliders==null) return;
        _syncing=true;
        try
        {
            uint rgb=CablePaintMode.SelectedColor;
            bool original=CablePaintMode.OriginalSelected;
            Color color=CablePaintAppearance.ColorOf(rgb);
            _currentColor.color=_hudColor.color=original?new Color(.6f,.63f,.65f):color;
            _hudText.text=original?"Original":"Color  #"+CablePaintStore.Hex(rgb);
            for(int i=0;i<Colors.Length;i++)
                _borders[i].color=!original && Colors[i]==rgb ? Ink : new Color(1,1,1,.1f);
            _hex.SetTextWithoutNotify(CablePaintStore.Hex(rgb));
            int[] channels={(int)(rgb>>16)&255,(int)(rgb>>8)&255,(int)rgb&255};
            for(int i=0;i<3;i++) { _sliders[i].SetValueWithoutNotify(channels[i]); _values[i].text=channels[i].ToString(); }
            _originalButton.image.color=original?new Color(.29f,.34f,.37f):ButtonColor;
            _apply.interactable=true;
        }
        finally { _syncing=false; }
    }

    private static void ToggleCustom() => SetCustom(!_customOpen);
    private static void SetCustom(bool open)
    {
        _customOpen=open; _custom.SetActive(open);
        _panel.sizeDelta=new Vector2(CablePaintPanelScale.Width,
            open?CablePaintPanelScale.ExpandedHeight:CablePaintPanelScale.CollapsedHeight);
        UpdateScale();
        _customButton.image.color=open?new Color(.29f,.34f,.37f):ButtonColor;
    }

    private static void UpdateScale()
    {
        if(_scaler==null) return;
        float scale=CablePaintPanelScale.ForScreen(Screen.width,Screen.height,_customOpen);
        if(Mathf.Abs(_scaler.scaleFactor-scale)>.0001f) _scaler.scaleFactor=scale;
    }

    private static void ChangedRgb()
    {
        if(_syncing) return;
        uint rgb=(uint)((int)_sliders[0].value<<16 | (int)_sliders[1].value<<8 | (int)_sliders[2].value);
        CablePaintMode.ChooseColor(rgb,false);
    }
    private static void ApplyCustom()
    {
        if(CablePaintStore.TryHex(_hex.text,out uint rgb)) CablePaintMode.ChooseColor(rgb,true);
    }

    private static void MakeHex()
    {
        var root=Rect("Hex",_custom.transform,43,9,157,28);
        var background=Fill(root,new Color(.035f,.05f,.06f)); background.raycastTarget=true;
        var viewport=Stretch("Viewport",root); viewport.offsetMin=new Vector2(8,2); viewport.offsetMax=new Vector2(-8,-2);
        var text=Text("Text",viewport,"",0,0,141,24,16,Ink);
        text.textWrappingMode=TextWrappingModes.NoWrap;
        _hex=root.gameObject.AddComponent<TMP_InputField>();
        _hex.targetGraphic=background; _hex.textViewport=viewport; _hex.textComponent=(TextMeshProUGUI)text;
        _hex.lineType=TMP_InputField.LineType.SingleLine; _hex.characterLimit=6;
        _hex.onValidateInput=(value,index,character)=>Uri.IsHexDigit(character)?char.ToUpperInvariant(character):'\0';
        _hex.onValueChanged.AddListener(value=>
        {
            if(_syncing) return;
            bool valid=CablePaintStore.TryHex(value,out uint rgb);
            _apply.interactable=valid;
            if(valid) CablePaintMode.ChooseColor(rgb,false);
        });
        Configure(_hex);
    }

    internal static RectTransform Rect(string name,Transform parent,float x,float y,float width,float height)
    {
        var obj=new GameObject(name,typeof(RectTransform)) { layer=5 };
        var rect=(RectTransform)obj.transform; rect.SetParent(parent,false);
        rect.anchorMin=rect.anchorMax=new Vector2(0,1); rect.pivot=new Vector2(0,1);
        rect.anchoredPosition=new Vector2(x,-y); rect.sizeDelta=new Vector2(width,height);
        return rect;
    }
    private static RectTransform Stretch(string name,Transform parent,float inset=0)
    {
        var rect=Rect(name,parent,0,0,0,0); rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one;
        rect.offsetMin=new Vector2(inset,inset); rect.offsetMax=new Vector2(-inset,-inset); return rect;
    }
    internal static Image Fill(RectTransform rect,Color color)
    {
        var image=rect.gameObject.AddComponent<Image>(); image.color=color; image.raycastTarget=false; return image;
    }
    internal static TMP_Text Text(string name,Transform parent,string value,float x,float y,float width,float height,float size,Color color)
    {
        var text=Rect(name,parent,x,y,width,height).gameObject.AddComponent<TextMeshProUGUI>();
        text.font=_font; text.text=value; text.fontSize=size; text.color=color;
        text.extraPadding=true;
        text.alignment=TextAlignmentOptions.MidlineLeft; text.textWrappingMode=TextWrappingModes.NoWrap;
        text.overflowMode=TextOverflowModes.Truncate; text.raycastTarget=false; return text;
    }
    internal static Button Button(Transform parent,string name,string label,float x,float y,float width,float height,UnityAction action,bool filled)
    {
        var rect=Rect(name,parent,x,y,width,height);
        var image=Fill(rect,filled?ButtonColor:new Color(1,1,1,.02f)); image.raycastTarget=true;
        var button=rect.gameObject.AddComponent<Button>(); button.targetGraphic=image; Configure(button);
        var text=Text("Label",rect,label,0,0,width,height,16,Ink); text.alignment=TextAlignmentOptions.Center;
        button.onClick.AddListener(action); return button;
    }
    internal static void Configure(Selectable control)
    {
        var colors=ColorBlock.defaultColorBlock;
        colors.normalColor=Color.white; colors.highlightedColor=new Color(1.18f,1.18f,1.18f);
        colors.pressedColor=new Color(.75f,.75f,.75f); colors.selectedColor=Color.white;
        colors.disabledColor=new Color(.5f,.5f,.5f,.5f); colors.fadeDuration=.08f;
        control.colors=colors; control.navigation=new Navigation { mode=Navigation.Mode.None };
    }

    public static void Remove()
    {
        if(_root!=null) UnityEngine.Object.Destroy(_root);
        _root=null; _palette=null; _custom=null; _hud=null; _font=null;
        _panel=null; _scaler=null; _hex=null; _sliders=null; _values=null; _borders=null; _apply=null;
    }
}
