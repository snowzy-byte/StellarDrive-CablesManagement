using System;
using System.Collections.Generic;
using System.Linq;
using FishNet.Broadcast;
using SDModKit.Game;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Services;
using Ships.Services;
using UnityEngine;

namespace CablesManagementMod;

public static class CableRoutes
{
    public const int MaximumSegments = 2048;
    private delegate bool PartLookup(PartContext context, out StatefulPart part);
    private delegate bool ConnectionLookup(EntityPartSocket socket, out SocketConnection connection);
    private static CablesClientTracker _cachedProvider;
    private static uint _cachedSeed;
    private static float _cacheUntil;
    private static uint[] _cachedRoute = Array.Empty<uint>();

    public static uint[] Find(uint seed)
    {
        var cables = GameRefs.Service<CablesClientTracker>();
        var ships = GameRefs.Service<ShipsClientTracker>();
        if (cables == null || ships == null) return Array.Empty<uint>();
        if (_cachedProvider == cables && _cachedSeed == seed && Time.unscaledTime < _cacheUntil)
            return _cachedRoute;
        _cachedProvider = cables;
        _cachedSeed = seed;
        _cacheUntil = Time.unscaledTime + .2f;
        var connections = GameRefs.Service<ConnectionsClientTracker>();
        _cachedRoute = Find(seed, cables, ships.TryGetStatefulPart,
            connections == null ? null : new ConnectionLookup(connections.TryGetSocketPhysicalConnection), false);
        return _cachedRoute;
    }

    internal static uint[] FindServer(uint seed, bool named = false)
    {
        var cables = GameRefs.Service<CablesServerTracker>();
        var ships = GameRefs.Service<ShipsServerTracker>();
        var connections = GameRefs.Service<ConnectionsServerTracker>();
        return cables == null || ships == null ? Array.Empty<uint>() : Find(seed, cables, ships.TryGetStatefulPart,
            connections == null ? null : new ConnectionLookup(connections.TryGetSocketPhysicalConnection), false, named);
    }

    public static uint[] FindNamed(uint seed)
    {
        var cables = GameRefs.Service<CablesClientTracker>();
        var ships = GameRefs.Service<ShipsClientTracker>();
        var connections = GameRefs.Service<ConnectionsClientTracker>();
        return cables == null || ships == null ? Array.Empty<uint>() : Find(seed, cables, ships.TryGetStatefulPart,
            connections == null ? null : new ConnectionLookup(connections.TryGetSocketPhysicalConnection), false, true);
    }

    public static uint[] FindTrace(uint seed)
    {
        var cables = GameRefs.Service<CablesClientTracker>();
        var ships = GameRefs.Service<ShipsClientTracker>();
        var connections = GameRefs.Service<ConnectionsClientTracker>();
        return cables == null || ships == null ? Array.Empty<uint>() : Find(seed, cables, ships.TryGetStatefulPart,
            connections == null ? null : new ConnectionLookup(connections.TryGetSocketPhysicalConnection), true);
    }

    public static CableRouteLink[] TraceLinks(uint[] ids)
    {
        var cables = GameRefs.Service<CablesClientTracker>();
        var ships = GameRefs.Service<ShipsClientTracker>();
        var connections = GameRefs.Service<ConnectionsClientTracker>();
        if (ids == null || ids.Length > MaximumSegments || cables == null || ships == null) return Array.Empty<CableRouteLink>();
        ConnectionLookup physical = connections == null ? null : new ConnectionLookup(connections.TryGetSocketPhysicalConnection);
        var links = new HashSet<CableRouteLink>();
        Action<EntityPartSocket, EntityPartSocket> record = (a, b) =>
        {
            if (a != b && links.Count <= MaximumSegments * 8) links.Add(new CableRouteLink(a, b));
        };
        foreach (uint id in ids)
        {
            if (!cables.TryGetCableState(id, out CableState cable) || cable.PlugA.IsHeldByPlayer || cable.PlugB.IsHeldByPlayer) continue;
            Resolve(cable.PlugA.EntityPartSocket, id, cable.Type, cables, ships.TryGetStatefulPart, physical, true, record);
            Resolve(cable.PlugB.EntityPartSocket, id, cable.Type, cables, ships.TryGetStatefulPart, physical, true, record);
        }
        if (links.Count > MaximumSegments * 8) return Array.Empty<CableRouteLink>();
        var result = new CableRouteLink[links.Count];
        links.CopyTo(result);
        return result;
    }

    public static uint[] Preview(uint heldId, EntityPartSocket target)
    {
        var cables = GameRefs.Service<CablesClientTracker>();
        var ships = GameRefs.Service<ShipsClientTracker>();
        var connections = GameRefs.Service<ConnectionsClientTracker>();
        if (cables == null || ships == null || !cables.TryGetCableState(heldId, out CableState held)
            || held.PlugA.IsHeldByPlayer == held.PlugB.IsHeldByPlayer) return Array.Empty<uint>();
        CablePlugSocket origin = held.PlugA.IsHeldByPlayer ? held.PlugB : held.PlugA;
        var ids = new HashSet<uint>();
        ConnectionLookup physical = connections == null ? null : new ConnectionLookup(connections.TryGetSocketPhysicalConnection);
        foreach (var endpoint in new[] { origin.EntityPartSocket, target })
        {
            foreach (uint seed in Resolve(endpoint, heldId, held.Type, cables, ships.TryGetStatefulPart, physical, true))
            {
                foreach (uint id in Find(seed, cables, ships.TryGetStatefulPart, physical, false))
                    if (ids.Add(id) && ids.Count > MaximumSegments) return Array.Empty<uint>();
            }
        }
        var result = new uint[ids.Count];
        ids.CopyTo(result); Array.Sort(result);
        return result;
    }

    private static uint[] Find(uint seed, ICablesProvider cables, PartLookup parts, ConnectionLookup connections, bool trace, bool named = false)
    {
        if (seed == 0 || !cables.TryGetCableState(seed, out CableState first)
            || first.PlugA.IsHeldByPlayer || first.PlugB.IsHeldByPlayer) return Array.Empty<uint>();
        return Walk(seed, id =>
        {
            var next = new List<uint>(2);
            if (cables.TryGetCableState(id, out CableState cable) && cable.Type == first.Type
                && !cable.PlugA.IsHeldByPlayer && !cable.PlugB.IsHeldByPlayer)
            {
                Continue(cable.PlugA, id, first.Type, cables, parts, connections, trace, next, named);
                Continue(cable.PlugB, id, first.Type, cables, parts, connections, trace, next, named);
            }
            return next;
        });
    }

    private static void Continue(CablePlugSocket plug, uint current, CableType type, ICablesProvider cables,
        PartLookup parts, ConnectionLookup connections, bool trace, List<uint> next, bool named)
    {
        if (plug.IsHeldByPlayer) return;
        HashSet<uint> connected = Resolve(plug.EntityPartSocket, current, type, cables, parts, connections, trace, null, named);
        if (!trace && connected.Count > 1) return;
        next.AddRange(connected);
    }

    private static IEnumerable<EntityPartSocket> Outlets(EntityPartSocket entry, PartLookup parts, CableType type, bool trace, bool named)
    {
        if (!parts(entry.PartContext, out StatefulPart part)) yield break;
        bool clip = CablesManagementMod.IsClip(part.Settings.id);
        if (!clip && named)
        {
            var sockets = part.Settings.GetSockets();
            if (!sockets.Any(socket => socket.Id == entry.Socket.SocketId && socket.Type == type)) yield break;
            foreach (var socket in sockets)
                if (socket.Type == type && socket.Id != entry.Socket.SocketId)
                    yield return new EntityPartSocket(entry.EntityId, entry.Socket.PartId, socket.Id);
            yield break;
        }
        if (!clip && !trace) yield break;
        var relayed = part.GetRelayedSockets(entry.Socket.SocketId);
        if (relayed == null) yield break;
        foreach (PartSocket socket in relayed)
        {
            if (clip && (socket.PartId != entry.Socket.PartId || socket.SocketId != (byte)(entry.Socket.SocketId ^ 1))) continue;
            yield return new EntityPartSocket(entry.EntityId, socket.PartId, socket.SocketId);
        }
    }

    private static HashSet<uint> Resolve(EntityPartSocket entry, uint current, CableType type, ICablesProvider cables,
        PartLookup parts, ConnectionLookup connections, bool trace, Action<EntityPartSocket, EntityPartSocket> record = null, bool named = false)
    {
        var result = new HashSet<uint>();
        var visited = new HashSet<EntityPartSocket>();
        var pending = new Queue<EntityPartSocket>();
        foreach (var outlet in Outlets(entry, parts, type, trace, named))
        {
            record?.Invoke(entry, outlet);
            pending.Enqueue(outlet);
        }
        while (pending.Count > 0)
        {
            EntityPartSocket outlet = pending.Dequeue();
            if (!visited.Add(outlet)) continue;
            if (visited.Count > MaximumSegments * 4) return new HashSet<uint>();
            if (cables.TryGetCableId(outlet, out uint id))
            {
                if (id != 0 && id != current && cables.TryGetCableState(id, out CableState cable) && cable.Type == type
                    && !cable.PlugA.IsHeldByPlayer && !cable.PlugB.IsHeldByPlayer) result.Add(id);
                continue;
            }
            if (connections == null || !connections(outlet, out SocketConnection connection)
                || connection.Type != SocketConnectionType.Touching) continue;
            EntityPartSocket? other = connection.SocketA == outlet ? connection.SocketB
                : connection.SocketB == outlet ? connection.SocketA : null;
            if (!other.HasValue || other.Value == outlet) continue;
            record?.Invoke(outlet, other.Value);
            foreach (var linked in Outlets(other.Value, parts, type, trace, named))
            {
                record?.Invoke(other.Value, linked);
                pending.Enqueue(linked);
            }
        }
        return result;
    }

    public static uint[] Walk(uint seed, Func<uint, IReadOnlyCollection<uint>> neighbors)
    {
        if (seed == 0) return Array.Empty<uint>();
        var visited = new HashSet<uint>();
        var pending = new Queue<uint>();
        pending.Enqueue(seed);
        while (pending.Count > 0)
        {
            uint id = pending.Dequeue();
            if (id == 0 || !visited.Add(id)) continue;
            if (visited.Count > MaximumSegments) return Array.Empty<uint>();
            var connected = neighbors(id);
            if (connected == null) continue;
            foreach (uint next in connected) if (next != 0 && !visited.Contains(next)) pending.Enqueue(next);
        }
        var result = new uint[visited.Count];
        visited.CopyTo(result);
        Array.Sort(result);
        return result;
    }

    public static string Name(uint cable)
    {
        string name = CableNameStore.Get(cable);
        if (!string.IsNullOrEmpty(name)) return name;
        foreach (uint id in FindNamed(cable))
        {
            name = CableNameStore.Get(id);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return string.Empty;
    }
    public static bool RequestName(uint seed, string name) => CablePaintNetwork.SendName(seed, name);
    public static bool PaintRoute(uint seed, uint? rgb) => CablePaintNetwork.SendRoute(seed, rgb);
    public static void Invalidate()
    {
        _cachedProvider = null;
        _cachedSeed = 0;
        _cacheUntil = 0;
        _cachedRoute = Array.Empty<uint>();
    }
}

public readonly struct CableRouteLink : IEquatable<CableRouteLink>
{
    public readonly EntityPartSocket A, B;
    public CableRouteLink(EntityPartSocket a, EntityPartSocket b)
    {
        if (a.CompareTo(b) <= 0) { A = a; B = b; }
        else { A = b; B = a; }
    }
    public bool Equals(CableRouteLink other) => A == other.A && B == other.B;
    public override bool Equals(object value) => value is CableRouteLink other && Equals(other);
    public override int GetHashCode() => unchecked(A.GetHashCode() * 397 ^ B.GetHashCode());
}

public sealed class CableTraceSelection
{
    public uint Seed { get; private set; }
    public uint[] Segments { get; private set; } = Array.Empty<uint>();

    public uint[] Update(bool held, uint hovered, Func<uint, uint[]> find)
    {
        if (!held) { Clear(); return Segments; }
        if (Seed != 0 || hovered == 0) return Segments;
        uint[] route = find(hovered);
        if (route == null || route.Length == 0 || route.Length > CableRoutes.MaximumSegments) return Segments;
        Seed = hovered;
        Segments = (uint[])route.Clone();
        return Segments;
    }

    public uint[] Refresh(Func<uint, uint[]> find)
    {
        if (Seed == 0) return Segments;
        uint[] route = find(Seed);
        Segments = route == null || route.Length > CableRoutes.MaximumSegments ? Array.Empty<uint>() : (uint[])route.Clone();
        return Segments;
    }

    public void Clear()
    {
        Seed = 0;
        Segments = Array.Empty<uint>();
    }
}

public struct CableNameCommand : IBroadcast
{
    public uint Cable;
    public string Name;
}

public struct CableNameSnapshot : IBroadcast
{
    public uint Cable;
    public string Name;
    public byte Kind;
}
