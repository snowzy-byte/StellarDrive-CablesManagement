using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Players.Interface.Model;
using SDModKit.Game;
using Ships.Cables.VisualObjects;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Settings;
using Ships.Parts.Common;
using Ships.Parts.Common.Model.Reactions;
using UnityEngine;
using Items.Model;
using Research;
using Research.Interface.Model;
using Research.Interface.Values;
using SDModKit.Content;
using System.Collections;
using Ships;
using Ships.Interface.Model.State;
using Ships.Physics.Collision;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(CablesManagementMod.CablesManagementMod), "Cables Management", "1.1.0", "Ghost", null)]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]
[assembly: MelonPriority(100)]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace CablesManagementMod;

public sealed class CablesManagementMod : MelonMod
{
    public static readonly ushort[] PartIds = { 8890, 8891, 8892, 8893, 8894, 8895 };
    public static bool IsClip(ushort id) => id >= 8890 && id <= 8895;
    public static bool IsCross(ushort id) => id >= 8893 && id <= 8895;

    public override void OnInitializeMelon()
    {
        InstallHelpers();
        CablesManagementControls.Install(HarmonyInstance);
        CablesManagementProtection.Install(HarmonyInstance);
        CablesManagementResearch.Install(HarmonyInstance);
        CablesManagementSocketPositions.Install(HarmonyInstance);
        CablesManagementRouting.Install(HarmonyInstance);
        CablesManagementNativeMovement.Install(HarmonyInstance);
        CablesManagementPlacement.Install(HarmonyInstance);
        CablesManagementAlignment.Install(HarmonyInstance);
        CablesManagementKeyTip.Install(HarmonyInstance);
        CablePaintSaving.Install(HarmonyInstance);
        CablePaintAppearance.Install(HarmonyInstance);
        CablePaintNetwork.Install(HarmonyInstance);
        CablePaintMode.Install(HarmonyInstance);
        CablePaintKeyTips.Install(HarmonyInstance);
        CableRouteView.Install(HarmonyInstance);
        Register(8890, CompatibilityIds.SignalPart, "Cables Management - Signal", CableType.Signal);
        Register(8891, CompatibilityIds.PowerPart, "Cables Management - Power", CableType.Power);
        Register(8892, CompatibilityIds.PipePart, "Cables Management - Pipe", CableType.Fluid);
        Register(8893, "CablesManagementCrossSignal", "Cables Management - Signal Junction", CableType.Signal);
        Register(8894, "CablesManagementCrossPower", "Cables Management - Power Junction", CableType.Power);
        Register(8895, "CablesManagementCrossPipe", "Cables Management - Pipe Junction", CableType.Fluid);
    }

    public override void OnUpdate()
    {
        CablesManagementControls.Update();
        CablesManagementRouting.Update();
        CablesManagementAlignment.Update();
        CablePaintMode.Update();
        CablePaintPanel.Tick();
        CableRouteView.Update();
    }
    public override void OnDeinitializeMelon()
    {
        CableRouteView.Remove();
        CablePaintMode.Remove();
        CablePaintKeyTips.Remove();
        CablePaintNetwork.Remove();
        CablePaintAppearance.Remove();
        CablesManagementPicking.Remove();
        CablePaintStore.Clear();
        CablesManagementAlignment.Remove();
        CablesManagementSocketPositions.Remove();
        CablesManagementKeyTip.Remove();
        CablesManagementResearch.Remove();
        CablesManagementControls.Remove();
    }

    private static void Register(ushort id, string name, string label, CableType type)
    {
        CustomParts.Register(new CustomPartDefinition
        {
            Id = id, Name = name, Donor = "SignalSplitter", BuildTab = "Cables Management", BuildRow = IsCross(id) ? 1 : 0, AutoFinish = false,
            Configure = (settings, prefab) => Configure(settings, prefab, label, type)
        });
    }

    private static void InstallHelpers()
    {
        Assembly assembly = typeof(CustomParts).Assembly;
        foreach (string name in new[] { "BuildPreview", "GhostSet", "PartLookup", "BuildCost", "IconOverrides", "PartGeometry", "RecycleGuard" })
            assembly.GetType("SDModKit.Game." + name)?.GetMethod("Install",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(null, null);
    }

    private static void Configure(PartSettings settings, GameObject prefab, string label, CableType type)
    {
        bool cross = IsCross(settings.id);
        CablesManagementResearch.ConfigureCost(settings);
        SocketObject donor = prefab.GetComponentsInChildren<SocketObject>(true).FirstOrDefault(s => s.Type == CableType.Signal);
        if (donor == null || donor.interactive == null)
            throw new InvalidOperationException("SignalSplitter has no socket interaction template.");
        int socketLayer = donor.interactive.GetComponentInChildren<Collider>(true)?.gameObject.layer ?? donor.interactive.layer;
        Material material = prefab.GetComponentsInChildren<MeshRenderer>(true).SelectMany(r => r.sharedMaterials)
            .FirstOrDefault(m => m != null && m.shader != null && m.shader.name == "Custom/PlanetObject");
        if (material == null) throw new InvalidOperationException("SignalSplitter has no native PlanetObject surface material.");
        PartInteraction oldInteraction = prefab.GetComponentInChildren<PartInteraction>(true);
        float pickupDistance = oldInteraction != null ? oldInteraction.DetectionDistance : 2f;
        int interactionLayer = oldInteraction != null ? oldInteraction.gameObject.layer : socketLayer;
        var pickupLabels = oldInteraction?.Interactions?.Where(x => x.type == InteractionType.PickUp).ToList()
            ?? new List<PartInteractionConfig>();
        if (pickupLabels.Count == 0)
            throw new InvalidOperationException("SignalSplitter has no pickup interaction.");

        foreach (Transform child in prefab.transform.Cast<Transform>().ToArray())
            UnityEngine.Object.DestroyImmediate(child.gameObject);
        foreach (MonoBehaviour component in prefab.GetComponents<MonoBehaviour>())
            if (!(component is ShipPartBounds)) UnityEngine.Object.DestroyImmediate(component);
        foreach (Collider collider in prefab.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(collider);

        var visual = new GameObject("CablesManagementVisual") { layer = prefab.layer };
        visual.transform.SetParent(prefab.transform, false);
        visual.AddComponent<CablesManagementVisual>();
        GameObject model = cross ? CablesManagementModel.CreateCross(material) : CablesManagementModel.Create(material, "CablesManagementModel");
        model.transform.SetParent(visual.transform, false);
        CablesManagementBadge.Create(visual.transform, material, type, cross);
        CablesManagementBridge.Create(visual.transform, material, type, cross);
        foreach (Transform node in visual.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = prefab.layer;

        var sockets = new SocketObject[cross ? 4 : 2];
        for (byte id = 0; id < sockets.Length; id++) sockets[id] = CreateSocket(prefab.transform, id, type, socketLayer, cross);
        foreach (SocketObject socket in sockets)
            Traverse.Create(socket).Field("relayedSockets").SetValue(CablesManagementLayout.RelayedPorts(socket.Id, cross).Select(id => sockets[id]).ToArray());

        var interactions = new GameObject("CablesManagementInteractions") { layer = interactionLayer, tag = "Interactable" };
        interactions.transform.SetParent(prefab.transform, false);
        interactions.AddComponent<DefaultShipPartInteractions>();
        var pickup = interactions.AddComponent<PartInteraction>();
        Traverse.Create(pickup).Field("interactions").SetValue(pickupLabels);
        Traverse.Create(pickup).Field("detectionDistance").SetValue(pickupDistance);
        interactions.AddComponent<CablesManagementClipPicking>();

        CablesManagementSocketInteraction.ConfigurePickup(interactions, cross);

        settings.fullLabel = label;
        settings.description = cross
            ? "Four-way cable crossing with two independent paths. Each socket connects only to the opposite socket. Indestructible. 1 kg. 1 Iron."
            : "Indestructible straight-through cable guide. Connect one segment to each end; rotate the clip to guide the route. 1 kg. 1 Iron.";
        settings.localizedDescription = null;
        settings.mass = 1f;
        settings.snappingStyle = SnappingStyle.PreciseOnWall;
        settings.size = cross ? CablesManagementLayout.CrossBoundsSize : CablesManagementLayout.BoundsSize;
        settings.thumbnailRotation = Quaternion.Euler(10f, -25f, 0f);

        settings.customCreationClass = null;
        var bounds = prefab.GetComponent<ShipPartBounds>() ?? prefab.AddComponent<ShipPartBounds>();
        bounds.center = CablesManagementLayout.BoundsCenter;
        bounds.bounds = settings.size;
        if (settings.GetSockets().Count != sockets.Length || visual.GetComponentsInChildren<Collider>(true).Length != 0)
            throw new InvalidOperationException("Cables Management prefab validation failed.");
    }

    private static SocketObject CreateSocket(Transform parent, byte id, CableType type, int layer, bool cross)
    {
        var root = new GameObject("CablesManagementSocket_" + id) { layer = layer };
        root.transform.SetParent(parent, false);
        root.transform.localPosition = cross ? CablesManagementLayout.CrossPort(id) : CablesManagementLayout.Port(id);
        root.transform.localRotation = Quaternion.LookRotation(cross ? CablesManagementLayout.CrossInward(id) : CablesManagementLayout.Inward(id), Vector3.back);
        var socket = root.AddComponent<SocketObject>();
        Traverse.Create(socket).Field("id").SetValue(id);
        Traverse.Create(socket).Field("type").SetValue(type);
        CablesManagementSocketPositions.Track(socket, root.transform.localPosition);
        Traverse.Create(socket).Field("relayedSockets").SetValue(Array.Empty<SocketObject>());
        socket.shouldCreateFluidBufferWhenTouchingAnotherSocket = type == CableType.Fluid;
        socket.visualObject = new GameObject("GuideEndpoint_" + id);
        socket.visualObject.transform.SetParent(root.transform, false);
        socket.interactive = new GameObject("GuideSocket_" + id) { layer = layer };
        socket.interactive.transform.SetParent(root.transform, false);
        CablesManagementSocketInteraction.Configure(socket.interactive, type, cross);
        return socket;
    }
}

public static class CablesManagementResearch
{
    public const ushort ResearchId = 8890;
    public const int BuildIronQuantity = 1;
    public const int ResearchIronQuantity = 6;
    private static readonly ushort[] ClipPartIds = CablesManagementMod.PartIds;
    private static readonly FieldInfo BuildingCost = AccessTools.Field(typeof(PartSettings), "buildingCost");
    private static readonly Dictionary<ResearchSettingsList, ResearchSettings> Registered = new Dictionary<ResearchSettingsList, ResearchSettings>();
    private static bool _registering;
    private static bool _reportedConflict;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        var clientPrefix = new HarmonyMethod(typeof(CablesManagementResearch), nameof(BeforeClientLookup));
        harmony.Patch(AccessTools.Method(typeof(ResearchClientProxy), "Awake"), prefix: clientPrefix);
        harmony.Patch(AccessTools.PropertyGetter(typeof(ResearchClientProxy), nameof(ResearchClientProxy.Researches)), prefix: clientPrefix);
        harmony.Patch(AccessTools.Method(typeof(ResearchClientProxy), nameof(ResearchClientProxy.IsPartUnlocked)), prefix: clientPrefix);
        harmony.Patch(AccessTools.Method(typeof(ResearchSettingsList), nameof(ResearchSettingsList.GetResearchSettingById)),
            prefix: new HarmonyMethod(typeof(CablesManagementResearch), nameof(BeforeResearchLookup)));
        harmony.Patch(AccessTools.Method(typeof(ResearchSettingsList), nameof(ResearchSettingsList.GetTechsRequiredForPart)),
            prefix: new HarmonyMethod(typeof(CablesManagementResearch), nameof(BeforeResearchLookup)));
    }

    public static void ConfigureCost(PartSettings settings)
    {
        if (BuildingCost == null)
            throw new MissingFieldException(typeof(PartSettings).FullName, "buildingCost");
        BuildingCost.SetValue(settings, new List<ItemInstance>(CreateCost(BuildIronQuantity)));
    }

    private static ItemInstance[] CreateCost(int quantity)
    {
        ItemSettings iron = ItemCatalog.ById(ItemsConstants.IronItemId);
        if (iron == null)
            iron = Resources.FindObjectsOfTypeAll<ItemSettings>().FirstOrDefault(item => item != null && item.id == ItemsConstants.IronItemId);
        if (iron == null)
            throw new InvalidOperationException("Cannot find Iron for the Cables Management cost.");
        return new[] { new ItemInstance(iron, quantity) };
    }

    private static void BeforeClientLookup(ResearchSettingsList ___researchSettingsList)
    {
        EnsureRegistered(___researchSettingsList);
    }

    private static void BeforeResearchLookup(ResearchSettingsList __instance)
    {
        EnsureRegistered(__instance);
    }

    private static void EnsureRegistered(ResearchSettingsList list)
    {
        if (list == null || list.list == null || _registering)
            return;
        if (Registered.TryGetValue(list, out ResearchSettings existing))
        {
            if (existing != null && list.list.Contains(existing))
                return;
            if (existing != null)
                UnityEngine.Object.Destroy(existing);
            Registered.Remove(list);
        }
        _registering = true;
        try
        {
            PartSettings[] parts = ClipPartIds.Select(id => CustomParts.Get(id)).ToArray();
            if (parts.Any(part => part == null))
                return;
            if (list.list.Any(research => research != null && research.id == ResearchId))
            {
                if (!_reportedConflict)
                {
                    MelonLogger.Warning("Cables Management research ID 8890 is already in use; research was not registered.");
                    _reportedConflict = true;
                }
                return;
            }
            ResearchSettings prerequisite = list.list.FirstOrDefault(research => research != null &&
                research.availableInDemo && !research.hideFromTechTree && research.unlockedParts != null &&
                research.unlockedParts.Any(unlocked => unlocked != null && unlocked.name == "SignalSplitter"));
            if (prerequisite == null)
                return;

            ResearchSettings research = ScriptableObject.CreateInstance<ResearchSettings>();
            research.name = "CablesManagementResearch";
            research.hideFlags = HideFlags.DontSave;
            research.id = ResearchId;
            research.techName = "Cables Management";
            research.unlockedParts = parts;
            research.unlockedFeatures = Array.Empty<ResearchUnlockableFeature>();
            research.unlockedGuideEntriesIds = Array.Empty<uint>();
            research.requirements = new[] { prerequisite };
            research.resourceCost = CreateCost(ResearchIronQuantity);
            research.positionInTechTree = FindPosition(list.list, prerequisite);
            research.hideFromTechTree = false;
            research.availableInDemo = true;
            research.exclusiveToDemo = false;
            list.list.Add(research);
            Registered[list] = research;
            ResearchLayout.Settle(list);
        }
        finally
        {
            _registering = false;
        }
    }

    private static Vector2Int FindPosition(IEnumerable<ResearchSettings> researches, ResearchSettings prerequisite)
    {
        ResearchSettings[] entries = researches.Where(research => research != null).ToArray();
        int row = prerequisite.positionInTechTree.y > 0 ? prerequisite.positionInTechTree.y - 1 : 1;
        Vector2Int position = new Vector2Int(prerequisite.positionInTechTree.x + 1, row);
        while (entries.Any(research => research.positionInTechTree == position))
            position.x++;
        return position;
    }

    public static void Remove()
    {
        foreach (var entry in Registered)
        {
            if (entry.Key != null && entry.Key.list != null)
                entry.Key.list.Remove(entry.Value);
            if (entry.Value != null)
                UnityEngine.Object.Destroy(entry.Value);
        }
        Registered.Clear();
        _reportedConflict = false;
    }
}

public static class CablesManagementProtection
{
    private static FieldInfo _isHull, _ship, _partId;

    public static void Install(HarmonyLib.Harmony harmony)
    {
        Type candidate = typeof(ShipCollisionDestruction).GetNestedType("DestroyCandidate", BindingFlags.NonPublic);
        if (candidate == null) throw new InvalidOperationException("Cables Management: collision candidate type was not found.");
        _isHull = AccessTools.Field(candidate, "IsHull");
        _ship = AccessTools.Field(candidate, "Ship");
        _partId = AccessTools.Field(candidate, "StatefulPartId");
        if (_isHull == null || _ship == null || _partId == null)
            throw new InvalidOperationException("Cables Management: collision candidate fields were not found.");
        harmony.Patch(AccessTools.Method(typeof(ShipCollisionDestruction), "FetchDestroyPartsCandidates"),
            postfix: new HarmonyMethod(typeof(CablesManagementProtection), nameof(AfterFetchCandidates)));
        harmony.Patch(AccessTools.Method(typeof(TrackedShipServer), nameof(TrackedShipServer.DestroyStatefulPart), new[] { typeof(ushort) }),
            prefix: new HarmonyMethod(typeof(CablesManagementProtection), nameof(BeforeDestroy)));
    }

    private static bool Protected(IShipStateRead ship, ushort partId) => ship != null
        && ship.TryGetStatefulPart(partId, out var part) && part.Settings != null && CablesManagementMod.IsClip(part.Settings.id);

    private static bool BeforeDestroy(IShipStateRead __instance, ushort partId) => !Protected(__instance, partId);

    private static void AfterFetchCandidates(IList ____destroyCandidates)
    {

        for (int i = ____destroyCandidates.Count - 1; i >= 0; i--)
        {
            object candidate = ____destroyCandidates[i];
            if (!(bool)_isHull.GetValue(candidate)
                && Protected(_ship.GetValue(candidate) as IShipStateRead, (ushort)_partId.GetValue(candidate)))
                ____destroyCandidates.RemoveAt(i);
        }
    }
}
