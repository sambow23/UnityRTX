using System;
using System.Collections.Generic;

namespace UnityRemix
{
    /// <summary>
    /// Tracks which category each texture belongs to based on user selections in Remix UI.
    /// This allows us to properly set categoryFlags on DrawInstance calls.
    /// </summary>
    public class TextureCategoryManager
    {
        // Maps texture hash → category flags
        private Dictionary<ulong, uint> textureCategoryMap = new Dictionary<ulong, uint>();
        
        // Option names from rtx_options.h - these are the actual category names used by Remix
        public const string CATEGORY_SKY = "rtx.skyBoxTextures";
        public const string CATEGORY_IGNORE = "rtx.ignoreTextures";
        public const string CATEGORY_UI = "rtx.uiTextures";
        public const string CATEGORY_WORLD_UI = "rtx.worldSpaceUiTextures";
        public const string CATEGORY_WORLD_MATTE = "rtx.worldSpaceUiBackgroundTextures";
        public const string CATEGORY_PARTICLE = "rtx.particleTextures";
        public const string CATEGORY_DECAL = "rtx.decalTextures";
        public const string CATEGORY_TERRAIN = "rtx.terrainTextures";
        public const string CATEGORY_ANIMATED_WATER = "rtx.animatedWaterTextures";
        public const string CATEGORY_PARTICLE_EMITTER = "rtx.particleEmitterTextures";
        public const string CATEGORY_LEGACY_EMISSIVE = "rtx.legacyEmissiveTextures";
        
        /// <summary>
        /// Register a texture to a specific category.
        /// Called when user categorizes a texture in Remix UI, or we detect it via polling.
        /// </summary>
        public void SetTextureCategory(ulong textureHash, string categoryName)
        {
            if (textureHash == 0) return;
            
            uint categoryFlag = CategoryNameToFlag(categoryName);
            if (categoryFlag == 0) return;
            
            if (!textureCategoryMap.ContainsKey(textureHash))
            {
                textureCategoryMap[textureHash] = categoryFlag;
            }
            else
            {
                // Add to existing flags (textures can have multiple categories)
                textureCategoryMap[textureHash] |= categoryFlag;
            }
        }
        
        /// <summary>
        /// Remove a texture from a specific category
        /// </summary>
        public void RemoveTextureCategory(ulong textureHash, string categoryName)
        {
            if (textureHash == 0) return;
            
            uint categoryFlag = CategoryNameToFlag(categoryName);
            if (categoryFlag == 0) return;
            
            if (textureCategoryMap.ContainsKey(textureHash))
            {
                textureCategoryMap[textureHash] &= ~categoryFlag;
                
                // Remove entry if no categories left
                if (textureCategoryMap[textureHash] == 0)
                {
                    textureCategoryMap.Remove(textureHash);
                }
            }
        }
        
        /// <summary>
        /// Get category flags for a given texture hash
        /// </summary>
        public uint GetCategoryFlags(ulong textureHash)
        {
            if (textureHash == 0) return 0;
            
            if (textureCategoryMap.TryGetValue(textureHash, out uint flags))
            {
                return flags;
            }
            
            return 0;
        }
        
        /// <summary>
        /// Get category flags for a material based on its albedo texture
        /// This matches how Remix does it internally (using getColorTexture().getImageHash())
        /// </summary>
        public uint GetCategoryFlagsForMaterial(ulong albedoTextureHash)
        {
            return GetCategoryFlags(albedoTextureHash);
        }
        
        /// <summary>
        /// Convert Remix category name to remixapi_InstanceCategoryBit flag
        /// </summary>
        private uint CategoryNameToFlag(string categoryName)
        {
            switch (categoryName)
            {
                case CATEGORY_SKY:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_SKY;
                    
                case CATEGORY_IGNORE:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE;
                    
                case CATEGORY_UI:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_UI; // Note: UI textures actually use WorldUI flag
                    
                case CATEGORY_WORLD_UI:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_UI;
                    
                case CATEGORY_WORLD_MATTE:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_MATTE;
                    
                case CATEGORY_PARTICLE:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE;
                    
                case CATEGORY_DECAL:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_DECAL_STATIC;
                    
                case CATEGORY_TERRAIN:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_TERRAIN;
                    
                case CATEGORY_ANIMATED_WATER:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_ANIMATED_WATER;
                    
                case CATEGORY_PARTICLE_EMITTER:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE_EMITTER;
                    
                case CATEGORY_LEGACY_EMISSIVE:
                    return (uint)RemixAPI.remixapi_InstanceCategoryBit.REMIXAPI_INSTANCE_CATEGORY_BIT_LEGACY_EMISSIVE;
                    
                default:
                    return 0;
            }
        }
        
        /// <summary>
        /// Clear all category mappings
        /// </summary>
        public void Clear()
        {
            textureCategoryMap.Clear();
        }
    }
}
