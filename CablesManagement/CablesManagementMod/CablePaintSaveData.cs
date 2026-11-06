using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Saving.Serialization.V12.Cables;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using Saving.Services.Converters;

namespace CablesManagementMod;

public static class CablePaintSaveData
{
    public const string PropertyName = CompatibilityIds.PaintProperty;
    public const string NameProperty = "cableManagementName";
    public const string CurvedProperty = "cableManagementCurved";
    private sealed class Box { public string Value, Name; public bool Curved; }
    private static readonly ConditionalWeakTable<SerializableCable, Box> Values = new ConditionalWeakTable<SerializableCable, Box>();

    public static void Attach(SerializableCable cable, uint rgb)
    {
        if (cable != null && rgb <= 0xFFFFFF) Values.GetValue(cable, _ => new Box()).Value = CablePaintStore.Hex(rgb);
    }

    public static bool TryGet(SerializableCable cable, out uint rgb)
    {
        rgb = 0;
        return cable != null && Values.TryGetValue(cable, out Box box) && CablePaintStore.TryHex(box.Value, out rgb);
    }

    public static void AttachName(SerializableCable cable, string name)
    {
        if (cable != null) Values.GetValue(cable, _ => new Box()).Name = CableNameStore.Normalize(name);
    }

    public static bool TryGetName(SerializableCable cable, out string name)
    {
        name = null;
        if (cable == null || !Values.TryGetValue(cable, out Box box) || string.IsNullOrEmpty(box.Name)) return false;
        name = box.Name;
        return true;
    }

    public static void AttachCurved(SerializableCable cable, bool curved)
    {
        if (cable != null) Values.GetValue(cable, _ => new Box()).Curved = curved;
    }

    public static bool IsCurved(SerializableCable cable) => cable != null && Values.TryGetValue(cable, out Box box) && box.Curved;

    public static void ExtendContract(Type type, IList<JsonProperty> properties)
    {
        if (type != typeof(SerializableCable)) return;
        if (!properties.Any(property => property.PropertyName == PropertyName)) properties.Add(new JsonProperty
        {
            PropertyName = PropertyName, PropertyType = typeof(string), DeclaringType = type,
            Readable = true, Writable = true, NullValueHandling = NullValueHandling.Ignore,
            ValueProvider = new PaintValue(),
            ShouldSerialize = value => TryGet(value as SerializableCable, out _)
        });
        if (!properties.Any(property => property.PropertyName == NameProperty)) properties.Add(new JsonProperty
        {
            PropertyName = NameProperty, PropertyType = typeof(string), DeclaringType = type,
            Readable = true, Writable = true, NullValueHandling = NullValueHandling.Ignore,
            ValueProvider = new NameValue(),
            ShouldSerialize = value => TryGetName(value as SerializableCable, out _)
        });
        if (!properties.Any(property => property.PropertyName == CurvedProperty)) properties.Add(new JsonProperty
        {
            PropertyName = CurvedProperty, PropertyType = typeof(bool), DeclaringType = type,
            Readable = true, Writable = true, ValueProvider = new CurvedValue(),
            ShouldSerialize = value => IsCurved(value as SerializableCable)
        });
    }

    private sealed class PaintValue : IValueProvider
    {
        public object GetValue(object target) => TryGet(target as SerializableCable, out uint rgb) ? CablePaintStore.Hex(rgb) : null;
        public void SetValue(object target, object value)
        {
            if (target is SerializableCable cable && value is string text && CablePaintStore.TryHex(text, out uint rgb))
                Attach(cable, rgb);
        }
    }

    private sealed class NameValue : IValueProvider
    {
        public object GetValue(object target) => TryGetName(target as SerializableCable, out string name) ? name : null;
        public void SetValue(object target, object value)
        {
            if (target is SerializableCable cable && value is string name) AttachName(cable, name);
        }
    }

    private sealed class CurvedValue : IValueProvider
    {
        public object GetValue(object target) => IsCurved(target as SerializableCable);
        public void SetValue(object target, object value)
        {
            if (target is SerializableCable cable && value is bool curved) AttachCurved(cable, curved);
        }
    }
}

public static class CableRoutingStore
{
    private static readonly HashSet<uint> Curved = new HashSet<uint>();
    public static uint Revision { get; private set; }
    public static bool IsCurved(uint id) => Curved.Contains(id);
    public static uint[] Snapshot() => Curved.ToArray();

    public static bool Set(uint id, bool curved)
    {
        if (id == 0 || !(curved ? Curved.Add(id) : Curved.Remove(id))) return false;
        Revision++;
        return true;
    }

    public static void Clear() { Curved.Clear(); Revision++; }
}

public static class CablePaintStore
{
    private static readonly Dictionary<uint, uint> Colors = new Dictionary<uint, uint>();
    public static uint Revision { get; private set; }
    public static bool TryGet(uint id, out uint rgb) => Colors.TryGetValue(id, out rgb);
    public static KeyValuePair<uint, uint>[] Snapshot() => Colors.ToArray();

    public static bool Set(uint id, uint? rgb)
    {
        if (id == 0 || rgb.HasValue && rgb.Value > 0xFFFFFF) return false;
        if (rgb.HasValue)
        {
            if (Colors.TryGetValue(id, out uint current) && current == rgb.Value) return false;
            Colors[id] = rgb.Value;
        }
        else if (!Colors.Remove(id)) return false;
        Revision++;
        return true;
    }

    public static void Clear() { Colors.Clear(); Revision++; }
    public static string Hex(uint rgb) => rgb.ToString("X6", CultureInfo.InvariantCulture);
    public static bool TryHex(string text, out uint rgb)
    {
        rgb = 0;
        if (text == null) return false;
        text = text.Trim();
        if (text.StartsWith("#")) text = text.Substring(1);
        return text.Length == 6 && uint.TryParse(text, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out rgb) && rgb <= 0xFFFFFF;
    }
}

public static class CableNameStore
{
    public const int MaximumLength = 64;
    private static readonly Dictionary<uint, string> Names = new Dictionary<uint, string>();
    public static uint Revision { get; private set; }
    public static string Get(uint id) => Names.TryGetValue(id, out string name) ? name : string.Empty;
    public static KeyValuePair<uint, string>[] Snapshot() => Names.ToArray();

    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var result = new StringBuilder(MaximumLength);
        foreach (char character in name.Trim())
        {
            if (result.Length >= MaximumLength) break;
            if (!char.IsControl(character) && !char.IsSurrogate(character) && character != '<' && character != '>')
                result.Append(character);
        }
        return result.ToString().Trim();
    }

    public static bool Set(uint id, string name)
    {
        if (id == 0) return false;
        name = Normalize(name);
        if (name.Length == 0)
        {
            if (!Names.Remove(id)) return false;
        }
        else
        {
            if (Names.TryGetValue(id, out string current) && current == name) return false;
            Names[id] = name;
        }
        Revision++;
        return true;
    }

    public static void Clear() { Names.Clear(); Revision++; }
}

public static class CablePaintSaving
{
    public static void Install(HarmonyLib.Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(DefaultContractResolver), "CreateProperties"),
            postfix: new HarmonyMethod(typeof(CablePaintSaving), nameof(AfterProperties)));
        harmony.Patch(AccessTools.Method(typeof(CablesConverter), nameof(CablesConverter.ConvertToSerializable)),
            postfix: new HarmonyMethod(typeof(CablePaintSaving), nameof(AfterSerialize)));
        harmony.Patch(AccessTools.Method(typeof(CablesConverter), nameof(CablesConverter.ConvertToState)),
            prefix: new HarmonyMethod(typeof(CablePaintSaving), nameof(BeforeLoad)));
    }

    private static void AfterProperties(Type type, IList<JsonProperty> __result) => CablePaintSaveData.ExtendContract(type, __result);

    private static void AfterSerialize(SerializableCable[] __result)
    {
        foreach (SerializableCable cable in __result)
        {
            if (CablePaintStore.TryGet(cable.id, out uint rgb)) CablePaintSaveData.Attach(cable, rgb);
            CablePaintSaveData.AttachName(cable, CableNameStore.Get(cable.id));
            CablePaintSaveData.AttachCurved(cable, CableRoutingStore.IsCurved(cable.id));
        }
    }

    private static void BeforeLoad(SerializableCable[] serialized)
    {
        CablePaintStore.Clear();
        CableNameStore.Clear();
        CableRoutingStore.Clear();
        CableRoutes.Invalidate();
        foreach (SerializableCable cable in serialized)
        {
            if (CablePaintSaveData.TryGet(cable, out uint rgb)) CablePaintStore.Set(cable.id, rgb);
            if (CablePaintSaveData.TryGetName(cable, out string name)) CableNameStore.Set(cable.id, name);
            CableRoutingStore.Set(cable.id, CablePaintSaveData.IsCurved(cable));
        }
    }
}
