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
        private const float ArrowDamageScale = .55f;
        // The stock 130-point durability setting converts this to 0.9 wear.
        private const float ShotWear = 3f;
        private static readonly FieldInfo tightness = AccessTools.Field(typeof(BowString), "tightnessMultiplier");

        private BowString bowString;
        private Shooter shooter;
        private DurabilityModule durability;
        private float ironDamage;
        private float damageFactor = ArrowDamageScale;

        internal static void ValidateSchema()
        {
            if (tightness == null || tightness.FieldType != typeof(float))
                throw new MissingFieldException(typeof(BowString).FullName, "tightnessMultiplier");
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
            tightness.SetValue(bowString, 1f / ratio);
            damageFactor = ratio * ArrowDamageScale;
            ApplyAppearance(material);
        }

        private void WearAfterShot(Shooter source, Pickup ammunition, Vector3 velocity)
        {
            // Shooter only raises OnShoot after the primary launch. A miss still
            // wears the bow, and extra arrows don't launch through this event.
            if (NetworkSceneManager.IsServer && durability != null && durability.Entity != null
                && !durability.Entity.IsBeingDestroyed)
                durability.AffectDurability(ShotWear);
        }

        private void OnDestroy()
        {
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
