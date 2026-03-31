using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Reads URBK bake cache files. Vertices are pre-converted to Z-up (Remix) coordinates. 
    /// </summary>
    public class BakeCacheReader
    {
        private const uint Magic = 0x4B425255; // "URBK"
        private const uint SupportedVersion = 1;
        
        public struct BakedVertex
        {
            public float X, Y, Z;
            public float NX, NY, NZ;
            public float U, V;
        }
        
        public struct BakedMeshEntry
        {
            public ulong NameHash;
            public float[] WorldMatrix; // 16 floats, row-major
            public BakedVertex[] Vertices;
            public uint[] Indices;
        }
        
        /// <summary>
        /// Read all mesh entries from a URBK cache file.
        /// Returns null on failure.
        /// </summary>
        public static BakedMeshEntry[] Read(string path, ManualLogSource logger)
        {
            if (!File.Exists(path))
            {
                logger.LogWarning($"Bake cache not found: {path}");
                return null;
            }
            
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    uint magic = br.ReadUInt32();
                    if (magic != Magic)
                    {
                        logger.LogError($"Invalid bake cache magic: 0x{magic:X8} (expected 0x{Magic:X8})");
                        return null;
                    }
                    
                    uint version = br.ReadUInt32();
                    if (version != SupportedVersion)
                    {
                        logger.LogError($"Unsupported bake cache version: {version} (expected {SupportedVersion})");
                        return null;
                    }
                    
                    uint entryCount = br.ReadUInt32();
                    var entries = new BakedMeshEntry[entryCount];
                    
                    for (uint e = 0; e < entryCount; e++)
                    {
                        var entry = new BakedMeshEntry();
                        entry.NameHash = br.ReadUInt64();
                        
                        // Read 4x4 matrix (16 floats, row-major)
                        entry.WorldMatrix = new float[16];
                        for (int i = 0; i < 16; i++)
                            entry.WorldMatrix[i] = br.ReadSingle();
                        
                        uint vertCount = br.ReadUInt32();
                        uint idxCount = br.ReadUInt32();
                        
                        entry.Vertices = new BakedVertex[vertCount];
                        for (uint v = 0; v < vertCount; v++)
                        {
                            entry.Vertices[v] = new BakedVertex
                            {
                                X = br.ReadSingle(),
                                Y = br.ReadSingle(),
                                Z = br.ReadSingle(),
                                NX = br.ReadSingle(),
                                NY = br.ReadSingle(),
                                NZ = br.ReadSingle(),
                                U = br.ReadSingle(),
                                V = br.ReadSingle()
                            };
                        }
                        
                        entry.Indices = new uint[idxCount];
                        for (uint i = 0; i < idxCount; i++)
                            entry.Indices[i] = br.ReadUInt32();
                        
                        entries[e] = entry;
                    }
                    
                    logger.LogInfo($"Loaded {entries.Length} baked mesh entries from {Path.GetFileName(path)}");
                    return entries;
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to read bake cache: {ex.Message}");
                return null;
            }
        }
    }
}
