/******************************************************************************
 * SortCommonMobile
 * Mobile-compatible sorting utilities using shared memory atomics only.
 * No wave intrinsics (WavePrefixSum, WaveActiveBallot, etc.)
 * 
 * Based on GPUSorting by Thomas Smith, modified for mobile compatibility.
 ******************************************************************************/
#ifndef SORT_COMMON_MOBILE_HLSL
#define SORT_COMMON_MOBILE_HLSL

#define KEYS_PER_THREAD     4U
#define D_DIM               256U
#define PART_SIZE           1024U
#define D_TOTAL_SMEM        2560U  // RADIX + PART_SIZE*2 for temp storage

#define RADIX               256U
#define RADIX_MASK          255U
#define RADIX_LOG           8U
#define RADIX_PASSES        4U

cbuffer cbGpuSorting : register(b0)
{
    uint e_numKeys;
    uint e_radixShift;
    uint e_threadBlocks;
    uint padding;
};

#if defined(KEY_UINT)
RWStructuredBuffer<uint> b_sort;
RWStructuredBuffer<uint> b_alt;
#endif

#if defined(PAYLOAD_UINT)
RWStructuredBuffer<uint> b_sortPayload;
RWStructuredBuffer<uint> b_altPayload;
#endif

groupshared uint g_d[D_TOTAL_SMEM];
groupshared uint g_hist[RADIX];
groupshared uint g_scan[D_DIM];

struct KeyStruct
{
    uint k[KEYS_PER_THREAD];
};

struct OffsetStruct
{
    uint o[KEYS_PER_THREAD];
};

inline uint FloatToUint(float f)
{
    uint mask = -((int)(asuint(f) >> 31)) | 0x80000000;
    return asuint(f) ^ mask;
}

inline float UintToFloat(uint u)
{
    uint mask = ((u >> 31) - 1) | 0x80000000;
    return asfloat(u ^ mask);
}

inline uint ExtractDigit(uint key)
{
    return (key >> e_radixShift) & RADIX_MASK;
}

inline uint GlobalHistOffset()
{
    return e_radixShift << 5;
}

inline uint DescendingIndex(uint deviceIndex)
{
    return e_numKeys - deviceIndex - 1;
}

inline void ClearHistogram(uint gtid)
{
    if (gtid < RADIX)
        g_hist[gtid] = 0;
}

inline void ClearSharedMem(uint gtid)
{
    for (uint i = gtid; i < D_TOTAL_SMEM; i += D_DIM)
        g_d[i] = 0;
}

inline void LoadKey(inout uint key, uint index)
{
#if defined(KEY_UINT)
    key = b_sort[index];
#endif
}

inline void LoadDummyKey(inout uint key)
{
    key = 0xffffffff;
}

inline KeyStruct LoadKeys(uint gtid, uint partIndex)
{
    KeyStruct keys;
    uint baseIdx = partIndex * PART_SIZE + gtid;
    [unroll]
    for (uint i = 0; i < KEYS_PER_THREAD; ++i)
    {
        uint idx = baseIdx + i * D_DIM;
        if (idx < e_numKeys)
            LoadKey(keys.k[i], idx);
        else
            LoadDummyKey(keys.k[i]);
    }
    return keys;
}

inline void LoadPayload(inout uint payload, uint deviceIndex)
{
#if defined(PAYLOAD_UINT)
    payload = b_sortPayload[deviceIndex];
#endif
}

inline KeyStruct LoadPayloads(uint gtid, uint partIndex)
{
    KeyStruct payloads;
    uint baseIdx = partIndex * PART_SIZE + gtid;
    [unroll]
    for (uint i = 0; i < KEYS_PER_THREAD; ++i)
    {
        uint idx = baseIdx + i * D_DIM;
        if (idx < e_numKeys)
            LoadPayload(payloads.k[i], idx);
        else
            payloads.k[i] = 0;
    }
    return payloads;
}

inline void WriteKey(uint deviceIndex, uint key)
{
#if defined(KEY_UINT)
    b_alt[deviceIndex] = key;
#endif
}

inline void WritePayload(uint deviceIndex, uint payload)
{
#if defined(PAYLOAD_UINT)
    b_altPayload[deviceIndex] = payload;
#endif
}

// Blelloch exclusive prefix sum in shared memory
inline void ExclusivePrefixSum(uint gtid, uint count)
{
    // Up-sweep (reduce) phase
    uint offset = 1;
    for (uint d = count >> 1; d > 0; d >>= 1)
    {
        GroupMemoryBarrierWithGroupSync();
        if (gtid < d)
        {
            uint ai = offset * (2 * gtid + 1) - 1;
            uint bi = offset * (2 * gtid + 2) - 1;
            g_scan[bi] += g_scan[ai];
        }
        offset *= 2;
    }
    
    // Clear the last element
    if (gtid == 0)
        g_scan[count - 1] = 0;
    
    // Down-sweep phase
    for (uint d = 1; d < count; d *= 2)
    {
        offset >>= 1;
        GroupMemoryBarrierWithGroupSync();
        if (gtid < d)
        {
            uint ai = offset * (2 * gtid + 1) - 1;
            uint bi = offset * (2 * gtid + 2) - 1;
            uint t = g_scan[ai];
            g_scan[ai] = g_scan[bi];
            g_scan[bi] += t;
        }
    }
    GroupMemoryBarrierWithGroupSync();
}

// Build histogram using atomics
inline void BuildHistogram(uint gtid, KeyStruct keys)
{
    [unroll]
    for (uint i = 0; i < KEYS_PER_THREAD; ++i)
    {
        if (keys.k[i] != 0xffffffff)
        {
            uint digit = ExtractDigit(keys.k[i]);
            InterlockedAdd(g_hist[digit], 1);
        }
    }
}

// Rank keys using histogram and atomics
inline OffsetStruct RankKeys(uint gtid, KeyStruct keys)
{
    OffsetStruct offsets;
    
    [unroll]
    for (uint i = 0; i < KEYS_PER_THREAD; ++i)
    {
        uint digit = ExtractDigit(keys.k[i]);
        uint localOffset;
        InterlockedAdd(g_d[digit], 1, localOffset);
        offsets.o[i] = localOffset;
    }
    
    return offsets;
}

#endif // SORT_COMMON_MOBILE_HLSL

