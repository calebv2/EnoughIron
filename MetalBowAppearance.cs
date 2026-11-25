using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EnoughIron
{
    public sealed partial class MetalBowBehaviour
    {
        [SerializeField] private Renderer[] bodyRenderers;
        [SerializeField] private int[] bodySlots;
        [SerializeField] private Material[] originalBodyMaterials;
        private Material glowSource;
        private Material glowMaterial;
        private object glowDefinition;
        private GameObject glowPulseHost;

        private void ConfigureAppearance(GameObject root)
        {
            if (ApplicationManager.IsHeadless)
                return;

            bodyRenderers = new Renderer[5];
            bodySlots = new int[bodyRenderers.Length];
            originalBodyMaterials = new Material[bodyRenderers.Length];

            BindBodySlot<SkinnedMeshRenderer>(root, 0, "Mesh/metal_boy_body_LOD0", 0);
            BindBodySlot<SkinnedMeshRenderer>(root, 1, "Mesh/metal_bow_worn_grp/metal_bow_body_worn_LOD0", 0);
            BindBodySlot<SkinnedMeshRenderer>(root, 2, "Mesh/metal_bow_damaged_grp/metal_bow_body_damaged_LOD0", 0);
            BindBodySlot<SkinnedMeshRenderer>(root, 3, "Mesh/metal_bow_LOD1", 1);
            BindBodySlot<MeshRenderer>(root, 4, "Mesh/metal_bow_LOD2", 1);
        }

        private void BindBodySlot<TRenderer>(GameObject root, int index, string path, int slot)
            where TRenderer : Renderer
        {
            Transform target = root.transform.Find(path);
            TRenderer renderer = target != null ? target.GetComponent<TRenderer>() : null;
            if (renderer == null)
                throw new InvalidOperationException($"Metal Bow body renderer is missing or has a different type at {path}.");

            Material[] materials = renderer.sharedMaterials;
            if (materials.Length != slot + 1 || materials[slot] == null || materials[slot].name != "IronNonForging"
                || (slot == 1 && (materials[0] == null || materials[0].name != "Wooden_Atlas_m"))
                || (index > 0 && materials[slot] != originalBodyMaterials[0]))
            {
                throw new InvalidOperationException($"Metal Bow body material layout is unexpected at {path}.");
            }

            bodyRenderers[index] = renderer;
            bodySlots[index] = slot;
            originalBodyMaterials[index] = materials[slot];
        }

        private void ApplyAppearance(PhysicalMaterial material)
        {
            if (bodyRenderers == null || bodySlots == null || originalBodyMaterials == null
                || bodyRenderers.Length != bodySlots.Length || bodyRenderers.Length != originalBodyMaterials.Length)
            {
                return;
            }

            Material replacement = ApplyIngotGlow(material, GetBodyMaterial(material));
            for (int index = 0; index < bodyRenderers.Length; index++)
            {
                Renderer renderer = bodyRenderers[index];
                Material visual = replacement != null ? replacement : originalBodyMaterials[index];
                if (renderer == null || visual == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                int slot = bodySlots[index];
                if (slot < 0 || slot >= materials.Length || materials[slot] == visual)
                    continue;

                materials[slot] = visual;
                renderer.sharedMaterials = materials;
            }
        }

        private static class IngotGlow
        {
            internal static readonly MethodInfo Find = AccessTools.TypeByName("CustomIngots.API.IngotCatalog")
                ?.GetMethod("FindByMaterialHash", new[] { typeof(uint) });
            internal static readonly PropertyInfo Emission = Find?.ReturnType.GetProperty("Emission");
            internal static readonly MethodInfo Bind = Find == null ? null : AccessTools
                .TypeByName("CustomIngots.Config.Integration.CrystalClientAppearancePatch")
                ?.GetMethod("BindEmissionPulse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(GameObject), typeof(IReadOnlyList<Material>), Find.ReturnType }, null);
            internal static bool ReportedFailure;
        }

        private Material ApplyIngotGlow(PhysicalMaterial metal, Material surface)
        {
            if (surface == null || (!surface.HasProperty("_Emission") && !surface.HasProperty("_EmissionColor"))
                || IngotGlow.Emission == null)
            {
                ReleaseAppearance();
                return surface;
            }

            try
            {
                object definition = IngotGlow.Find.Invoke(null, new object[] { metal.Hash });
                if (ReferenceEquals(definition, glowDefinition) && surface == glowSource && glowMaterial != null)
                    return glowMaterial;

                ReleaseAppearance();
                if (definition == null || !(IngotGlow.Emission.GetValue(definition) is Color emission)
                    || Mathf.Max(emission.r, emission.g, emission.b) <= 0f)
                    return surface;

                glowSource = surface;
                glowDefinition = definition;
                glowMaterial = new Material(surface) { name = surface.name + " (Metal Bow Glow)" };
                glowMaterial.EnableKeyword("_EMISSION");
                if (glowMaterial.HasProperty("_Emission"))
                    glowMaterial.SetColor("_Emission", emission);
                if (glowMaterial.HasProperty("_EmissionColor"))
                    glowMaterial.SetColor("_EmissionColor", emission);
                if (glowMaterial.HasProperty("_ForcedTemperature"))
                    glowMaterial.SetFloat("_ForcedTemperature", 1f);
                if (IngotGlow.Bind != null)
                {
                    // The ingot postfix clears pulse controllers on the pickup root when
                    // its PhysicalMaterialPart has no native renderers. The bow owns its slots.
                    glowPulseHost = new GameObject("Metal Bow Emission");
                    glowPulseHost.transform.SetParent(transform, false);
                    IngotGlow.Bind.Invoke(null, new object[] { glowPulseHost, new[] { glowMaterial }, definition });
                }
                return glowMaterial;
            }
            catch (Exception exception)
            {
                ReportGlowFailure(exception);
                ReleaseAppearance();
                return surface;
            }
        }

        private void ReleaseAppearance()
        {
            if (glowPulseHost != null)
                Destroy(glowPulseHost);
            if (glowMaterial != null)
                Destroy(glowMaterial);
            glowPulseHost = null;
            glowMaterial = null;
            glowSource = null;
            glowDefinition = null;
        }

        private static void ReportGlowFailure(Exception exception)
        {
            if (IngotGlow.ReportedFailure)
                return;
            IngotGlow.ReportedFailure = true;
            MelonLogger.Warning("ENOUGH_IRON_BOW_GLOW_UNAVAILABLE " + exception.GetBaseException().Message);
        }

        private static Material GetBodyMaterial(PhysicalMaterial material)
        {
            if (material == null || material.Hash == IronHash)
                return null;

            try
            {
                // The mixed LOD meshes share UVs with the wraps. Use the metal's
                // standalone shader material so the original UVs stay intact.
                Material visual = material.GetNonAtlasedNonDeformingMaterial(PhysicalMaterialChannel.A);
                Shader shader = visual != null ? visual.shader : null;
                return shader != null && shader.isSupported && shader.name != "Hidden/InternalErrorShader" ? visual : null;
            }
            catch (Exception)
            {
                // A material with incomplete channels falls back to the original body.
                return null;
            }
        }
    }
}
