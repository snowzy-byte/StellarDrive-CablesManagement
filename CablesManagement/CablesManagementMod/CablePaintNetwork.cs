using System.Collections.Generic;
using System.Linq;
using Core.Services;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Serializing;
using FishNet.Transporting;
using HarmonyLib;
using Players;
using Players.Interface.Model;
using SDModKit.Game;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Services;
using UnityEngine;
using WorldTracking.Interface.Services;

using Command = global::CableClipMod.CablePaintNetwork.Command;
using Snapshot = global::CableClipMod.CablePaintNetwork.Snapshot;

namespace CablesManagementMod;

public static class CablePaintNetwork
{

    private static NetworkManager _network;
    private static bool _remoteActive, _receivedSnapshot;
    private static float _nextSnapshotRequest;
    private static readonly Dictionary<int, float> NextCommand = new Dictionary<int, float>();
    private static readonly Dictionary<int, float> NextSnapshot = new Dictionary<int, float>();

    public static void Install(HarmonyLib.Harmony harmony)
    {
        GenericWriter<Command>.SetWrite((w, m) => { w.WriteUInt32(m.Cable); w.WriteUInt32(m.Rgb); w.WriteUInt8Unpacked(m.Kind); });
        GenericReader<Command>.SetRead(r => new Command { Cable=r.ReadUInt32(), Rgb=r.ReadUInt32(), Kind=r.ReadUInt8Unpacked() });
        GenericWriter<Snapshot>.SetWrite((w, m) => { w.WriteUInt32(m.Cable); w.WriteUInt32(m.Rgb); w.WriteUInt8Unpacked(m.Kind); });
        GenericReader<Snapshot>.SetRead(r => new Snapshot { Cable=r.ReadUInt32(), Rgb=r.ReadUInt32(), Kind=r.ReadUInt8Unpacked() });
        GenericWriter<CableNameCommand>.SetWrite((w, m) => { w.WriteUInt32(m.Cable); w.WriteString(m.Name); });
        GenericReader<CableNameCommand>.SetRead(r => new CableNameCommand { Cable=r.ReadUInt32(), Name=r.ReadStringAllocated() });
        GenericWriter<CableNameSnapshot>.SetWrite((w, m) => { w.WriteUInt32(m.Cable); w.WriteString(m.Name); w.WriteUInt8Unpacked(m.Kind); });
        GenericReader<CableNameSnapshot>.SetRead(r => new CableNameSnapshot { Cable=r.ReadUInt32(), Name=r.ReadStringAllocated(), Kind=r.ReadUInt8Unpacked() });
        harmony.Patch(AccessTools.Method(typeof(CablesServerTracker), nameof(CablesServerTracker.DeleteCable)),
            postfix: new HarmonyMethod(typeof(CablePaintNetwork), nameof(AfterDelete)));
        harmony.Patch(AccessTools.Method(typeof(CablesServerTracker), nameof(CablesServerTracker.LoadState)),
            postfix: new HarmonyMethod(typeof(CablePaintNetwork), nameof(AfterLoad)));
    }

    public static void Update()
    {
        NetworkManager current = InstanceFinder.NetworkManager;
        if (_network != current)
        {
            Unregister();
            _network = current;
            _receivedSnapshot = false;
            _nextSnapshotRequest = 0f;
            _remoteActive = false;
            NextCommand.Clear(); NextSnapshot.Clear();
            if (_network != null)
            {
                _network.ServerManager.RegisterBroadcast<Command>(ReceiveCommand, true);
                _network.ClientManager.RegisterBroadcast<Snapshot>(ReceiveSnapshot);
                _network.ServerManager.RegisterBroadcast<CableNameCommand>(ReceiveNameCommand, true);
                _network.ClientManager.RegisterBroadcast<CableNameSnapshot>(ReceiveNameSnapshot);
            }
        }
        bool active = _network != null && (_network.IsServerStarted || _network.IsClientStarted);
        bool server = _network != null && _network.IsServerStarted;
        bool remote = active && !server;
        if (remote && !_remoteActive)
        {

            CablePaintStore.Clear(); CableNameStore.Clear(); CableRoutingStore.Clear(); CableRoutes.Invalidate();
            _receivedSnapshot=false; _nextSnapshotRequest=0f;
        }
        _remoteActive=remote;
        if (active && !server && !_receivedSnapshot && Time.unscaledTime >= _nextSnapshotRequest)
        {
            _nextSnapshotRequest = Time.unscaledTime + 2f;
            _network.ClientManager.Broadcast(new Command { Kind=2 }, Channel.Reliable);
        }
    }

    public static bool Send(uint cable, uint? rgb)
    {
        Update();
        if (_network == null) return false;
        var command = new Command { Cable=cable, Rgb=rgb??0, Kind=(byte)(rgb.HasValue ? 0 : 1) };
        if (_network.IsServerStarted) return Apply(PlayerAccess.LocalServerPlayer, command);
        if (!_network.IsClientStarted) return false;
        _network.ClientManager.Broadcast(command, Channel.Reliable);
        return true;
    }

    public static bool SendRoute(uint cable, uint? rgb)
    {
        Update();
        if (_network == null || cable == 0 || rgb.HasValue && rgb.Value > 0xFFFFFF) return false;
        var command = new Command { Cable=cable, Rgb=rgb??0, Kind=(byte)(rgb.HasValue ? 3 : 4) };
        if (_network.IsServerStarted) return Apply(PlayerAccess.LocalServerPlayer, command);
        if (!_network.IsClientStarted) return false;
        _network.ClientManager.Broadcast(command, Channel.Reliable);
        return true;
    }

    public static bool SendName(uint cable, string name)
    {
        Update();
        if (_network == null || cable == 0) return false;
        var command = new CableNameCommand { Cable=cable, Name=CableNameStore.Normalize(name) };
        if (_network.IsServerStarted) return ApplyName(PlayerAccess.LocalServerPlayer, command);
        if (!_network.IsClientStarted) return false;
        _network.ClientManager.Broadcast(command, Channel.Reliable);
        return true;
    }

    public static bool SendRouting(uint cable, bool curved)
    {
        Update();
        if (_network == null || cable == 0) return false;
        var command = new Command { Cable=cable, Kind=(byte)(curved ? 5 : 6) };
        if (_network.IsServerStarted) return Apply(PlayerAccess.LocalServerPlayer, command);
        if (!_network.IsClientStarted) return false;
        _network.ClientManager.Broadcast(command, Channel.Reliable);
        return true;
    }

    private static void ReceiveCommand(NetworkConnection connection, Command command, Channel channel)
    {
        if (connection == null || _network == null || !_network.IsServerStarted) return;
        int id = connection.ClientId;
        if (command.Kind == 2)
        {
            if (NextSnapshot.TryGetValue(id, out float next) && Time.unscaledTime < next) return;
            NextSnapshot[id] = Time.unscaledTime + 1.5f;
            _network.ServerManager.Broadcast(connection, new Snapshot { Kind=2 }, true, Channel.Reliable);
            foreach (var color in CablePaintStore.Snapshot())
                _network.ServerManager.Broadcast(connection, new Snapshot { Kind=0, Cable=color.Key, Rgb=color.Value }, true, Channel.Reliable);
            _network.ServerManager.Broadcast(connection, new CableNameSnapshot { Kind=2 }, true, Channel.Reliable);
            foreach (var name in CableNameStore.Snapshot())
                _network.ServerManager.Broadcast(connection, new CableNameSnapshot { Cable=name.Key, Name=name.Value }, true, Channel.Reliable);
            foreach (uint cable in CableRoutingStore.Snapshot())
                _network.ServerManager.Broadcast(connection, new Snapshot { Kind=4, Cable=cable }, true, Channel.Reliable);
            _network.ServerManager.Broadcast(connection, new Snapshot { Kind=3 }, true, Channel.Reliable);
            return;
        }
        if (NextCommand.TryGetValue(id, out float nextCommand) && Time.unscaledTime < nextCommand) return;
        NextCommand[id] = Time.unscaledTime + .08f;
        var players = GameRefs.PlayersServer;
        if (players == null) return;
        Apply(players.ActivePlayers.FirstOrDefault(p => p.ConnectionId == id), command);
    }

    private static bool Apply(TrackedPlayerServer player, Command command)
    {
        if (command.Kind == 5 || command.Kind == 6)
        {
            if (!CanChangeRouting(player, command.Cable)) return false;
            bool curved = command.Kind == 5;
            if (CableRoutingStore.Set(command.Cable, curved))
                _network.ServerManager.Broadcast(new Snapshot { Cable=command.Cable, Kind=(byte)(curved ? 4 : 5) }, true, Channel.Reliable);
            return true;
        }
        if ((command.Kind != 0 && command.Kind != 1 && command.Kind != 3 && command.Kind != 4)
            || command.Rgb > 0xFFFFFF || !CanEdit(player, command.Cable)) return false;
        uint[] route = command.Kind <= 1 ? new[] { command.Cable } : CableRoutes.FindServer(command.Cable);
        if (route.Length == 0) return false;
        uint? rgb = command.Kind == 0 || command.Kind == 3 ? (uint?)command.Rgb : null;
        foreach (uint id in route)
        {
            if (!CablePaintStore.Set(id, rgb)) continue;
            _network.ServerManager.Broadcast(new Snapshot { Cable=id, Rgb=command.Rgb, Kind=(byte)(rgb.HasValue ? 0 : 1) }, true, Channel.Reliable);
        }
        return true;
    }

    private static bool CanChangeRouting(TrackedPlayerServer player, uint id)
    {
        var cables = GameRefs.Service<CablesServerTracker>();
        return player != null && cables != null && cables.TryGetCableState(id, out CableState cable)
            && (CableRoutingAccess.OwnsHeldCable(player.State, player.Id, id, cable) || CanEdit(player, id));
    }

    private static bool CanEdit(TrackedPlayerServer player, uint id)
    {
        if (player == null || id == 0) return false;
        if (player.StateVal != PlayerStateType.Walking && player.StateVal != PlayerStateType.CheatFlying) return false;
        var cables = GameRefs.Service<CablesServerTracker>();
        if (cables == null || !cables.TryGetCableState(id, out CableState state) || state.PlugA.IsHeldByPlayer || state.PlugB.IsHeldByPlayer) return false;
        var transpose = ServiceLocator.GetService<IWorldTransposeServer>();
        var ships = GameRefs.Service<ShipsServerTracker>();
        if (transpose == null || ships == null || !TryEndpoint(state.PlugA, ships, transpose, player.SpaceId, out Vector3 a)
            || !TryEndpoint(state.PlugB, ships, transpose, player.SpaceId, out Vector3 b)) return false;
        return CablePaintMath.DistanceSquared(player.Position, a, b) <= 16f;
    }

    private static void ReceiveNameCommand(NetworkConnection connection, CableNameCommand command, Channel channel)
    {
        if (connection == null || _network == null || !_network.IsServerStarted) return;
        int id = connection.ClientId;
        if (NextCommand.TryGetValue(id, out float next) && Time.unscaledTime < next) return;
        NextCommand[id] = Time.unscaledTime + .08f;
        var players = GameRefs.PlayersServer;
        if (players != null) ApplyName(players.ActivePlayers.FirstOrDefault(p => p.ConnectionId == id), command);
    }

    private static bool ApplyName(TrackedPlayerServer player, CableNameCommand command)
    {
        if (command.Name == null || command.Name.Length > CableNameStore.MaximumLength || !CanEdit(player, command.Cable)) return false;
        string name = CableNameStore.Normalize(command.Name);
        uint[] route = CableRoutes.FindServer(command.Cable);
        if (route.Length == 0) return false;
        foreach (uint id in route)
        {
            if (!CableNameStore.Set(id, name)) continue;
            _network.ServerManager.Broadcast(new CableNameSnapshot { Cable=id, Name=name }, true, Channel.Reliable);
        }
        return true;
    }

    internal static void RestoreMovedMetadata(uint id, uint? rgb, string name, bool curved = false)
    {
        Update();
        if (_network == null || !_network.IsServerStarted || id == 0) return;
        if (CablePaintStore.Set(id, rgb))
            _network.ServerManager.Broadcast(new Snapshot { Cable=id, Rgb=rgb??0, Kind=(byte)(rgb.HasValue ? 0 : 1) }, true, Channel.Reliable);
        if (CableNameStore.Set(id, name))
            _network.ServerManager.Broadcast(new CableNameSnapshot { Cable=id, Name=CableNameStore.Get(id) }, true, Channel.Reliable);
        if (CableRoutingStore.Set(id, curved))
            _network.ServerManager.Broadcast(new Snapshot { Cable=id, Kind=(byte)(curved ? 4 : 5) }, true, Channel.Reliable);
        CableRoutes.Invalidate();
    }

    private static void ReceiveNameSnapshot(CableNameSnapshot snapshot, Channel channel)
    {
        if (_network == null || _network.IsServerStarted) return;
        if (snapshot.Kind == 2) CableNameStore.Clear();
        else if (snapshot.Kind == 0) CableNameStore.Set(snapshot.Cable, snapshot.Name);
    }

    private static bool TryEndpoint(CablePlugSocket plug, ShipsServerTracker ships, IWorldTransposeServer transpose,
        uint playerSpace, out Vector3 point)
    {
        point = default;
        var ship = ships.GetShip(plug.EntityId);
        if (ship == null || !ships.TryGetStatefulPart(plug.EntityPartSocket.PartContext, out StatefulPart part)) return false;
        SocketObject socket = part.Settings.GetSockets().FirstOrDefault(s => s.Id == plug.PartSocket.SocketId);
        if (socket == null) return false;
        Vector3 local = part.Settings.part.transform.InverseTransformPoint(socket.transform.position);
        point = transpose.Position(ship.RelativeSpaceId, playerSpace, part.LocalPosition + part.LocalRotation * local);
        return true;
    }

    private static void ReceiveSnapshot(Snapshot snapshot, Channel channel)
    {
        if (_network == null || _network.IsServerStarted) return;
        if (snapshot.Kind == 2) { CablePaintStore.Clear(); CableNameStore.Clear(); CableRoutingStore.Clear(); return; }
        if (snapshot.Kind == 3) { _receivedSnapshot = true; return; }
        if (snapshot.Kind <= 1) CablePaintStore.Set(snapshot.Cable, snapshot.Kind == 0 ? (uint?)snapshot.Rgb : null);
        else if (snapshot.Kind == 4 || snapshot.Kind == 5) CableRoutingStore.Set(snapshot.Cable, snapshot.Kind == 4);
    }

    private static void AfterDelete(uint id)
    {
        CableRoutes.Invalidate();
        bool paint = CablePaintStore.Set(id, null), name = CableNameStore.Set(id, null), curved = CableRoutingStore.Set(id, false);
        if (_network != null && _network.IsServerStarted)
        {
            if (paint)
                _network.ServerManager.Broadcast(new Snapshot { Cable=id, Kind=1 }, true, Channel.Reliable);
            if (name)
                _network.ServerManager.Broadcast(new CableNameSnapshot { Cable=id, Name=string.Empty }, true, Channel.Reliable);
            if (curved)
                _network.ServerManager.Broadcast(new Snapshot { Cable=id, Kind=5 }, true, Channel.Reliable);
        }
    }

    private static void AfterLoad(CablesServerTracker __instance)
    {

        foreach(var color in CablePaintStore.Snapshot())
            if(!__instance.TryGetCableState(color.Key,out _)) CablePaintStore.Set(color.Key,null);
        foreach(var name in CableNameStore.Snapshot())
            if(!__instance.TryGetCableState(name.Key,out _)) CableNameStore.Set(name.Key,null);
        foreach(uint cable in CableRoutingStore.Snapshot())
            if(!__instance.TryGetCableState(cable,out _)) CableRoutingStore.Set(cable,false);
        CableRoutes.Invalidate();
    }

    private static void Unregister()
    {
        if (_network == null) return;
        _network.ServerManager.UnregisterBroadcast<Command>(ReceiveCommand);
        _network.ClientManager.UnregisterBroadcast<Snapshot>(ReceiveSnapshot);
        _network.ServerManager.UnregisterBroadcast<CableNameCommand>(ReceiveNameCommand);
        _network.ClientManager.UnregisterBroadcast<CableNameSnapshot>(ReceiveNameSnapshot);
    }

    public static void Remove()
    {
        Unregister(); _network=null; _remoteActive=false; NextCommand.Clear(); NextSnapshot.Clear();
        CableRoutes.Invalidate(); CableNameStore.Clear(); CableRoutingStore.Clear();
    }
}

public static class CableRoutingAccess
{
    public static bool OwnsHeldCable(PlayerState state, uint player, uint id, CableState cable)
    {
        return player != 0 && id != 0 && state.EntityId == id
            && (state.Type == PlayerStateType.WalkingHoldingCable || state.Type == PlayerStateType.CheatFlyingHoldingCable)
            && (cable.PlugA.IsHeldByPlayer && cable.PlugA.EntityId == player
                || cable.PlugB.IsHeldByPlayer && cable.PlugB.EntityId == player);
    }
}
