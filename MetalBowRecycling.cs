using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Alta;
using Alta.Blacksmithing;
using Alta.Inventory;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EnoughIron
{
    internal static class MetalBowRecycling
    {
        internal const int RecoveredIngots = 4;
        private const uint BowItemHash = 64508;
        private const uint SmelterPrefabHash = 44646;
        // The low 16 bits identify the output ingot. Native saves store this ID.
        private const uint RecipeHashPrefix = 0x454A0000;

        private static readonly FieldInfo hash = Require<int>(typeof(HashedGeneralValue), "hash");
        private static readonly FieldInfo catalogue = Require<Dictionary<uint, SmeltingRecipe>>(typeof(HashedGeneralValue<SmeltingRecipe>), "items");
        private static readonly FieldInfo recipeInput = Require<ItemCount[]>(typeof(SmeltingRecipe), "input");
        private static readonly FieldInfo recipeOutput = Require<ItemCount[]>(typeof(SmeltingRecipe), "output");
        private static readonly FieldInfo duration = Require<float>(typeof(SmeltingRecipe), "duration");
        private static readonly FieldInfo countedItem = Require<Item>(typeof(ItemCount), "item");
        private static readonly FieldInfo itemCount = Require<int>(typeof(ItemCount), "count");
        private static readonly FieldInfo oreDocks = Require<PickupDock[]>(typeof(Smelter), "oreDocks");
        private static readonly FieldInfo dockSettings = Require<PickupDockSettings>(typeof(PickupDock), "settings");
        private static readonly FieldInfo handDocking = Require<bool>(typeof(PickupDockSettings), "isHandDockingAllowed");
        private static readonly FieldInfo unlockManager = Require<SmelterUnlockManager>(typeof(Smelter), "unlockManager");
        private static readonly FieldInfo smeltingPlayers = Require<HashSet<Player>>(typeof(Smelter), "smeltingPlayers");
        private static readonly FieldInfo activeRecipe = Require<SmeltingRecipe>(typeof(Smelter), "recipe");
        private static readonly FieldInfo savedFuel = Require<float>(typeof(Smelter), "totalFuel");
        private static readonly FieldInfo savedDuration = Require<float>(typeof(Smelter), "requiredFuel");
        private static readonly MethodInfo consume = RequireMethod("Consume", typeof(Dictionary<Item, int>));

        private static readonly Dictionary<uint, SmeltingRecipe> byMetal = new Dictionary<uint, SmeltingRecipe>();
        private static readonly HashSet<SmeltingRecipe> registered = new HashSet<SmeltingRecipe>();
        private static readonly ConditionalWeakTable<Smelter, SmeltingRecipe> resuming = new ConditionalWeakTable<Smelter, SmeltingRecipe>();
        private static readonly ConditionalWeakTable<PickupDock, PickupDockSettings> configuredDocks = new ConditionalWeakTable<PickupDock, PickupDockSettings>();
        private static readonly ConditionalWeakTable<Smelter, PickupDock[]> intakeDocks = new ConditionalWeakTable<Smelter, PickupDock[]>();
        private static Item bow;
        private static bool loggedIntake;

        static MetalBowRecycling() { }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            if (bow != null)
                return;

            var stockBow = Item.Get(BowItemHash);
            var materials = MouldDefinition.Get(22952)?.AllowedMaterials;
            if (stockBow == null || materials == null)
                throw new InvalidOperationException("Native Metal Bow or metal ingot set is missing.");

            var initialize = RequireMethod(nameof(Smelter.Initialize));
            var findRecipe = RequireMethod("FindRecipe", typeof(float), typeof(Dictionary<Item, int>), typeof(float).MakeByRefType());
            var trySmelt = RequireMethod("TrySmelt", typeof(bool));
            var getInput = RequireMethod("GetInput");
            var evaluateFuel = RequireMethod("EvaluateFuel");

            SmeltingRecipe.CheckItems();
            var recipes = (Dictionary<uint, SmeltingRecipe>)catalogue.GetValue(null);
            try
            {
                // Use the native mould's ingots. If two items use one material,
                // use the lower item ID, such as Copper in place of Bronze.
                foreach (var item in materials.Items.Where(value => value != null).OrderBy(value => value.Hash))
                {
                    var ingot = item.GetComponent<Ingot>();
                    if (ingot == null || ingot.PhysicalMaterial == null || item.Hash > ushort.MaxValue)
                        continue;
                    // Heating one bow costs one ingot's heat budget. Stock fuel
                    // supplies 10 seconds and the stock ingots use 3–8 seconds.
                    float fuel = ingot.SmeltingDuration;
                    if (!(fuel > 0f) || float.IsInfinity(fuel))
                        continue;

                    uint id = RecipeHashPrefix | item.Hash;
                    if (recipes.ContainsKey(id))
                        throw new InvalidOperationException("Metal Bow recycling recipe ID is already in use: " + id);

                    var recipe = ScriptableObject.CreateInstance<SmeltingRecipe>();
                    recipe.name = "Recycle Metal Bow into " + item.name;
                    hash.SetValue(recipe, (int)id);
                    registered.Add(recipe);
                    duration.SetValue(recipe, fuel);
                    recipeInput.SetValue(recipe, new[] { Count(stockBow, 1) });
                    recipeOutput.SetValue(recipe, new[] { Count(item, RecoveredIngots) });
                    recipes.Add(id, recipe);
                    if (!byMetal.ContainsKey(ingot.PhysicalMaterial.Hash))
                        byMetal.Add(ingot.PhysicalMaterial.Hash, recipe);
                }

                if (byMetal.Count == 0)
                    throw new InvalidOperationException("No Metal Bow recycling ingots are available.");

                Patch(harmony, initialize, nameof(BeforeInitialize));
                Patch(harmony, findRecipe, nameof(BeforeFindRecipe));
                harmony.Patch(trySmelt, postfix: new HarmonyMethod(typeof(MetalBowRecycling), nameof(AfterTrySmelt)));
                Patch(harmony, getInput, nameof(BeforeGetInput));
                harmony.Patch(getInput, postfix: new HarmonyMethod(typeof(MetalBowRecycling), nameof(AfterGetInput)));
                Patch(harmony, evaluateFuel, nameof(BeforeEvaluateFuel));
                bow = stockBow;
            }
            catch
            {
                harmony.UnpatchSelf();
                foreach (var recipe in registered)
                {
                    if (recipes.TryGetValue(recipe.Hash, out var current) && current == recipe)
                        recipes.Remove(recipe.Hash);
                    UnityEngine.Object.Destroy(recipe);
                }
                registered.Clear();
                byMetal.Clear();
                throw;
            }
        }

        private static void BeforeInitialize(Smelter __instance)
        {
            if (bow == null || __instance.Entity?.Prefab?.Hash != SmelterPrefabHash)
                return;

            if (intakeDocks.TryGetValue(__instance, out _))
                return;

            // The active dispenser tray is where players normally drop ore.
            // Its three docks differ from the internal docks read by GetInput.
            var intake = __instance.GetComponentsInChildren<AutomaticDockArea>(true)
                .SelectMany(area => area.Docks ?? Array.Empty<PickupDock>())
                .Where(dock => dock != null && IsOreDispenser(dock.Entity?.Hash))
                .Distinct().ToArray();
            intakeDocks.Add(__instance, intake);

            foreach (var dock in ((PickupDock[])oreDocks.GetValue(__instance)).Concat(intake))
            {
                if (dock == null || dock.Settings == null || configuredDocks.TryGetValue(dock, out _))
                    continue;

                bool allowBowByHand = intake.Contains(dock) && !dock.Settings.IsHandDockingAllowed;
                var settings = UnityEngine.Object.Instantiate(dock.Settings);
                if (!settings.IncludedItems.Contains(bow))
                    settings.IncludedItems.Add(bow);
                if (allowBowByHand)
                    handDocking.SetValue(settings, true);
                dockSettings.SetValue(dock, settings);
                dock.DockFilter += (Interactor hand, Pickup pickup, ref bool allowed) =>
                    FilterBow(hand, pickup, allowBowByHand, ref allowed);
                configuredDocks.Add(dock, settings);
            }

            foreach (var dock in intake)
                dock.ItemDocked += (hand, pickup, slot) =>
                {
                    if (pickup != null && pickup.Item == bow)
                        __instance.CheckSmelt();
                };

            if (!loggedIntake)
            {
                loggedIntake = true;
                MelonLogger.Msg("ENOUGH_IRON_BOW_RECYCLING_INTAKE oreTraySlots=" + intake.Length);
            }
        }

        private static bool IsOreDispenser(uint? id) => id == 54940 || id == 54938 || id == 54936;

        private static void FilterBow(Interactor interactor, Pickup pickup, bool allowBowByHand, ref bool allowed)
        {
            if (!allowed)
                return;
            if (pickup != null && pickup.Item == bow)
                allowed = TryGetBowRecipe(pickup, out _, out _);
            else if (allowBowByHand && interactor != null)
                allowed = false;
        }

        private static bool TryGetBowRecipe(Pickup pickup, out PhysicalMaterial metal, out SmeltingRecipe recipe)
        {
            metal = null;
            recipe = null;
            if (pickup == null || pickup.Item != bow || pickup.Entity == null || pickup.Entity.IsBeingDestroyed
                || !MetalBowInstances.IsStockBow(pickup.Entity) || pickup.IsCrafted
                || pickup.GetComponentsInChildren<PickupDock>(true).Any(dock => dock.QuantityStored > 0 || dock.DockedPickup != null))
                return false;

            metal = pickup.GetComponent<PhysicalMaterialPart>()?.PhysicalMaterial;
            return metal != null && byMetal.TryGetValue(metal.Hash, out recipe);
        }

        private static bool BeforeFindRecipe(Smelter __instance, float __0, Dictionary<Item, int> __1,
            ref float __2, ref SmeltingRecipe __result)
        {
            if (resuming.TryGetValue(__instance, out var saved))
            {
                resuming.Remove(__instance);
                __result = saved;
                float previousDuration = (float)savedDuration.GetValue(__instance);
                __2 = previousDuration > 0f && !float.IsInfinity(previousDuration) ? previousDuration : saved.Duration;
                return false;
            }

            if (bow == null || __instance.Entity?.Prefab?.Hash != SmelterPrefabHash || !__1.TryGetValue(bow, out int count))
                return true;

            __result = null;
            __2 = 0f;
            if (__1.Count != 1 || count != 1)
                return false;

            var dock = ((PickupDock[])oreDocks.GetValue(__instance))
                .FirstOrDefault(value => value != null && value.dockedType == bow && value.QuantityStored == 1);
            bool inTray = dock == null;
            if (inTray)
                dock = FindIntakeBow(__instance);
            var unlocks = (SmelterUnlockManager)unlockManager.GetValue(__instance);
            if (dock == null || !TryGetBowRecipe(dock.DockedPickup, out var metal, out var recipe)
                || !(__0 >= recipe.Duration) || unlocks == null || !unlocks.PhysicalMaterials.Contains(metal)
                || recipe.Output[0].Item.Prefab == null)
                return false;

            if (inTray)
            {
                // Native Consume only sees the two internal ore docks.
                var players = (HashSet<Player>)smeltingPlayers.GetValue(__instance);
                players.Clear();
                var player = dock.DockedPickup.LastPlayerInteractor?.NetworkPlayer;
                if (player != null)
                    players.Add(player);
                dock.DestroyAmount(1);
                if (dock.QuantityStored != 0)
                    return false;
            }
            else
            {
                consume.Invoke(__instance, new object[] { new Dictionary<Item, int> { [bow] = 1 } });
                if (dock.QuantityStored != 0)
                    return false;
            }
            // The native iterator handles fuel, effects, and the recipe output.
            __result = recipe;
            __2 = recipe.Duration;
            return false;
        }

        private static void AfterTrySmelt(Smelter __instance, bool __0, ref IEnumerator __result)
        {
            if (!__0 || !__instance.IsSmelting || bow == null || __instance.Entity?.Prefab?.Hash != SmelterPrefabHash)
                return;

            var recipe = (SmeltingRecipe)activeRecipe.GetValue(__instance);
            if (recipe != null && registered.Contains(recipe))
                __result = Resume(__instance, __result, recipe);
        }

        private static IEnumerator Resume(Smelter smelter, IEnumerator native, SmeltingRecipe recipe)
        {
            bool pending = true;
            try
            {
                while (true)
                {
                    if (pending)
                        resuming.Add(smelter, recipe);
                    bool hasNext;
                    try
                    {
                        hasNext = native.MoveNext();
                    }
                    finally
                    {
                        // FindRecipe removes the saved selection when it is used.
                        // Only expose it while the resumed iterator is executing.
                        pending = resuming.Remove(smelter);
                    }
                    if (!hasNext)
                        yield break;
                    yield return native.Current;
                }
            }
            finally
            {
                (native as IDisposable)?.Dispose();
            }
        }

        private static bool BeforeGetInput(Smelter __instance, ref Dictionary<Item, int> __result)
        {
            if (!resuming.TryGetValue(__instance, out _))
                return true;

            // The saved cycle already consumed its input. Let the native loop
            // enter once and resume progress before checking the real docks.
            __result = new Dictionary<Item, int> { [bow] = 1 };
            return false;
        }

        private static void AfterGetInput(Smelter __instance, Dictionary<Item, int> __result)
        {
            if (__result.Count == 0 && FindIntakeBow(__instance) != null)
                __result.Add(bow, 1);
        }

        private static PickupDock FindIntakeBow(Smelter smelter)
        {
            if (!intakeDocks.TryGetValue(smelter, out var intake))
                return null;

            PickupDock donor = null;
            foreach (var dock in intake)
            {
                if (dock == null || dock.QuantityStored == 0)
                    continue;
                if (donor != null || dock.QuantityStored != 1 || dock.dockedType != bow)
                    return null;
                donor = dock;
            }
            return donor;
        }

        private static bool BeforeEvaluateFuel(Smelter __instance, ref float __result)
        {
            if (!resuming.TryGetValue(__instance, out var recipe))
                return true;

            // Keep the saved cycle's start budget. Remaining fuel and progress
            // are restored separately by the native save loader.
            float previousFuel = (float)savedFuel.GetValue(__instance);
            __result = previousFuel > 0f && !float.IsInfinity(previousFuel) ? previousFuel : recipe.Duration;
            return false;
        }

        private static ItemCount Count(Item item, int count)
        {
            var value = new ItemCount();
            countedItem.SetValue(value, item);
            itemCount.SetValue(value, count);
            return value;
        }

        private static void Patch(HarmonyLib.Harmony harmony, MethodInfo original, string prefix) =>
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(MetalBowRecycling), prefix));

        private static MethodInfo RequireMethod(string name, params Type[] parameters) =>
            AccessTools.DeclaredMethod(typeof(Smelter), name, parameters)
                ?? throw new MissingMethodException(typeof(Smelter).FullName, name);

        private static FieldInfo Require<T>(Type owner, string name)
        {
            var field = AccessTools.Field(owner, name);
            if (field == null || field.FieldType != typeof(T))
                throw new MissingFieldException(owner.FullName, name);
            return field;
        }
    }
}
