using System;
using System.Reflection;
using Alta;
using Alta.Caves;
using Alta.Impact;
using Alta.Inventory;
using Alta.Networking;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EnoughIron
{
    internal static class MetalBowInstances
    {
        internal const uint PrefabHash = 25100;
        private const uint ItemHash = 64508;
        private const string BeltSide = "Belt Side";
        private static readonly Item.DockPositioning sideSlotPose =
            new Item.DockPositioning(Vector3.zero, new Vector3(0f, -180f, 0f));

        private static readonly FieldInfo entityBehaviours = Require<MonoBehaviour[]>(typeof(NetworkEntity), "networkEntityBehaviours");
        private static readonly FieldInfo partEntity = Require<NetworkEntity>(typeof(NetworkEntityBehaviour), "entity");
        private static readonly FieldInfo material = Require<PhysicalMaterial>(typeof(PhysicalMaterialPart), "physicalMaterial");
        private static readonly FieldInfo distribution = Require<Distribution>(typeof(PhysicalMaterialPart), "materialDistribution");
        private static readonly FieldInfo channel = Require<PhysicalMaterialChannel>(typeof(PhysicalMaterialPart), "materialChannel");
        private static readonly FieldInfo renderers = Require<Renderer[]>(typeof(PhysicalMaterialPart), "renderers");
        private static readonly FieldInfo impactors = Require<ImpactGenerator[]>(typeof(PhysicalMaterialPart), "impactors");
        private static readonly FieldInfo listeners = Require<MonoBehaviour[]>(typeof(PhysicalMaterialPart), "listenToPhysicalMaterial");

        private static PhysicalMaterial iron;
        private static Distribution metalDistribution;
        private static bool reportedInstallation;
        private static bool reportedFailure;

        // Validate the reflected fields before installing the spawn patch.
        static MetalBowInstances() { }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            MetalBowBehaviour.ValidateSchema();
            var initialize = AccessTools.Method(typeof(NetworkEntity), nameof(NetworkEntity.Initialize), new[]
            {
                typeof(INetworkScene), typeof(NetworkEntity), typeof(uint), typeof(uint[]), typeof(bool)
            });
            if (initialize == null)
                throw new MissingMethodException(typeof(NetworkEntity).FullName, nameof(NetworkEntity.Initialize));

            var generate = AccessTools.DeclaredMethod(typeof(PhysicalMaterialPart), "Generate", new[] { typeof(SpawnData) });
            if (generate == null)
                throw new MissingMethodException(typeof(PhysicalMaterialPart).FullName, "Generate");

            var checkDock = AccessTools.DeclaredMethod(typeof(PickupDock), nameof(PickupDock.CheckAllowed), new[]
            {
                typeof(Interactor), typeof(Pickup), typeof(int), typeof(PickupDock).MakeByRefType()
            });
            if (checkDock == null)
                throw new MissingMethodException(typeof(PickupDock).FullName, nameof(PickupDock.CheckAllowed));

            var getPose = AccessTools.DeclaredMethod(typeof(Item), nameof(Item.GetPositioning),
                new[] { typeof(PickupDockSettings), typeof(bool), typeof(bool) });
            if (getPose == null)
                throw new MissingMethodException(typeof(Item).FullName, nameof(Item.GetPositioning));

            harmony.Patch(initialize, prefix: new HarmonyMethod(typeof(MetalBowInstances), nameof(BeforeInitialize)));
            harmony.Patch(generate, prefix: new HarmonyMethod(typeof(MetalBowInstances), nameof(BeforeMaterialGeneration)));
            harmony.Patch(checkDock, postfix: new HarmonyMethod(typeof(MetalBowInstances), nameof(AllowBowInSideSlot)));
            harmony.Patch(getPose, postfix: new HarmonyMethod(typeof(MetalBowInstances), nameof(UseSideSlotPose)));
        }

        private static void AllowBowInSideSlot(PickupDock __instance, Pickup __1, ref PickupDock.AllowedResult __result)
        {
            // The native size check runs after the tag, occupancy, and pickup filters.
            if (__result == PickupDock.AllowedResult.CraftingSizeInvalid && IsBeltSide(__instance.Settings)
                && __1 != null && __1.Entity != null && IsStockBow(__1.Entity))
                __result = PickupDock.AllowedResult.Ok;
        }

        private static void UseSideSlotPose(Item __instance, PickupDockSettings __0, bool __1, ref Item.DockPositioning __result)
        {
            if (__instance.Hash == ItemHash && !__1 && IsBeltSide(__0))
                __result = sideSlotPose;
        }

        private static bool IsBeltSide(PickupDockSettings settings)
        {
            return settings != null && settings.name == BeltSide;
        }

        private static bool BeforeMaterialGeneration(PhysicalMaterialPart __instance, SpawnData __0)
        {
            // Manual spawns start as Iron. Loot with generation attributes uses
            // the native metal distribution, and spawn arguments can set a metal.
            return __0?.Attributes != null || __instance.GetComponent<MetalBowBehaviour>() == null;
        }

        private static void BeforeInitialize(NetworkEntity __instance)
        {
            if (__instance.IsInitialized || !IsStockBow(__instance))
                return;

            try
            {
                if (iron == null)
                    iron = HashedGeneralValue<PhysicalMaterial>.Get(MetalBowBehaviour.IronHash);
                if (metalDistribution == null)
                    metalDistribution = Resources.Load<Distribution>("distributions/Metal Material Distribution");
                AddMaterialPart(__instance, iron, metalDistribution);

                if (!reportedInstallation)
                {
                    reportedInstallation = true;
                    MelonLogger.Msg("ENOUGH_IRON_BOWS_READY prefab=25100 iron=1 sideSlots=enabled");
                }
            }
            catch (Exception exception)
            {
                if (!reportedFailure)
                {
                    reportedFailure = true;
                    MelonLogger.Error("ENOUGH_IRON_BOWS_SETUP_FAILED " + exception);
                }
            }
        }

        internal static bool IsStockBow(NetworkEntity entity)
        {
            var prefab = entity.Prefab;
            var pickup = entity.CommonPickup;
            return prefab != null && prefab.Entity == entity && prefab.Hash == PrefabHash
                && pickup != null && pickup.Item != null && pickup.Item.Hash == ItemHash;
        }

        internal static void AddMaterialPart(NetworkEntity entity, PhysicalMaterial defaultIron, Distribution metals)
        {
            var root = entity.gameObject;
            if (root.GetComponent<MetalBowBehaviour>() != null)
                return;
            if (root.GetComponent<PhysicalMaterialPart>() != null)
                throw new InvalidOperationException("A different mod already added a material part to this Metal Bow.");
            if (defaultIron == null || defaultIron.Hash != MetalBowBehaviour.IronHash || metals == null)
                throw new InvalidOperationException("Native Iron or Metal Material Distribution is missing.");
            float baseline = defaultIron.DamageMultiplier;
            if (baseline <= 0f || float.IsNaN(baseline) || float.IsInfinity(baseline))
                throw new InvalidOperationException("Native Iron damage is invalid.");

            var bow = root.GetComponent<Bow>();
            var stringNode = root.transform.Find("String");
            var bowString = stringNode != null ? stringNode.GetComponent<BowString>() : null;
            var originalBehaviours = entityBehaviours.GetValue(entity) as MonoBehaviour[];
            if (bow == null || bowString == null || bowString.Bow != bow || originalBehaviours == null)
                throw new InvalidOperationException("Native Metal Bow graph is incomplete.");

            var behaviour = root.AddComponent<MetalBowBehaviour>();
            PhysicalMaterialPart part = null;
            try
            {
                behaviour.Configure(bowString, defaultIron);
                part = root.AddComponent<PhysicalMaterialPart>();
                partEntity.SetValue(part, entity);
                material.SetValue(part, defaultIron);
                distribution.SetValue(part, metals);
                channel.SetValue(part, PhysicalMaterialChannel.A);
                renderers.SetValue(part, Array.Empty<Renderer>());
                impactors.SetValue(part, Array.Empty<ImpactGenerator>());
                listeners.SetValue(part, new MonoBehaviour[] { behaviour });

                // Keep every existing behaviour in order. The native material
                // part owns its save data and SyncMaterial message.
                var updated = new MonoBehaviour[originalBehaviours.Length + 1];
                Array.Copy(originalBehaviours, updated, originalBehaviours.Length);
                updated[updated.Length - 1] = part;
                entityBehaviours.SetValue(entity, updated);
            }
            catch
            {
                if (part != null)
                    UnityEngine.Object.Destroy(part);
                UnityEngine.Object.Destroy(behaviour);
                throw;
            }
        }

        private static FieldInfo Require<T>(Type type, string name)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.FieldType != typeof(T))
                throw new MissingFieldException(type.FullName, name);
            return field;
        }
    }
}
