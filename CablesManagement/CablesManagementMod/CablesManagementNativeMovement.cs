using System;
using System.Collections.Generic;
using HarmonyLib;
using FishNet.Connection;
using Managers.Client.Services;
using Managers.Server.Services;
using Managers.Interface.Model;
using Players.Interface.Model;
using Players.Services;
using Ships.Interface.Model.Parts;
using Ships.Parts.Common.Model.Reactions;
using Ships.Services;
using Ships.Cables;
using Ships.Interface.Model.Parts.Common;
using Ships.Parts.Common;
using Players.LocalClient.Interaction;
using SDModKit.Game;
using UnityEngine;

namespace CablesManagementMod;

public static class CablesManagementNativeMovement
{
    private sealed class CableMetadata
    {
        public CablePlugSocket Other;
        public byte Port;
        public CableType Type;
        public bool MovingA;
        public uint? Color;
        public string Name;
        public bool Curved;
    }
    private sealed class MoveScope
    {
        public MoveScope Previous;
        public uint Destination;
        public readonly List<CableMetadata> Cables = new List<CableMetadata>();
    }
    [ThreadStatic] private static MoveScope _scope;

    internal static bool HoveringClip()
    {
        var hover = GameRefs.Service<InteractableHoverTracker>()?.CurrentHoveredInteractable;
        if (!(hover is PartInteraction interaction)) return false;
        PartContext context = Traverse.Create(interaction).Field("PartContext").GetValue<PartContext>();
        var ships = GameRefs.Service<ShipsClientTracker>();
        return ships != null && ships.TryGetStatefulPart(context, out var part) && CablesManagementMod.IsClip(part.Settings.id);
    }

    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(LocalLocalPlayerHeldPart), nameof(LocalLocalPlayerHeldPart.Rotate)),
            prefix: new HarmonyMethod(typeof(CablesManagementNativeMovement), nameof(BeforeRotate)));
        harmony.Patch(AccessTools.Method(typeof(ShipInteractionService), "RpcLogic___PlaceHeldPart_ServerRPC___2331514408"),
            prefix: new HarmonyMethod(typeof(CablesManagementNativeMovement), nameof(BeforePlace)),
            finalizer: new HarmonyMethod(typeof(CablesManagementNativeMovement), nameof(AfterPlace)));
        harmony.Patch(AccessTools.Method(typeof(CablesServerTracker), nameof(CablesServerTracker.AddCable)),
            postfix: new HarmonyMethod(typeof(CablesManagementNativeMovement), nameof(AfterAddCable)));
    }

    private static void BeforePlace(PlaceHeldPartNetworkRequest __0, NetworkConnection __1,
        PlayersServerTracker ____players, ShipsServerTracker ____ships, CablesServerTracker ____cables, out MoveScope __state)
    {
        __state = null;
        if (__1 == null || ____players == null || ____ships == null || ____cables == null) return;
        var player = ____players.GetTrackedPlayerFromConnectionId(__1.ClientId);
        if (player == null || player.StateVal != PlayerStateType.WalkingHoldingShipPart && player.StateVal != PlayerStateType.CheatFlyingHoldingShipPart) return;
        var context = new PartContext(player.State.EntityId, player.State.PartId);
        if (!____ships.TryGetStatefulPart(context, out _)) return;
        var scope = new MoveScope { Previous = _scope, Destination = __0.ShipId };
        foreach (var cable in ____cables.GetCablesConnectedToPart(context))
        {
            bool movingA = !cable.State.PlugA.IsHeldByPlayer && cable.State.PlugA.EntityPartSocket.PartContext == context;
            CablePlugSocket moving = movingA ? cable.State.PlugA : cable.State.PlugB;
            scope.Cables.Add(new CableMetadata
            {
                MovingA = movingA, Port = moving.PartSocket.SocketId, Type = cable.State.Type,
                Other = movingA ? cable.State.PlugB : cable.State.PlugA,
                Color = CablePaintStore.TryGet(cable.Id, out uint rgb) ? (uint?)rgb : null,
                Name = CableNameStore.Get(cable.Id), Curved = CableRoutingStore.IsCurved(cable.Id)
            });
        }
        __state = scope;
        _scope = scope;
    }

    private static Exception AfterPlace(Exception __exception, MoveScope __state)
    {
        if (__state != null) _scope = __state.Previous;
        return __exception;
    }

    private static void AfterAddCable(CableState cableState, uint __result)
    {
        MoveScope scope = _scope;
        if (scope == null) return;
        for (int i = 0; i < scope.Cables.Count; i++)
        {
            CableMetadata metadata = scope.Cables[i];
            CablePlugSocket moving = metadata.MovingA ? cableState.PlugA : cableState.PlugB;
            CablePlugSocket other = metadata.MovingA ? cableState.PlugB : cableState.PlugA;
            if (cableState.Type != metadata.Type || moving.IsHeldByPlayer || moving.EntityId != scope.Destination
                || moving.PartSocket.SocketId != metadata.Port || !other.Equals(metadata.Other)) continue;
            scope.Cables.RemoveAt(i);
            CablePaintNetwork.RestoreMovedMetadata(__result, metadata.Color, metadata.Name, metadata.Curved);
            break;
        }
    }

    private static bool BeforeRotate(float amount, PlayersClientTracker ____players, ShipsClientTracker ____ships)
    {
        var player = ____players?.LocalPlayer;
        if (player == null || ____ships == null) return true;
        var state = player.State;
        if (state.Type != PlayerStateType.WalkingHoldingShipPart && state.Type != PlayerStateType.CheatFlyingHoldingShipPart) return true;
        var context = new PartContext(state.EntityId, state.PartId);
        if (!____ships.TryGetStatefulPart(context, out var part) || part.Settings == null || !CablesManagementMod.IsClip(part.Settings.id)
            || !____ships.TryGetVisualPart(context, out IShipPartVisual visual) || visual.GameObject == null) return true;
        Transform preview = visual.GameObject.transform;
        Vector3 normal = -(preview.rotation * Vector3.forward);
        preview.rotation = Quaternion.AngleAxis(amount * 45f, normal) * preview.rotation;
        return false;
    }
}
