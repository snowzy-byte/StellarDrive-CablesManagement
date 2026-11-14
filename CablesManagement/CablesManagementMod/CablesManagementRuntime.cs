using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using UI.BuildMenu;
using UnityEngine;
using Object = UnityEngine.Object;
using BuildMenuRow = UI.BuildMenu.BuildMenuTabConfig.BuildMenuRow;

using Core.Services;
using Players;
using Players.LocalClient;
using Players.Services;

namespace CablesManagementMod;

internal static class GameRefs
{
    internal static PlayersServerTracker PlayersServer => Service<PlayersServerTracker>();

    internal static T Service<T>() where T : class
    {
        try
        {
            T service = ServiceLocator.GetService<T>();
            return service is Object unity && unity == null ? null : service;
        }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

internal static class PlayerAccess
{
    internal static TrackedPlayerServer LocalServerPlayer
    {
        get
        {
            var players = GameRefs.PlayersServer;
            if (players == null) return null;
            var local = GameRefs.Service<PlayersClientTracker>()?.LocalPlayer;
            return local != null ? players.ActivePlayers.FirstOrDefault(player => player.Id == local.Id)
                : players.ActivePlayers.FirstOrDefault(player => player.IsHost);
        }
    }
}

internal static class ModalInput
{
    private static readonly FieldInfo Paused = AccessTools.Field(typeof(PlayerControlsActivator), "_pauseIsActive");
    private static PlayerControlsActivator _controls;
    private static int _holders;
    private static bool _suppressed, _restorePause, _restoreVisible;
    private static CursorLockMode _restoreLock;
    internal static bool Held => _holders != 0;

    internal static void Capture()
    {
        if (_holders++ != 0) return;
        _restoreLock = Cursor.lockState;
        _restoreVisible = Cursor.visible;
        Suppress();
    }

    private static void Suppress()
    {
        if (_suppressed) return;
        _controls = Object.FindFirstObjectByType<PlayerControlsActivator>();
        if (_controls == null) return;
        if (Paused == null || Paused.FieldType != typeof(bool))
            throw new MissingFieldException(typeof(PlayerControlsActivator).FullName, "_pauseIsActive");
        _restorePause = (bool)Paused.GetValue(_controls);
        _controls.OnPauseChange(true);
        _suppressed = true;
    }

    internal static void Hold()
    {
        if (!Held) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Suppress();
    }

    internal static void Release()
    {
        if (_holders == 0 || --_holders != 0) return;
        try { if (_suppressed && _controls != null) _controls.OnPauseChange(_restorePause); }
        finally
        {
            _controls = null;
            _suppressed = false;
            bool menuOpen = CablesManagementControls.MenuOpen;
            Cursor.lockState = menuOpen ? CursorLockMode.None : _restoreLock;
            Cursor.visible = menuOpen || _restoreVisible;
        }
    }

    internal static void ReleaseAll()
    {
        while (_holders != 0) Release();
    }
}

internal sealed class CablePartDefinition
{
    internal ushort Id;
    internal string Name;
    internal string Donor;
    internal int BuildRow;
    internal Action<PartSettings, GameObject> Configure;
}

internal static class CableParts
{
    private static readonly List<CablePartDefinition> Definitions = new List<CablePartDefinition>();
    private static readonly Dictionary<ushort, PartSettings> Parts = new Dictionary<ushort, PartSettings>();
    private sealed class MenuBinding
    {
        internal BuildMenuTabConfig Tab;
    }

    private static ConditionalWeakTable<BuildMenu, MenuBinding> Menus = new ConditionalWeakTable<BuildMenu, MenuBinding>();
    private static readonly WeakObjectSet<BuildMenu> LiveMenus = new WeakObjectSet<BuildMenu>();
    private static readonly WeakObjectSet<PartSettingsProvider> Providers = new WeakObjectSet<PartSettingsProvider>();
    private static readonly HashSet<ushort> MissingReported = new HashSet<ushort>();
    private static FieldInfo _partsField, _tabsField;
    private static GameObject _holder;
    private static bool _building, _notifying, _installed;
    private static Action<string> _log;
    internal static Action PartsReady;
    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        if (_installed)
            return;
        _partsField = AccessTools.Field(typeof(PartSettingsProvider), "_partsDict");
        _tabsField = AccessTools.Field(typeof(BuildMenu), "tabsConfig");
        MethodInfo settings = AccessTools.DeclaredMethod(typeof(PartSettingsProvider), "EnsureDictIsCached", Type.EmptyTypes);
        MethodInfo menu = AccessTools.DeclaredMethod(typeof(BuildMenu), "Start", Type.EmptyTypes);
        if (_partsField?.FieldType != typeof(Dictionary<ushort, PartSettings>) || _tabsField?.FieldType != typeof(BuildMenuTabConfig[]) || settings == null || menu == null)
            throw new MissingMemberException("The native part registry/build menu changed; CablesManagement registration was not installed.");
        _log = log;
        harmony.Patch(settings, prefix: new HarmonyMethod(typeof(CableParts), nameof(BeforeSettingsCache)), postfix: new HarmonyMethod(typeof(CableParts), nameof(AfterSettingsCache)));
        harmony.Patch(menu, prefix: new HarmonyMethod(typeof(CableParts), nameof(BeforeBuildMenu)) { priority = Priority.Last });
        _installed = true;
    }

    internal static void Register(CablePartDefinition definition)
    {
        if (definition == null || definition.Id < 8890 || definition.Id > 8898 || string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Donor) || definition.BuildRow < 0 || definition.Configure == null)
            throw new ArgumentException("Invalid CablesManagement part definition.", nameof(definition));
        if (Definitions.Any(d => d.Id == definition.Id || d.Name == definition.Name))
            throw new InvalidOperationException("Duplicate CablesManagement part ID/name: " + definition.Id);
        Definitions.Add(definition);
    }

    internal static PartSettings Get(ushort id)
    {
        EnsureAll();
        Parts.TryGetValue(id, out PartSettings settings);
        return settings;
    }

    private static void EnsureAll()
    {
        if (_building || Definitions.Count == 0 || Parts.Count == Definitions.Count)
            return;
        bool changed = false;
        _building = true;
        try
        {
            PartSettings[] loaded = Resources.FindObjectsOfTypeAll<PartSettings>();
            foreach (CablePartDefinition definition in Definitions)
                if (!Parts.ContainsKey(definition.Id) && loaded.Any(p => p && p.id == definition.Id))
                    throw new InvalidOperationException("CablesManagement part ID " + definition.Id + " is already in use.");
            foreach (CablePartDefinition definition in Definitions)
            {
                if (Parts.ContainsKey(definition.Id))
                    continue;
                PartSettings donor = loaded.FirstOrDefault(p => p && p.name == definition.Donor && p.part);
                if (!donor)
                {
                    if (MissingReported.Add(definition.Id))
                        _log?.Invoke("CablesManagement is waiting for donor " + definition.Donor + " (part " + definition.Id + ").");
                    continue;
                }

                Parts.Add(definition.Id, CreatePart(definition, donor));
                MissingReported.Remove(definition.Id);
                changed = true;
            }
        }
        finally
        {
            _building = false;
        }

        if (changed && !_notifying)
        {
            _notifying = true;
            try
            {
                PartsReady?.Invoke();
            }
            finally
            {
                _notifying = false;
            }
        }
    }

    private static PartSettings CreatePart(CablePartDefinition definition, PartSettings donor)
    {
        if (!_holder)
        {
            _holder = new GameObject("CablesManagement_PartTemplates")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _holder.transform.position = new Vector3(0f, -100000f, 0f);
            Object.DontDestroyOnLoad(_holder);
        }

        PartSettings settings = null;
        GameObject prefab = null;
        try
        {
            prefab = Object.Instantiate(donor.part, _holder.transform, false);
            prefab.name = definition.Name;
            prefab.hideFlags = HideFlags.HideAndDontSave;
            prefab.transform.localPosition = Vector3.zero;
            prefab.transform.localRotation = Quaternion.identity;
            settings = Object.Instantiate(donor);
            settings.name = definition.Name;
            settings.hideFlags = HideFlags.DontSave;
            settings.id = definition.Id;
            settings.part = prefab;
            definition.Configure(settings, prefab);
            if (settings.id != definition.Id || settings.name != definition.Name || !ReferenceEquals(settings.part, prefab))
                throw new InvalidOperationException("CablesManagement part identity changed during configuration: " + definition.Id);
            return settings;
        }
        catch
        {
            if (settings)
                Object.DestroyImmediate(settings);
            if (prefab)
                Object.DestroyImmediate(prefab);
            throw;
        }
    }

    private static void BeforeSettingsCache() => EnsureAll();
    private static void AfterSettingsCache(PartSettingsProvider __instance)
    {
        var current = (Dictionary<ushort, PartSettings>)_partsField.GetValue(__instance);
        if (current == null || Parts.Count == 0)
            return;
        Providers.Add(__instance);
        Dictionary<ushort, PartSettings> merged = null;
        foreach (var part in Parts)
        {
            if (current.TryGetValue(part.Key, out PartSettings existing))
            {
                if (!ReferenceEquals(existing, part.Value))
                    throw new InvalidOperationException("A different part replaced CablesManagement ID " + part.Key);
                continue;
            }

            if (merged == null)
                merged = new Dictionary<ushort, PartSettings>(current);
            merged.Add(part.Key, part.Value);
        }

        if (merged != null)
            _partsField.SetValue(__instance, merged);
    }

    private static void BeforeBuildMenu(BuildMenu __instance)
    {
        EnsureAll();
        var tabs = (BuildMenuTabConfig[])_tabsField.GetValue(__instance);
        if (tabs == null)
            return;
        Menus.TryGetValue(__instance, out MenuBinding binding);
        BuildMenuTabConfig previous = binding?.Tab;
        var others = tabs.Where(t => !ReferenceEquals(t, previous)).ToList();
        var rows = Definitions.OrderBy(d => d.BuildRow).ThenBy(d => d.Id).Where(d => Parts.ContainsKey(d.Id)).GroupBy(d => d.BuildRow).Select(g => new BuildMenuRow { parts = g.Select(d => Parts[d.Id]).ToArray() }).ToArray();
        if (rows.Length == 0)
            return;
        var ours = new BuildMenuTabConfig
        {
            name = "Cables Management",
            icon = others.Select(t => t?.icon).FirstOrDefault(icon => icon),
            rows = rows
        };
        others.Add(ours);
        Menus.Remove(__instance);
        Menus.Add(__instance, new MenuBinding { Tab = ours });
        LiveMenus.Add(__instance);
        _tabsField.SetValue(__instance, others.ToArray());
    }

    internal static void Release()
    {
        foreach (BuildMenu menu in LiveMenus.Live)
            if (menu && Menus.TryGetValue(menu, out MenuBinding binding) && _tabsField.GetValue(menu)is BuildMenuTabConfig[] tabs)
                _tabsField.SetValue(menu, tabs.Where(t => !ReferenceEquals(t, binding.Tab)).ToArray());
        Menus = new ConditionalWeakTable<BuildMenu, MenuBinding>();
        LiveMenus.Clear();
        foreach (PartSettingsProvider provider in Providers.Live)
            if (_partsField.GetValue(provider)is Dictionary<ushort, PartSettings> settings)
                foreach (var part in Parts)
                    if (settings.TryGetValue(part.Key, out PartSettings existing) && ReferenceEquals(existing, part.Value))
                        settings.Remove(part.Key);
        Providers.Clear();
        foreach (PartSettings settings in Parts.Values)
            if (settings)
                Object.Destroy(settings);
        Parts.Clear();
        Definitions.Clear();
        PartsReady = null;
        MissingReported.Clear();
        if (_holder)
            Object.Destroy(_holder);
        _holder = null;
        _log = null;
    }
}

internal sealed class WeakObjectSet<T>
    where T : class
{
    private ConditionalWeakTable<T, object> _seen = new ConditionalWeakTable<T, object>();
    private readonly List<WeakReference<T>> _references = new List<WeakReference<T>>();
    public void Add(T target)
    {
        if (_seen.TryGetValue(target, out _))
            return;
        _seen.Add(target, new object ());
        _references.RemoveAll(reference => !reference.TryGetTarget(out _));
        _references.Add(new WeakReference<T>(target));
    }

    public IEnumerable<T> Live
    {
        get
        {
            foreach (WeakReference<T> reference in _references)
                if (reference.TryGetTarget(out T target))
                    yield return target;
        }
    }

    public void Clear()
    {
        _seen = new ConditionalWeakTable<T, object>();
        _references.Clear();
    }
}
