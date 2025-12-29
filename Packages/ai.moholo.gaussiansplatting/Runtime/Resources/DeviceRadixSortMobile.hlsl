/******************************************************************************
 * DeviceRadixSortMobile
 * Mobile-compatible 8-bit LSD Radix Sort using shared memory atomics only.
 * No wave intrinsics - works on Quest/Adreno GPUs.
 * 
 * Based on GPUSorting by Thomas Smith, modified for mobile compatibility.
 ******************************************************************************/
#ifndef DEVICE_RADIX_SORT_MOBILE_HLSL
#define DEVICE_RADIX_SORT_MOBILE_HLSL

#include "SortCommonMobile.hlsl"

RWStructuredBuffer<uint> b_globalHist;
RWStructuredBuffer<uint> b_passHist;

//*****************************************************************************
// INIT KERNEL - Clear global histogram
//*****************************************************************************
[numthreads(256, 1, 1)]
void InitDeviceRadixSort(uint3 id : SV_DispatchThreadID)
{
    if (id.x < RADIX * RADIX_PASSES)
        b_globalHist[id.x] = 0;
}

//*****************************************************************************
// UPSWEEP KERNEL - Build per-partition histograms
//*****************************************************************************
[numthreads(D_DIM, 1, 1)]
void Upsweep(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    // Clear local histogram
    if (gtid.x < RADIX)
        g_hist[gtid.x] = 0;
    GroupMemoryBarrierWithGroupSync();
    
    // Calculate partition bounds
    uint partStart = gid.x * PART_SIZE;
    uint partEnd = min(partStart + PART_SIZE, e_numKeys);
    
    // Each thread processes multiple elements
    for (uint i = partStart + gtid.x; i < partEnd; i += D_DIM)
    {
        uint key = b_sort[i];
        uint digit = ExtractDigit(key);
        InterlockedAdd(g_hist[digit], 1);
    }
    GroupMemoryBarrierWithGroupSync();
    
    if (gtid.x < RADIX)
    {
        b_passHist[gtid.x * e_threadBlocks + gid.x] = g_hist[gtid.x];
        InterlockedAdd(b_globalHist[gtid.x + GlobalHistOffset()], g_hist[gtid.x]);
    }
}

//*****************************************************************************
// SCAN KERNEL - Exclusive prefix sum over partition histograms AND global histogram
// gid.x = 0..RADIX-1 for per-digit partition scans
// gid.x = RADIX for global histogram scan
//*****************************************************************************
[numthreads(D_DIM, 1, 1)]
void Scan(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    if (gid.x < RADIX)
    {
        // Per-digit partition scan
        uint digitOffset = gid.x * e_threadBlocks;
        uint reduction = 0;
        uint numChunks = (e_threadBlocks + D_DIM - 1) / D_DIM;
        
        for (uint chunk = 0; chunk < numChunks; ++chunk)
        {
            uint idx = chunk * D_DIM + gtid.x;
            
            uint val = 0;
            if (idx < e_threadBlocks)
                val = b_passHist[digitOffset + idx];
            
            g_scan[gtid.x] = val;
            GroupMemoryBarrierWithGroupSync();
            
            // Blelloch exclusive prefix sum
            uint offset = 1;
            for (uint d = D_DIM >> 1; d > 0; d >>= 1)
            {
                GroupMemoryBarrierWithGroupSync();
                if (gtid.x < d)
                {
                    uint ai = offset * (2 * gtid.x + 1) - 1;
                    uint bi = offset * (2 * gtid.x + 2) - 1;
                    g_scan[bi] += g_scan[ai];
                }
                offset *= 2;
            }
            
            uint chunkSum = 0;
            if (gtid.x == 0)
            {
                chunkSum = g_scan[D_DIM - 1];
                g_scan[D_DIM - 1] = 0;
            }
            
            for (uint d = 1; d < D_DIM; d *= 2)
            {
                offset >>= 1;
                GroupMemoryBarrierWithGroupSync();
                if (gtid.x < d)
                {
                    uint ai = offset * (2 * gtid.x + 1) - 1;
                    uint bi = offset * (2 * gtid.x + 2) - 1;
                    uint t = g_scan[ai];
                    g_scan[ai] = g_scan[bi];
                    g_scan[bi] += t;
                }
            }
            GroupMemoryBarrierWithGroupSync();
            
            if (idx < e_threadBlocks)
                b_passHist[digitOffset + idx] = g_scan[gtid.x] + reduction;
            
            GroupMemoryBarrierWithGroupSync();
            if (gtid.x == 0)
                g_scan[0] = chunkSum;
            GroupMemoryBarrierWithGroupSync();
            reduction += g_scan[0];
        }
    }
    else
    {
        // Global histogram exclusive prefix sum (single group, gid.x == RADIX)
        uint histOffset = GlobalHistOffset();
        
        // Load counts into shared memory
        uint val = 0;
        if (gtid.x < RADIX)
            val = b_globalHist[gtid.x + histOffset];
        g_scan[gtid.x] = val;
        GroupMemoryBarrierWithGroupSync();
        
        // Blelloch exclusive prefix sum over 256 elements
        uint offset = 1;
        for (uint d = RADIX >> 1; d > 0; d >>= 1)
        {
            GroupMemoryBarrierWithGroupSync();
            if (gtid.x < d)
            {
                uint ai = offset * (2 * gtid.x + 1) - 1;
                uint bi = offset * (2 * gtid.x + 2) - 1;
                g_scan[bi] += g_scan[ai];
            }
            offset *= 2;
        }
        
        if (gtid.x == 0)
            g_scan[RADIX - 1] = 0;
        
        for (uint d = 1; d < RADIX; d *= 2)
        {
            offset >>= 1;
            GroupMemoryBarrierWithGroupSync();
            if (gtid.x < d)
            {
                uint ai = offset * (2 * gtid.x + 1) - 1;
                uint bi = offset * (2 * gtid.x + 2) - 1;
                uint t = g_scan[ai];
                g_scan[ai] = g_scan[bi];
                g_scan[bi] += t;
            }
        }
        GroupMemoryBarrierWithGroupSync();
        
        // Write back exclusive prefix sums
        if (gtid.x < RADIX)
            b_globalHist[gtid.x + histOffset] = g_scan[gtid.x];
    }
}

//*****************************************************************************
// DOWNSWEEP KERNEL - Scatter keys to sorted positions
//*****************************************************************************
[numthreads(D_DIM, 1, 1)]
void Downsweep(uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    // Load base offsets: globalHist now has exclusive prefix sum
    if (gtid.x < RADIX)
    {
        uint globalOffset = b_globalHist[gtid.x + GlobalHistOffset()];
        uint passOffset = b_passHist[gtid.x * e_threadBlocks + gid.x];
        g_hist[gtid.x] = globalOffset + passOffset;
    }
    
    if (gtid.x < RADIX)
        g_d[gtid.x] = 0;
    GroupMemoryBarrierWithGroupSync();
    
    uint partStart = gid.x * PART_SIZE;
    uint partEnd = min(partStart + PART_SIZE, e_numKeys);
    uint partSize = partEnd - partStart;
    
    // First pass: claim local offsets and store data
    for (uint i = partStart + gtid.x; i < partEnd; i += D_DIM)
    {
        uint key = b_sort[i];
        uint digit = ExtractDigit(key);
        
        uint localOffset;
        InterlockedAdd(g_d[digit], 1, localOffset);
        
        uint localIdx = i - partStart;
        g_d[RADIX + localIdx] = key;
        g_d[RADIX + PART_SIZE + localIdx] = localOffset | (digit << 16);
    }
    GroupMemoryBarrierWithGroupSync();
    
    // Second pass: scatter keys (descending on last pass for back-to-front)
    bool isLastPass = (e_radixShift == 24);
    for (uint i = gtid.x; i < partSize; i += D_DIM)
    {
        uint key = g_d[RADIX + i];
        uint packed = g_d[RADIX + PART_SIZE + i];
        uint localOffset = packed & 0xFFFF;
        uint digit = packed >> 16;
        uint destIdx = g_hist[digit] + localOffset;
        
        if (isLastPass)
            destIdx = DescendingIndex(destIdx);
        
        b_alt[destIdx] = key;
    }
    
#if defined(SORT_PAIRS)
    GroupMemoryBarrierWithGroupSync();
    
    for (uint j = partStart + gtid.x; j < partEnd; j += D_DIM)
    {
        uint payload = b_sortPayload[j];
        uint localIdx = j - partStart;
        uint packed = g_d[RADIX + PART_SIZE + localIdx];
        uint localOffset = packed & 0xFFFF;
        uint digit = packed >> 16;
        uint destIdx = g_hist[digit] + localOffset;
        
        if (isLastPass)
            destIdx = DescendingIndex(destIdx);
        
        b_altPayload[destIdx] = payload;
    }
#endif
}

#endif // DEVICE_RADIX_SORT_MOBILE_HLSL
