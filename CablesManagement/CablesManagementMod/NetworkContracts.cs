using FishNet.Broadcast;

namespace CableClipMod;

public static class CablePaintNetwork
{
    public struct Command : IBroadcast { public uint Cable, Rgb; public byte Kind; }
    public struct Snapshot : IBroadcast { public uint Cable, Rgb; public byte Kind; }
}
