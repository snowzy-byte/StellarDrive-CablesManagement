using System;
using System.Collections.Generic;
using System.Linq;
using Core.Utils;
using HarmonyLib;
using Managers.Interface.Model;
using Managers.Interface.Services;
using Players.Interface.Model;
using Players.LocalClient.Controls;
using Players.LocalClient.Interaction;
using Players.Services;
using Research.Interface.Services;

using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Interface.Services;

using Ships.Services;
using TMPro;
using UI.ToolBar;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WorldTracking.Interface.Values;
using Object = UnityEngine.Object;

namespace CablesManagementMod;

internal static class CablesManagementBulkhead
{
    internal const float Depth = .084f;
    internal static Bounds Bounds => new Bounds(new Vector3(0f, 0f, -.01f), new Vector3(.14f, .14f, .02f));
    internal static Vector3 Port(byte id) => new Vector3(0f, 0f, id == 0 ? -.04f : Depth + .04f);

    internal static GameObject Create(Material donor) => CablesManagementModel.CreatePieces(Build(), donor, "CablesManagementBulkhead");

    internal static List<CablesManagementObj.Piece> Build()
    {
        var flange = new CablesManagementObj.Piece("Bulkhead_Flanges", "Industrial_LightGrey");
        var sleeve = new CablesManagementObj.Piece("Bulkhead_Sleeve", "DarkSlate_Plastic");
        var bolts = new CablesManagementObj.Piece("Bulkhead_Bolts", "Bolt_LightMetal");
        Ring(flange, Vector2.zero, .026f, .068f, -.018f, 0f, 24);
        Ring(flange, Vector2.zero, .026f, .068f, Depth, Depth + .018f, 24);
        Ring(sleeve, Vector2.zero, .018f, .027f, -.029f, Depth + .029f, 20);
        foreach (int x in new[] { -1, 1 })
        foreach (int y in new[] { -1, 1 })
        {
            Ring(bolts, new Vector2(x * .043f, y * .043f), .0015f, .005f, -.022f, -.017f, 6);
            Ring(bolts, new Vector2(x * .043f, y * .043f), .0015f, .005f, Depth + .017f, Depth + .022f, 6);
        }
        return new List<CablesManagementObj.Piece> { flange, sleeve, bolts };
    }

    private static void Ring(CablesManagementObj.Piece mesh, Vector2 center, float inner, float outer, float a, float b, int sides)
    {
        Vector3 Point(float r, float angle, float z) => new Vector3(center.x + Mathf.Cos(angle) * r, center.y + Mathf.Sin(angle) * r, z);
        for (int i = 0; i < sides; i++)
        {
            float u = i * Mathf.PI * 2f / sides, v = (i + 1) * Mathf.PI * 2f / sides;
            Quad(mesh, Point(inner,u,a), Point(inner,v,a), Point(outer,v,a), Point(outer,u,a), Vector3.back);
            Quad(mesh, Point(inner,u,b), Point(outer,u,b), Point(outer,v,b), Point(inner,v,b), Vector3.forward);
            Vector3 normal = new Vector3(Mathf.Cos((u+v)*.5f),Mathf.Sin((u+v)*.5f),0f);
            Quad(mesh, Point(outer,u,a), Point(outer,v,a), Point(outer,v,b), Point(outer,u,b), normal);
            Quad(mesh, Point(inner,u,a), Point(inner,u,b), Point(inner,v,b), Point(inner,v,a), -normal);
        }
    }

    private static void Quad(CablesManagementObj.Piece mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        if (Vector3.Dot(Vector3.Cross(b-a,c-a),normal)<0f) { Vector3 swap=b;b=d;d=swap; }
        int index=mesh.Vertices.Count;
        mesh.Vertices.AddRange(new[]{a,b,c,d});
        for(int i=0;i<4;i++)mesh.Normals.Add(normal);
        mesh.Triangles.AddRange(new[]{index,index+1,index+2,index,index+2,index+3});
    }

    internal static void Badges(Transform parent, Material material, CableType type)
    {
        foreach(bool rear in new[]{false,true})
        {
            var frame=new GameObject(rear?"Rear identification":"Front identification");
            frame.transform.SetParent(parent,false);
            frame.transform.localPosition=new Vector3(0f,0f,rear?Depth+.008f:-.008f);
            frame.transform.localScale=Vector3.one*.72f;
            frame.transform.localRotation=rear?Quaternion.Euler(0f,180f,0f):Quaternion.identity;
            CablesManagementBadge.Create(frame.transform,material,type);
        }
    }

    internal static void Pickup(GameObject target)
    {
        foreach(float z in new[]{-.01f,Depth+.01f})
        foreach(int side in new[]{-1,1})
        {
            var vertical=target.AddComponent<BoxCollider>();vertical.center=new Vector3(side*.052f,0f,z);vertical.size=new Vector3(.025f,.12f,.02f);
            var horizontal=target.AddComponent<BoxCollider>();horizontal.center=new Vector3(0f,side*.052f,z);horizontal.size=new Vector3(.08f,.025f,.02f);
        }
    }
}

internal static class CablesManagementRouteBuilder
{
    internal struct Anchor
    {
        internal Vector3 Position, Normal;
        internal Anchor(Vector3 position, Vector3 normal) { Position=position;Normal=normal; }
    }
    private enum Stage { Off, Planning, Place, WaitPlace, Plug, WaitPlug, StartCable, WaitCable, StyleColor, StyleName, StyleCurve, Finish, WaitFinish }
    private sealed class PreviewGhost
    {
        internal GameObject Root;
        internal MeshRenderer[] Renderers;
        internal bool? Valid;
    }
    private static Stage _stage;
    private static readonly List<Anchor> Anchors=new List<Anchor>();
    private static readonly List<Anchor> Last=new List<Anchor>();
    private static readonly List<FloatingPartPlacement> Plan=new List<FloatingPartPlacement>();
    private static readonly List<bool> ValidSlots=new List<bool>();
    private static readonly List<LineRenderer> Lines=new List<LineRenderer>();
    private static readonly List<PreviewGhost> Ghosts=new List<PreviewGhost>();
    private static readonly RaycastHit[] Hits=new RaycastHit[100];
    private static CameraPhysicsData _camera;
    private static float _cameraAt=-100f, _deadline, _messageUntil;
    private static float _styleAt;
    private static IShipPartPlacements _grid;
    private static IShipPartPlacements _lastGrid;
    private static Vector3 _sourceLocal;
    private static uint _ship, _lastShip, _cable;
    private static ushort _settings;
    private static ushort? _placed;
    private static int _index, _rendered;
    private static int _requiredIron;
    private static EntityPartSocket _target;
    private static CableType _type;
    private static string _message="", _name;
    private static uint? _color;
    private static bool _curved;
    private static bool _confirmHeld;
    private static readonly List<CablesManagementAlignmentMath.Guide> AlignmentGuides=new List<CablesManagementAlignmentMath.Guide>();
    internal static bool Active=>_stage!=Stage.Off;
    private static bool FreeBuild=>GameRefs.Service<PlayersClientTracker>()?.IsLocalPlayerUsingFreeBuildCheat()==true;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PlayerObjectInteractionDetector),nameof(PlayerObjectInteractionDetector.UpdateDetection)),
            prefix:new HarmonyMethod(typeof(CablesManagementRouteBuilder),nameof(Camera)));
        harmony.Patch(AccessTools.Method(typeof(PlayerPlugSocketDetector),nameof(PlayerPlugSocketDetector.UpdateDetection)),
            prefix:new HarmonyMethod(typeof(CablesManagementRouteBuilder),nameof(Camera)));
        harmony.Patch(AccessTools.Method(typeof(PlayerPrimaryActionControl),nameof(PlayerPrimaryActionControl.OnPrimaryAction)),
            prefix:new HarmonyMethod(typeof(CablesManagementRouteBuilder),nameof(Primary)) { priority=Priority.First });
        harmony.Patch(AccessTools.Method(typeof(PlayerSecondaryActionControl),nameof(PlayerSecondaryActionControl.OnSecondaryAction)),
            prefix:new HarmonyMethod(typeof(CablesManagementRouteBuilder),nameof(Other)) { priority=Priority.First });
        harmony.Patch(AccessTools.Method(typeof(PlayerTertiaryControl),nameof(PlayerTertiaryControl.OnTertiaryAction)),
            prefix:new HarmonyMethod(typeof(CablesManagementRouteBuilder),nameof(Other)) { priority=Priority.First });
        harmony.Patch(AccessTools.Method(typeof(PlayerEscapeControl),nameof(PlayerEscapeControl.OnEscape)),
            prefix:new HarmonyMethod(typeof(CablesManagementRouteBuilder),nameof(Escape)) { priority=Priority.First });
        harmony.Patch(AccessTools.Method(typeof(PlayerChatControl),nameof(PlayerChatControl.StartChat)),
            prefix:new HarmonyMethod(typeof(CablesManagementRouteBuilder),nameof(Chat)) { priority=Priority.First });
    }

    private static void Camera(CameraPhysicsData cameraPhysics) { _camera=cameraPhysics;_cameraAt=Time.unscaledTime; }
    private static bool Primary(InputAction.CallbackContext context)
    {
        if(!Active)return true;
        if(context.performed&&_stage==Stage.Planning&&!CablesManagementControls.MenuOpen)Click();
        return false;
    }
    private static bool Other()=>!Active;
    private static bool Chat(InputAction.CallbackContext context)
    {
        if(!Active&&!_confirmHeld)return true;
        var keyboard=Keyboard.current;
        bool enter=keyboard!=null&&(keyboard.enterKey.isPressed||keyboard.numpadEnterKey.isPressed);
        if(context.performed&&enter&&!_confirmHeld&&_stage==Stage.Planning&&!CablesManagementControls.MenuOpen)
        {_confirmHeld=true;Confirm();}
        return false;
    }
    private static void ReleaseConfirm(bool enterDown){if(!enterDown)_confirmHeld=false;}
    private static bool Escape(InputAction.CallbackContext context)
    {
        if(!Active)return true;
        if(CablesManagementControls.MenuOpen)return true;
        if(context.performed)Stop("Route cancelled");
        return false;
    }

    private static bool Player(out PlayerState state)
    {
        state=default;
        var players=GameRefs.Service<PlayersClientTracker>();
        return players!=null&&players.TryGetLocalPlayerState(out state);
    }
    private static bool Holding(PlayerState state)=>state.Type==PlayerStateType.WalkingHoldingCable||state.Type==PlayerStateType.CheatFlyingHoldingCable;
    private static bool Held(out CableState cable)
    {
        cable=default;
        return Player(out var state)&&Holding(state)&&GameRefs.Service<CablesClientTracker>()?.TryGetCableState(state.EntityId,out cable)==true;
    }

    internal static void Update()
    {
        var keyboard=Keyboard.current;
        ReleaseConfirm(keyboard!=null&&(keyboard.enterKey.isPressed||keyboard.numpadEnterKey.isPressed));
        if(_stage!=Stage.Planning)Hide();else{_rendered=0;_renderedGhosts=0;}
        if(!Player(out var state)) { if(Active)Stop("Route stopped");return; }
        if(!Application.isFocused){Hide();_deadline+=Time.unscaledDeltaTime;return;}
        if(CablesManagementControls.Pressed("PlanRoute")&&!CablesManagementControls.MenuOpen)
        {
            if(Active)Stop("Route cancelled");else Begin();
        }
        if(!Active)return;
        if(_grid==null||_grid is Component component&&component==null){Stop("Route stopped: ship unavailable");return;}
        if(!Holding(state)&&state.Type!=PlayerStateType.Walking&&state.Type!=PlayerStateType.CheatFlying){Stop("Route stopped: player state changed");return;}
        if(CablesManagementControls.MenuOpen){Hide();_deadline+=Time.unscaledDeltaTime;return;}
        if(_stage==Stage.Planning)
        {
            if(!Holding(state)||state.EntityId!=_cable){Stop("Route stopped: cable changed");return;}
            if(Mouse.current?.rightButton.wasPressedThisFrame==true)
            {
                if(_target.EntityId!=0)_target=default;
                else if(Anchors.Count>0)Anchors.RemoveAt(Anchors.Count-1);
                Refresh();
            }
            if(Mouse.current?.middleButton.wasPressedThisFrame==true)Reuse();
            if(!_confirmHeld&&keyboard!=null&&(keyboard.enterKey.wasPressedThisFrame||keyboard.numpadEnterKey.wasPressedThisFrame))
            {_confirmHeld=true;Confirm();}
            Preview();
            TrimPreview();
        }
        else Build(state);
    }

    private static void Begin()
    {
        if(!Held(out var cable)||!Player(out var state)){Notice("Hold a cable, then press "+CablesManagementControls.Label("PlanRoute"));return;}
        if(cable.Type!=CableType.Signal&&cable.Type!=CableType.Power&&cable.Type!=CableType.Fluid){Notice("This cable type is not supported by the route planner");return;}
        CablePlugSocket fixedPlug=cable.PlugA.IsHeldByPlayer?cable.PlugB:cable.PlugA;
        if(fixedPlug.IsHeldByPlayer){Notice("Connect one end of the cable first");return;}
        _ship=fixedPlug.EntityId;_type=cable.Type;_cable=state.EntityId;
        _settings=_type==CableType.Power?(ushort)8891:_type==CableType.Fluid?(ushort)8892:(ushort)8890;
        var research=GameRefs.Service<IResearchProvider>();
        var settings=CableParts.Get(_settings);
        if(settings==null||!FreeBuild&&(research==null||!research.IsPartUnlocked(settings))){Notice("Research Cables Management first");return;}
        _grid=FindGrid(_ship);
        if(_grid==null){Notice("The ship is not available");return;}
        if(!ReferenceEquals(_lastGrid,_grid))Last.Clear();
        var ships=GameRefs.Service<ShipsClientTracker>();
        if(ships==null||!ships.TryGetVisualSocketTransform(fixedPlug.EntityPartSocket,out var source)){Notice("The source socket is unavailable");return;}
        _sourceLocal=_grid.ToLocalPos(source.position);
        foreach(var ghost in Ghosts)if(ghost.Root!=null)Object.Destroy(ghost.Root);Ghosts.Clear();
        CablePaintMode.Exit();Anchors.Clear();Plan.Clear();ValidSlots.Clear();_requiredIron=0;_target=default;_stage=Stage.Planning;
        _color=CablePaintStore.TryGet(_cable,out uint rgb)?(uint?)rgb:null;
        _name=CableNameStore.Get(_cable);_curved=CableRoutingStore.IsCurved(_cable);
        _message="Choose the route points, then click the destination socket";
    }

    private static IShipPartPlacements FindGrid(uint id)
    {
        foreach(var proxy in Object.FindObjectsByType<Ships.Physics.Proxies.ShipPartsAvailabilityProxy>(FindObjectsSortMode.None))
            if(proxy.ShipId==id)return proxy;
        return null;
    }

    private static void Click()
    {
        if(Time.unscaledTime-_cameraAt>.25f)return;
        int count=ClientScene.PhysicsScene.Raycast(_camera.CameraPos,_camera.Direction,Hits,3f,LayerMask.GetMask("Default","Ship","Interactable","WorldObject"));
        Array.Sort(Hits,0,count,Comparer<RaycastHit>.Create((a,b)=>a.distance.CompareTo(b.distance)));
        foreach(var hit in Hits.Take(count))
        {
            var socket=hit.collider.GetComponent<IPartSocket>();
            if(socket!=null)
            {
                if(socket.ShipId!=_ship||socket.Type!=_type){_message="Choose a matching socket on the same ship";return;}
                var connections=GameRefs.Service<ConnectionsClientTracker>();
                if(connections==null||connections.IsSocketPhysicallyOccupied(socket.EntityPartSocket)){_message="That socket is already connected";return;}
                _target=socket.EntityPartSocket;Refresh();_message="Press Enter to build this route";return;
            }
            if(hit.collider.CompareTag("Interactable"))continue;
            if(!SurfacePoint(hit,out Anchor point,out _)){_message="Choose a flat surface on the same ship";return;}
            Vector3 position=point.Position,normal=point.Normal;
            if(Anchors.Count>0&&(Anchors[Anchors.Count-1].Position-position).sqrMagnitude<.01f){_message="Choose a point farther along the route";return;}
            if(Anchors.Count>=24){_message="Route limit: 24 marked points";return;}
            Anchors.Add(new Anchor(position,normal));_target=default;Refresh();_message="Add another point or click the destination socket";return;
        }
    }

    private static bool SurfacePoint(RaycastHit hit,out Anchor point,out CablesManagementAlignmentMath.Match? alignment)
    {
        point=default;alignment=null;
        var grid=hit.collider.GetComponentInParent<IShipPartPlacements>();
        if(grid==null||grid.ShipId!=_ship)return false;
        var local=grid.ConvertHitToLocalHit(hit);
        Vector3 normal=PartOrientationHelper.SnapAnyOrientation(local.Normal).GetVector3();
        if(Vector3.Dot(normal,local.Normal)<.98f)return false;
        Vector3 position=local.Pos;
        position=normal*Vector3.Dot(position,normal)+Vector3.ProjectOnPlane(new Vector3(Mathf.Round(position.x*20f)/20f,Mathf.Round(position.y*20f)/20f,Mathf.Round(position.z*20f)/20f),normal)+normal*.002f;
        if(CablesManagementControls.Held("Align"))
        {
            AlignmentGuides.Clear();
            var ships=GameRefs.Service<ShipsClientTracker>();
            if(ships!=null&&ships.TryGetShip(_ship,out IShipStateRead ship))
                foreach(var part in ship.StatefulParts)
                    if(CablesManagementMod.IsClip(part.Settings.id)&&!CablesManagementMod.IsWallPass(part.Settings.id)
                        &&!(part.State is IPickablePartState held&&held.IsHeldByPlayer))
                        AlignmentGuides.Add(new CablesManagementAlignmentMath.Guide{Position=part.LocalPosition,Normal=-(part.LocalRotation*Vector3.forward),Axis=part.LocalRotation*Vector3.up});
            foreach(var placement in Plan)
                AlignmentGuides.Add(new CablesManagementAlignmentMath.Guide{Position=placement.Coord,Normal=-(placement.Rotation*Vector3.forward),Axis=placement.Rotation*Vector3.up});
            if(AlignmentGuides.Count==0)
            {
                Vector3 axis=Vector3.ProjectOnPlane(Vector3.up,normal);
                if(axis.sqrMagnitude<.001f)axis=Vector3.ProjectOnPlane(Vector3.forward,normal);
                AlignmentGuides.Add(new CablesManagementAlignmentMath.Guide{Position=_sourceLocal+normal*Vector3.Dot(position-_sourceLocal,normal),Normal=normal,Axis=axis.normalized});
            }
            if(TryAlign(position,normal,AlignmentGuides,out var match)){position=match.Position;alignment=match;}
        }
        point=new Anchor(position,normal);return true;
    }

    internal static bool TryAlign(Vector3 position,Vector3 normal,IReadOnlyList<CablesManagementAlignmentMath.Guide> guides,out CablesManagementAlignmentMath.Match match)
    {
        if(!CablesManagementAlignmentMath.TryFind(position,normal,guides,(index,p,axis)=>true,out match)||!match.Snapped)return false;
        if(!CablesManagementAlignmentMath.TrySpacing(guides[match.Index].Position,match.Position,match.Axis,normal,.05f,out var spaced))return false;
        match.Position=spaced;return true;
    }

    internal static List<Anchor> Distribute(IReadOnlyList<Anchor> marked)
    {
        return new List<Anchor>(marked);
    }

    private static void Refresh()
    {
        Plan.Clear();ValidSlots.Clear();_requiredIron=0;var points=Distribute(Anchors);
        if(points.Count>64){_message="Route limit: 64 clips";return;}
        for(int i=0;i<points.Count;i++)
        {
            Vector3 tangent=Vector3.zero;
            if(i>0)tangent+=Vector3.ProjectOnPlane((points[i].Position-points[i-1].Position).normalized,points[i].Normal);
            else tangent+=Vector3.ProjectOnPlane((points[i].Position-_sourceLocal).normalized,points[i].Normal);
            if(i+1<points.Count)tangent+=Vector3.ProjectOnPlane((points[i+1].Position-points[i].Position).normalized,points[i].Normal);
            if(tangent.sqrMagnitude<.001f&&i==points.Count-1&&_target.EntityId!=0)
            {
                var ships=GameRefs.Service<ShipsClientTracker>();
                if(ships!=null&&ships.TryGetVisualSocketTransform(_target,out var target))
                    tangent=Vector3.ProjectOnPlane(_grid.ToLocalPos(target.position)-points[i].Position,points[i].Normal);
            }
            if(tangent.sqrMagnitude<.001f)tangent=Vector3.ProjectOnPlane(_grid.ToLocalRotation(Quaternion.LookRotation(_camera.Direction))*Vector3.right,points[i].Normal);
            if(tangent.sqrMagnitude<.001f)tangent=Vector3.ProjectOnPlane(Vector3.forward,points[i].Normal);
            PartOrientation forward=PartOrientationHelper.SnapAnyOrientation(-points[i].Normal);
            var up=CablesManagementSurfaceOrientation.SnapUp(tangent,forward);
            if(!up.HasValue){Plan.Clear();return;}
            Plan.Add(new FloatingPartPlacement(points[i].Position,forward,up.Value));
        }
        foreach(var placement in Plan)
        {
            bool existing=Existing(placement).HasValue;
            if(!existing)_requiredIron++;
            ValidSlots.Add(existing||Supported(placement)&&_grid.IsFloatingPlacementFree(placement,CableParts.Get(_settings).Bounds,null));
        }
        for(int i=0;i<Plan.Count;i++)for(int j=0;j<i;j++)
            if(Overlaps(Plan[i],Plan[j],CableParts.Get(_settings).Bounds))ValidSlots[i]=ValidSlots[j]=false;
    }

    private static void Reuse()
    {
        if(_lastShip!=_ship||Last.Count==0){_message="No previous route on this ship";return;}
        Anchors.Clear();
        for(int i=0;i<Last.Count;i++)
        {
            Vector3 tangent=i+1<Last.Count?Last[i+1].Position-Last[i].Position:i>0?Last[i].Position-Last[i-1].Position:Vector3.up;
            Vector3 side=Vector3.Cross(Last[i].Normal,tangent).normalized;
            Anchors.Add(new Anchor(Last[i].Position+side*.2f,Last[i].Normal));
        }
        _target=default;Refresh();_message="Previous path offset by 20 cm; choose the destination";
    }

    private static bool Validate()
    {
        if(_target.EntityId==0){_message="Click a destination socket first";return false;}
        var ships=GameRefs.Service<ShipsClientTracker>();var connections=GameRefs.Service<ConnectionsClientTracker>();
        if(ships==null||connections==null||!ships.TryGetStatefulPart(_target.PartContext,out _)||connections.IsSocketPhysicallyOccupied(_target)){_message="The destination is no longer available";return false;}
        foreach(var placement in Plan)
            if(Existing(placement)==null&&(!Supported(placement)||!_grid.IsFloatingPlacementFree(placement,CableParts.Get(_settings).Bounds,null))){_message="A clip position is blocked or off the surface; undo or change the route";return false;}
        for(int i=0;i<Plan.Count;i++)
        for(int j=0;j<i;j++)
            if(Overlaps(Plan[i],Plan[j],CableParts.Get(_settings).Bounds)){_message="Two clips overlap; spread the marked points farther apart";return false;}
        return true;
    }

    private static bool Supported(FloatingPartPlacement placement)
    {
        Vector3 normal=-(_grid.ToWorldRotation(placement.Rotation)*Vector3.forward);
        if(!ClientScene.PhysicsScene.Raycast(World(placement.Coord)+normal*.1f,-normal,out var hit,.2f,LayerMask.GetMask("Default","Ship","WorldObject")))return false;
        var grid=hit.collider.GetComponentInParent<IShipPartPlacements>();
        return grid!=null&&grid.ShipId==_ship&&Vector3.Dot(hit.normal,normal)>.98f&&Mathf.Abs(hit.distance-.102f)<.012f;
    }

    internal static bool Overlaps(FloatingPartPlacement first,FloatingPartPlacement second,Bounds bounds)
    {
        Vector3[] basis={Vector3.right,Vector3.up,Vector3.forward};
        Vector3[] a=basis.Select(axis=>first.Rotation*axis).ToArray(),b=basis.Select(axis=>second.Rotation*axis).ToArray();
        Vector3 delta=second.Coord+second.Rotation*bounds.center-first.Coord-first.Rotation*bounds.center;
        var axes=new List<Vector3>(a);axes.AddRange(b);
        foreach(var x in a)foreach(var y in b)axes.Add(Vector3.Cross(x,y));
        Vector3 half=bounds.extents;
        foreach(var candidate in axes)
        {
            if(candidate.sqrMagnitude<.000001f)continue;
            Vector3 axis=candidate.normalized;
            float radius=Mathf.Abs(Vector3.Dot(axis,a[0]))*half.x+Mathf.Abs(Vector3.Dot(axis,a[1]))*half.y+Mathf.Abs(Vector3.Dot(axis,a[2]))*half.z
                +Mathf.Abs(Vector3.Dot(axis,b[0]))*half.x+Mathf.Abs(Vector3.Dot(axis,b[1]))*half.y+Mathf.Abs(Vector3.Dot(axis,b[2]))*half.z;
            if(Mathf.Abs(Vector3.Dot(axis,delta))>=radius-.001f)return false;
        }
        return true;
    }

    private static void Confirm()
    {
        Refresh();if(Plan.Count==0&&Anchors.Count>0){_message="The route could not be planned";return;}
        if(!Validate())return;
        _index=0;_placed=null;_stage=Plan.Count==0?Stage.Finish:Stage.Place;_deadline=Time.unscaledTime+10f;
    }

    private static ushort? Existing(FloatingPartPlacement placement)
    {
        var ships=GameRefs.Service<ShipsClientTracker>();var connections=GameRefs.Service<ConnectionsClientTracker>();
        if(ships==null||connections==null||!ships.TryGetShip(_ship,out IShipStateRead ship))return null;
        foreach(var part in ship.StatefulParts)
            if(!(part.State is IPickablePartState held&&held.IsHeldByPlayer)&&part.Settings.id==_settings&&(part.LocalPosition-placement.Coord).sqrMagnitude<.0001f&&Quaternion.Angle(part.LocalRotation,placement.Rotation)<1f
                &&!connections.IsSocketPhysicallyOccupied(new EntityPartSocket(_ship,part.Id,0))&&!connections.IsSocketPhysicallyOccupied(new EntityPartSocket(_ship,part.Id,1)))return part.Id;
        return null;
    }

    private static void Build(PlayerState state)
    {
        if(Time.unscaledTime>_deadline){Stop("Route stopped. Check materials and connections; placed clips remain available.");return;}
        if(_stage!=Stage.WaitCable&&Holding(state)&&state.EntityId!=_cable){Stop("Route stopped: cable changed");return;}
        if((_stage==Stage.Place||_stage==Stage.WaitPlace||_stage==Stage.Plug||_stage==Stage.Finish||_stage==Stage.StyleColor||_stage==Stage.StyleName||_stage==Stage.StyleCurve)&&!Holding(state)){Stop("Route stopped: cable released");return;}
        var service=GameRefs.Service<IShipInteractionNetworkService>();var cables=GameRefs.Service<CablesClientTracker>();
        if(service==null||cables==null){Stop("Route stopped: world unavailable");return;}
        switch(_stage)
        {
            case Stage.Place:
                _placed=Existing(Plan[_index]);
                if(_placed.HasValue){Next(Stage.Plug);break;}
                if(!Supported(Plan[_index])||!_grid.IsFloatingPlacementFree(Plan[_index],CableParts.Get(_settings).Bounds,null)){Stop("Route stopped: placement blocked");break;}
                service.PlaceFloatingPart(new PlaceFloatingPartNetworkRequest{ShipId=_ship,SettingsId=_settings,Placement=Plan[_index]});Next(Stage.WaitPlace);break;
            case Stage.WaitPlace:
                _placed=Existing(Plan[_index]);if(_placed.HasValue)Next(Stage.Plug);break;
            case Stage.Plug:
                if(!Holding(state)||state.EntityId!=_cable){Stop("Route stopped: cable changed");break;}
                service.PlugInCable(new SocketNetworkRequest{EntityPartSocket=new EntityPartSocket(_ship,_placed.Value,0)});Next(Stage.WaitPlug);break;
            case Stage.WaitPlug:
                if(cables.TryGetCableState(_cable,out var linked)&&!linked.PlugA.IsHeldByPlayer&&!linked.PlugB.IsHeldByPlayer)Next(Stage.StartCable);break;
            case Stage.StartCable:
                if(state.Type!=PlayerStateType.Walking&&state.Type!=PlayerStateType.CheatFlying)break;
                service.AddNewCable(new SocketNetworkRequest{EntityPartSocket=new EntityPartSocket(_ship,_placed.Value,1)});Next(Stage.WaitCable);break;
            case Stage.WaitCable:
                if(!Holding(state)||!cables.TryGetCableState(state.EntityId,out var held))break;
                if(!held.PlugA.IsHeldByPlayer&&!held.PlugB.IsHeldByPlayer)break;
                var fixedPlug=held.PlugA.IsHeldByPlayer?held.PlugB:held.PlugA;
                if(fixedPlug.EntityPartSocket!=new EntityPartSocket(_ship,_placed.Value,1)){Stop("Route stopped: cable changed");break;}
                _cable=state.EntityId;
                Next(Stage.StyleColor);_styleAt=Time.unscaledTime;break;
            case Stage.StyleColor:
                if(!_color.HasValue||CablePaintStore.TryGet(_cable,out uint painted)&&painted==_color.Value)
                {Next(Stage.StyleName);_styleAt=Time.unscaledTime+.12f;break;}
                if(Time.unscaledTime>=_styleAt){CablePaintNetwork.Send(_cable,_color);_styleAt=Time.unscaledTime+.2f;}break;
            case Stage.StyleName:
                if(string.IsNullOrEmpty(_name)||CableNameStore.Get(_cable)==_name)
                {Next(Stage.StyleCurve);_styleAt=Time.unscaledTime+.12f;break;}
                if(Time.unscaledTime>=_styleAt){CablePaintNetwork.SendName(_cable,_name);_styleAt=Time.unscaledTime+.2f;}break;
            case Stage.StyleCurve:
                if(!_curved||CableRoutingStore.IsCurved(_cable)){_index++;Next(_index<Plan.Count?Stage.Place:Stage.Finish);break;}
                if(Time.unscaledTime>=_styleAt){CablePaintNetwork.SendRouting(_cable,true);_styleAt=Time.unscaledTime+.2f;}break;
            case Stage.Finish:
                if(!Holding(state)||state.EntityId!=_cable){Stop("Route stopped: cable changed");break;}
                if(!ValidateDestination()){Stop("Route stopped: destination occupied");break;}
                service.PlugInCable(new SocketNetworkRequest{EntityPartSocket=_target});Next(Stage.WaitFinish);break;
            case Stage.WaitFinish:
                if(cables.TryGetCableState(_cable,out var finished)&&!finished.PlugA.IsHeldByPlayer&&!finished.PlugB.IsHeldByPlayer)
                {Last.Clear();Last.AddRange(Anchors);_lastShip=_ship;_lastGrid=_grid;Stop("Route built");}break;
        }
    }
    private static bool ValidateDestination()=>GameRefs.Service<ConnectionsClientTracker>()?.IsSocketPhysicallyOccupied(_target)==false;
    private static void Next(Stage stage){_stage=stage;_deadline=Time.unscaledTime+10f;}
    private static void Stop(string message){_stage=Stage.Off;Anchors.Clear();Plan.Clear();ValidSlots.Clear();_requiredIron=0;Hide();Notice(message);}
    private static void Notice(string message){_message=message;_messageUntil=Time.unscaledTime+5f;}

    private static Vector3 World(Vector3 local)=>_grid.ToWorldPos(new FloatingPartPlacement(local,PartOrientation.Forward));
    private static void Preview()
    {
        var ships=GameRefs.Service<ShipsClientTracker>();
        if(ships==null||!Held(out var cable))return;
        var fixedPlug=cable.PlugA.IsHeldByPlayer?cable.PlugB:cable.PlugA;
        if(!ships.TryGetVisualSocketTransform(fixedPlug.EntityPartSocket,out var start))return;
        var path=new List<Vector3>{start.position};
        foreach(var placement in Plan)
        {
            Vector3 p=placement.Coord+placement.Rotation*new Vector3(0f,0f,-CablesManagementLayout.ChannelHeight);
            path.Add(World(p));
            Ghost(placement);
            Line(World(p)-_grid.ToWorldRotation(placement.Rotation)*Vector3.up*.04f,World(p)+_grid.ToWorldRotation(placement.Rotation)*Vector3.up*.04f,.015f);
        }
        if(_target.EntityId!=0&&ships.TryGetVisualSocketTransform(_target,out var target))path.Add(target.position);
        for(int i=1;i<path.Count;i++)Line(path[i-1],path[i],.006f);
        if(_target.EntityId==0)PreviewPointer();
    }
    private static void PreviewPointer()
    {
        if(Time.unscaledTime-_cameraAt>.25f)return;
        int count=ClientScene.PhysicsScene.Raycast(_camera.CameraPos,_camera.Direction,Hits,3f,LayerMask.GetMask("Default","Ship","Interactable","WorldObject"));
        Array.Sort(Hits,0,count,Comparer<RaycastHit>.Create((a,b)=>a.distance.CompareTo(b.distance)));
        foreach(var hit in Hits.Take(count))
        {
            if(hit.collider.GetComponent<IPartSocket>()!=null)return;
            if(hit.collider.CompareTag("Interactable"))continue;
            if(!SurfacePoint(hit,out Anchor point,out var match))return;
            Vector3 tangent=Vector3.ProjectOnPlane(point.Position-(Anchors.Count==0?_sourceLocal:Anchors[Anchors.Count-1].Position),point.Normal);
            if(tangent.sqrMagnitude<.001f)tangent=Vector3.ProjectOnPlane(Vector3.up,point.Normal);
            if(tangent.sqrMagnitude<.001f)tangent=Vector3.ProjectOnPlane(Vector3.forward,point.Normal);
            var forward=PartOrientationHelper.SnapAnyOrientation(-point.Normal);
            var up=CablesManagementSurfaceOrientation.SnapUp(tangent,forward);
            if(!up.HasValue)return;
            var placement=new FloatingPartPlacement(point.Position,forward,up.Value);
            bool valid=Anchors.Count<24&&(Anchors.Count==0||(Anchors[Anchors.Count-1].Position-point.Position).sqrMagnitude>=.01f)
                &&Supported(placement)&&_grid.IsFloatingPlacementFree(placement,CableParts.Get(_settings).Bounds,null)
                &&!Plan.Any(other=>Overlaps(placement,other,CableParts.Get(_settings).Bounds));
            Ghost(placement,valid);
            if(match.HasValue)
            {
                var aligned=match.Value;
                if(CablesManagementAlignmentMath.TryGetSegment(AlignmentGuides[aligned.Index].Position,aligned.Position,point.Normal,
                    CablesManagementLayout.ChannelHeight+.01f,.06f,out var a,out var b))Line(World(a),World(b),.014f);
            }
            return;
        }
    }
    private static void Ghost(FloatingPartPlacement placement,bool? validity=null)
    {
        int index=_renderedGhosts++;
        if(index==Ghosts.Count)
        {
            var visual=CableParts.Get(_settings).part.GetComponentInChildren<Ships.Parts.Common.Model.Reactions.IShipPartVisual>().GameObject;
            var ghost=Object.Instantiate(visual);
            ghost.name="Planned cable clip";
            foreach(var behavior in ghost.GetComponentsInChildren<MonoBehaviour>(true))behavior.enabled=false;
            foreach(var collider in ghost.GetComponentsInChildren<Collider>(true))collider.enabled=false;
            var renderers=ghost.GetComponentsInChildren<MeshRenderer>(true);
            foreach(var renderer in renderers)renderer.sharedMaterials=Enumerable.Repeat(CablesManagementAlignment.GuideMaterial,renderer.sharedMaterials.Length).ToArray();
            Ghosts.Add(new PreviewGhost{Root=ghost,Renderers=renderers});
        }
        var cached=Ghosts[index];var root=cached.Root;
        if(!root.activeSelf)root.SetActive(true);
        root.transform.position=World(placement.Coord);root.transform.rotation=_grid.ToWorldRotation(placement.Rotation);
        bool valid=validity??(index<ValidSlots.Count&&ValidSlots[index]);
        if(cached.Valid!=valid)
        {
            cached.Valid=valid;var block=new MaterialPropertyBlock();
            Color color=valid?new Color(.15f,.7f,1f):new Color(1f,.22f,.18f);
            block.SetColor("_Color",color);block.SetColor("_BaseColor",color);block.SetColor("_RenderColor",color);
            foreach(var renderer in cached.Renderers)renderer.SetPropertyBlock(block);
        }
    }
    private static int _renderedGhosts;
    private static void Line(Vector3 a,Vector3 b,float width)
    {
        LineRenderer line;
        if(_rendered==Lines.Count)
        {
            var go=new GameObject("Cable route preview"){hideFlags=HideFlags.HideAndDontSave};
            line=go.AddComponent<LineRenderer>();line.positionCount=2;line.useWorldSpace=true;line.sharedMaterial=CablesManagementAlignment.GuideMaterial;
            line.textureMode=LineTextureMode.Tile;line.numCapVertices=3;line.startColor=line.endColor=Color.white;Lines.Add(line);
        }
        line=Lines[_rendered++];line.enabled=true;line.startWidth=line.endWidth=width;line.SetPosition(0,a);line.SetPosition(1,b);
    }
    private static void TrimPreview()
    {
        for(int i=_rendered;i<Lines.Count;i++)if(Lines[i]!=null)Lines[i].enabled=false;
        for(int i=_renderedGhosts;i<Ghosts.Count;i++)if(Ghosts[i].Root!=null&&Ghosts[i].Root.activeSelf)Ghosts[i].Root.SetActive(false);
    }
    private static void Hide(){_rendered=0;_renderedGhosts=0;TrimPreview();}
    internal static void Draw()
    {
        if(!Application.isFocused||CablesManagementControls.MenuOpen||(!Active&&(string.IsNullOrEmpty(_message)||Time.unscaledTime>_messageUntil)))
        { CablesManagementRouteHud.Hide();return; }
        if(_stage==Stage.Planning)
        {
            bool blocked=ValidSlots.Any(valid=>!valid);
            string commands="LMB  Add point / select socket\nRMB  Undo"+(Last.Count>0?"     MMB  Reuse path":"")
                +"\n"+CablesManagementControls.Label("Align")+"  Align"+(_target.EntityId!=0?"     Enter  Build":"")
                +"\n"+CablesManagementControls.Label("PlanRoute")+" / Esc  Cancel";
            CablesManagementRouteHud.Show("Cable route",Plan.Count+" clips · "+(FreeBuild?"Free build":_requiredIron+" Iron"),commands,
                blocked?"Blocked clips are marked red":_message,blocked);
        }
        else if(Active)
            CablesManagementRouteHud.Show("Building route",Mathf.Min(_index+1,Mathf.Max(1,Plan.Count))+" / "+Mathf.Max(1,Plan.Count)+" clips","","",false);
        else CablesManagementRouteHud.Show("Cable route","","",_message,false);
    }
    internal static void Remove(){Stop("");CablesManagementRouteHud.Remove();foreach(var line in Lines)if(line!=null)Object.Destroy(line.gameObject);foreach(var ghost in Ghosts)if(ghost.Root!=null)Object.Destroy(ghost.Root);Lines.Clear();Ghosts.Clear();Last.Clear();AlignmentGuides.Clear();_grid=null;_lastGrid=null;_confirmHeld=false;}
}

internal static class CablesManagementRouteHud
{
    private static GameObject _root;
    private static RectTransform _panel;
    private static CanvasScaler _scaler;
    private static TMP_Text _title,_status,_commands,_note;
    private static readonly Color Blue=new Color(.2f,.64f,.84f);

    internal static void Show(string title,string status,string commands,string note,bool warning)
    {
        Prepare();
        _scaler.scaleFactor=Mathf.Min(Mathf.Clamp(Screen.height/900f,.65f,1.6f),Screen.width/700f);
        Set(_title,title);Set(_status,status);Set(_commands,commands);Set(_note,note);
        float y=36f;
        if(!string.IsNullOrEmpty(status)){Place(_status,y,22f);y+=28f;}
        if(!string.IsNullOrEmpty(commands))
        {
            float height=Mathf.Max((commands.Count(c=>c=='\n')+1)*22f,_commands.GetPreferredValues(commands,276f,0f).y);
            Place(_commands,y,height);y+=height+8f;
        }
        if(!string.IsNullOrEmpty(note))
        {
            _note.color=warning?new Color(1f,.55f,.42f):new Color(.72f,.8f,.84f);
            float height=Mathf.Max(22f,_note.GetPreferredValues(note,276f,0f).y);
            Place(_note,y,height);y+=height;
        }
        _panel.sizeDelta=new Vector2(300f,y+12f);
        if(!_root.activeSelf)_root.SetActive(true);
    }

    private static void Prepare()
    {
        if(_root!=null)return;
        var tips=Resources.FindObjectsOfTypeAll<ToolKeyTipUi>();
        var native=tips.FirstOrDefault(t=>t!=null&&t.isActiveAndEnabled)??tips.FirstOrDefault(t=>t!=null);
        TMP_Text sample=native!=null?Traverse.Create(native).Field("label").GetValue<TMP_Text>():null;
        TMP_FontAsset font=sample!=null&&sample.font!=null?sample.font:TMP_Settings.defaultFontAsset;
        _root=new GameObject("Cable route instructions",typeof(RectTransform)){layer=5,hideFlags=HideFlags.HideAndDontSave};
        _root.SetActive(false);Object.DontDestroyOnLoad(_root);
        var canvas=_root.AddComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=80;
        _scaler=_root.AddComponent<CanvasScaler>();_scaler.uiScaleMode=CanvasScaler.ScaleMode.ConstantPixelSize;
        _panel=Rect("Route",_root.transform);
        _panel.anchorMin=_panel.anchorMax=Vector2.zero;_panel.pivot=Vector2.zero;
        _panel.anchoredPosition=new Vector2(20f,100f);
        var background=_panel.gameObject.AddComponent<Image>();background.color=new Color(.035f,.1f,.14f,.82f);background.raycastTarget=false;
        var line=Rect("Divider",_panel);line.anchoredPosition=Vector2.zero;line.sizeDelta=new Vector2(300f,2f);
        var image=line.gameObject.AddComponent<Image>();image.color=Blue;image.raycastTarget=false;
        _title=Text("Title",font,18f,Blue);Place(_title,10f,24f);
        _status=Text("Cost",font,15f,Color.white);
        _commands=Text("Controls",font,15f,new Color(.94f,.96f,.97f));
        _note=Text("Status",font,14f,Color.white);
    }

    private static RectTransform Rect(string name,Transform parent)
    {
        var go=new GameObject(name,typeof(RectTransform)){layer=5};go.transform.SetParent(parent,false);
        var rect=(RectTransform)go.transform;rect.anchorMin=rect.anchorMax=new Vector2(0f,1f);rect.pivot=new Vector2(0f,1f);
        return rect;
    }

    private static TMP_Text Text(string name,TMP_FontAsset font,float size,Color color)
    {
        var rect=Rect(name,_panel);var text=rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.font=font;text.fontSize=size;text.color=color;text.raycastTarget=false;
        text.alignment=TextAlignmentOptions.TopLeft;text.textWrappingMode=TextWrappingModes.Normal;text.richText=false;
        return text;
    }

    private static void Place(TMP_Text text,float y,float height)
    {
        text.rectTransform.anchoredPosition=new Vector2(12f,-y);text.rectTransform.sizeDelta=new Vector2(276f,height);
    }

    private static void Set(TMP_Text text,string value)
    {
        bool visible=!string.IsNullOrEmpty(value);
        if(text.gameObject.activeSelf!=visible)text.gameObject.SetActive(visible);
        if(text.text!=value)text.text=value;
    }

    internal static void Hide(){if(_root!=null&&_root.activeSelf)_root.SetActive(false);}
    internal static void Remove(){if(_root!=null)Object.Destroy(_root);_root=null;_panel=null;_scaler=null;_title=_status=_commands=_note=null;}
}
