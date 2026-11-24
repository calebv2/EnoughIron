using System;
using System.Reflection;
using Alta.Inventory;
using Alta.Networking;
using HarmonyLib;
using UnityEngine;

namespace EnoughIron
{
    public sealed partial class MetalBowBehaviour : MonoBehaviour, IListenToPhysicalMaterial
    {
        internal const uint IronHash = 1;
        private const uint DeathSteelHash = 0x44454D01;
        private const float DeathSteelDamageRatio = 3.9f;
        private const float DeathSteelDrawSpeed = 8f;
        private const float ArrowDamageScale = .55f;
        private const int IronShotLimit = 500;
        private static readonly FieldInfo tightness = AccessTools.Field(typeof(BowString), "tightnessMultiplier");
        private static readonly FieldInfo currentDurability = AccessTools.Field(typeof(DurabilityModule), "currentDurability");
        private static readonly FieldInfo durabilitySettings = AccessTools.Field(typeof(DurabilityModule), "durabilitySettings");

        private BowString bowString;
        private Shooter shooter;
        private DurabilityModule durability;
        private int shotLimit = IronShotLimit;
        private float ironDamage;
        private float damageFactor = ArrowDamageScale;

        internal static void ValidateSchema()
        {
            if (tightness == null || tightness.FieldType != typeof(float))
                throw new MissingFieldException(typeof(BowString).FullName, "tightnessMultiplier");
            if (currentDurability == null || currentDurability.FieldType != typeof(float))
                throw new MissingFieldException(typeof(DurabilityModule).FullName, "currentDurability");
            if (durabilitySettings == null || durabilitySettings.FieldType != typeof(DurabilitySettings))
                throw new MissingFieldException(typeof(DurabilityModule).FullName, "durabilitySettings");
        }

        internal void Configure(BowString nativeString, PhysicalMaterial iron)
        {
            ConfigureAppearance(gameObject);
            bowString = nativeString;
            shooter = nativeString.GetComponent<Shooter>();
            durability = GetComponent<DurabilityModule>();
            ironDamage = iron.DamageMultiplier;
            SetPhysicalMaterial(iron);

            if (shooter != null && durability != null)
                shooter.OnShoot += WearAfterShot;
        }

        public void SetPhysicalMaterial(PhysicalMaterial material)
        {
            if (bowString == null || material == null)
                return;

            float ratio = material.DamageMultiplier / ironDamage;
            if (ratio <= 0f || float.IsNaN(ratio) || float.IsInfinity(ratio) || float.IsInfinity(1f / ratio))
                ratio = 1f;

            // Native HeldUpdate divides its pull response by this cached value.
            // Every material change derives a fresh value from the Iron baseline.
            float drawSpeed = GetDrawSpeed(material.Hash, ratio);
            tightness.SetValue(bowString, 1f / drawSpeed);
            damageFactor = ratio * ArrowDamageScale;
            shotLimit = GetShotLimit(material.Hash);
            ApplyAppearance(material);
        }

        private static float GetDrawSpeed(uint materialHash, float damageRatio)
        {
            if (materialHash == DeathSteelHash)
                return DeathSteelDrawSpeed;
            if (damageRatio <= 1f)
                return damageRatio;

            float progress = (damageRatio - 1f) / (DeathSteelDamageRatio - 1f);
            return Mathf.Lerp(1f, DeathSteelDrawSpeed, progress);
        }

        internal static int GetShotLimit(uint materialHash)
        {
            switch (materialHash)
            {
                case 20186:      // Copper and Bronze share a material.
                case 56394:      // Gold
                    return 250;
                case 0x43574D00: return 350;  // Legacy Crystal
                case 31468:      return 600;  // Red Iron Alloy
                case 16222:                   // Orchi Alloy
                case 24028:      return 750;  // White Gold Alloy
                case 28618:      return 900;  // Silver
                case 31174:      return 1200; // Mythril
                case 31502:      return 1450; // Evinon Steel Alloy
                case 0x43574D02: return 1650; // Crysteel
                case 0x454D4D01: return 1700; // Ember Steel
                case 24094:      return 1800; // Carsi Alloy
                case 0x50444D01: return 1900; // Pink Diamond
                case 0x4C444D01: return 2100; // Light & Dark Steel
                case 0x4A444D01: return 2250; // Jade Steel
                case DeathSteelHash: return 2500;
                default:         return IronShotLimit;
            }
        }

        private void WearAfterShot(Shooter source, Pickup ammunition, Vector3 velocity)
        {
            // Shooter only raises OnShoot after the primary launch. A miss still
            // wears the bow, and extra arrows don't launch through this event.
            if (!NetworkSceneManager.IsServer || durability == null || durability.Entity == null
                || durability.Entity.IsBeingDestroyed)
                return;

            var module = durability;
            while (module.AttachedTo != null)
                module = module.AttachedTo;

            float integrity = module.Integrity;
            var settings = (DurabilitySettings)durabilitySettings.GetValue(module);
            if (!(integrity > 0f) || float.IsInfinity(integrity) || settings == null
                || !(settings.DamageConversion > 0f) || float.IsInfinity(settings.DamageConversion))
                return;
            float capacity = (float)currentDurability.GetValue(module);
            if (!(capacity > 0f) || float.IsInfinity(capacity))
                return;

            // Integrity is the native saved percentage. Use the current cache
            // only to convert a one-shot percentage drop to native wear units.
            // This also works when loading a worn bow resets the native cache.
            double remaining = (double)integrity * shotLimit;
            double whole = Math.Round(remaining);
            if (Math.Abs(remaining - whole) < .001)
                remaining = whole;
            // Clear the last fraction even if native float conversion rounds down.
            double fraction = remaining <= 1.0
                ? 1.000001
                : 1.0 - (remaining - 1.0) / (shotLimit * (double)integrity);
            module.AffectDurability((float)(capacity * fraction / settings.DamageConversion));
        }

        private void OnDestroy()
        {
            ReleaseAppearance();
            if (shooter != null)
                shooter.OnShoot -= WearAfterShot;
        }

        internal static float DamageFor(BowString nativeString)
        {
            if (nativeString == null || nativeString.Bow == null)
                return 1f;

            var behaviour = nativeString.Bow.GetComponent<MetalBowBehaviour>();
            return behaviour != null && behaviour.bowString == nativeString ? behaviour.damageFactor : 1f;
        }

    }
}
