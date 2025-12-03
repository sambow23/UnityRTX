using System;
using System.Runtime.CompilerServices;

namespace UnityRemix
{
    /// <summary>
    /// XXHash64 implementation for texture hashing compatible with RTX Remix
    /// Based on xxHash - Extremely fast non-cryptographic hash algorithm
    /// Reference: https://github.com/Cyan4973/xxHash
    /// </summary>
    public static class XXHash64
    {
        private const ulong PRIME64_1 = 11400714785074694791UL;
        private const ulong PRIME64_2 = 14029467366897019727UL;
        private const ulong PRIME64_3 = 1609587929392839161UL;
        private const ulong PRIME64_4 = 9650029242287828579UL;
        private const ulong PRIME64_5 = 2870177450012600261UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RotateLeft(ulong value, int count)
        {
            return (value << count) | (value >> (64 - count));
        }

        /// <summary>
        /// Compute XXH64 hash of byte array
        /// </summary>
        public static unsafe ulong ComputeHash(byte[] data, int offset, int length, ulong seed = 0)
        {
            if (data == null || length == 0)
                return seed + PRIME64_5;

            fixed (byte* pData = &data[offset])
            {
                return ComputeHash(pData, length, seed);
            }
        }

        private static unsafe ulong ComputeHash(byte* data, int length, ulong seed)
        {
            byte* end = data + length;
            ulong hash;

            if (length >= 32)
            {
                byte* limit = end - 32;
                ulong v1 = seed + PRIME64_1 + PRIME64_2;
                ulong v2 = seed + PRIME64_2;
                ulong v3 = seed;
                ulong v4 = seed - PRIME64_1;

                do
                {
                    v1 += *(ulong*)data * PRIME64_2;
                    v1 = RotateLeft(v1, 31);
                    v1 *= PRIME64_1;
                    data += 8;

                    v2 += *(ulong*)data * PRIME64_2;
                    v2 = RotateLeft(v2, 31);
                    v2 *= PRIME64_1;
                    data += 8;

                    v3 += *(ulong*)data * PRIME64_2;
                    v3 = RotateLeft(v3, 31);
                    v3 *= PRIME64_1;
                    data += 8;

                    v4 += *(ulong*)data * PRIME64_2;
                    v4 = RotateLeft(v4, 31);
                    v4 *= PRIME64_1;
                    data += 8;
                } while (data <= limit);

                hash = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);

                v1 *= PRIME64_2;
                v1 = RotateLeft(v1, 31);
                v1 *= PRIME64_1;
                hash ^= v1;
                hash = hash * PRIME64_1 + PRIME64_4;

                v2 *= PRIME64_2;
                v2 = RotateLeft(v2, 31);
                v2 *= PRIME64_1;
                hash ^= v2;
                hash = hash * PRIME64_1 + PRIME64_4;

                v3 *= PRIME64_2;
                v3 = RotateLeft(v3, 31);
                v3 *= PRIME64_1;
                hash ^= v3;
                hash = hash * PRIME64_1 + PRIME64_4;

                v4 *= PRIME64_2;
                v4 = RotateLeft(v4, 31);
                v4 *= PRIME64_1;
                hash ^= v4;
                hash = hash * PRIME64_1 + PRIME64_4;
            }
            else
            {
                hash = seed + PRIME64_5;
            }

            hash += (ulong)length;

            while (data + 8 <= end)
            {
                ulong k1 = *(ulong*)data;
                k1 *= PRIME64_2;
                k1 = RotateLeft(k1, 31);
                k1 *= PRIME64_1;
                hash ^= k1;
                hash = RotateLeft(hash, 27) * PRIME64_1 + PRIME64_4;
                data += 8;
            }

            if (data + 4 <= end)
            {
                hash ^= (*(uint*)data) * PRIME64_1;
                hash = RotateLeft(hash, 23) * PRIME64_2 + PRIME64_3;
                data += 4;
            }

            while (data < end)
            {
                hash ^= (*data) * PRIME64_5;
                hash = RotateLeft(hash, 11) * PRIME64_1;
                data++;
            }

            hash ^= hash >> 33;
            hash *= PRIME64_2;
            hash ^= hash >> 29;
            hash *= PRIME64_3;
            hash ^= hash >> 32;

            return hash;
        }
    }
}
