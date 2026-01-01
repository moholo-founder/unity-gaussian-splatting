#!/usr/bin/env python3
"""
Barycentric Gaussian-to-Mesh Mapping

Maps Gaussian splat positions to mesh faces using barycentric coordinates
and normal offsets for stable animation correspondence.

For each Gaussian:
1. Find the nearest face on the mesh
2. Compute barycentric coordinates (u, v, w) on that face
3. Compute signed offset along the face normal

Output format per Gaussian:
- face_index: int32 - index of the nearest triangle
- bary_coords: float32[3] - barycentric coordinates (u, v, w)
- normal_offset: float32 - signed distance along face normal

Works on both CUDA (if available) and CPU.
"""

import argparse
import json
import struct
import sys
import time
from pathlib import Path
from typing import Tuple, Optional, NamedTuple

import numpy as np

# Try to import CUDA libraries
try:
    import cupy as cp
    from cupyx.scipy.spatial import KDTree as CuKDTree
    CUDA_AVAILABLE = True
except ImportError:
    CUDA_AVAILABLE = False
    cp = None

# CPU spatial queries
from scipy.spatial import KDTree


class MappingResult(NamedTuple):
    """Result of barycentric mapping computation."""
    face_indices: np.ndarray      # (N,) int32 - nearest face for each gaussian
    bary_coords: np.ndarray       # (N, 3) float32 - barycentric coordinates
    normal_offsets: np.ndarray    # (N,) float32 - signed offset along normal
    distances: np.ndarray         # (N,) float32 - distance to nearest face (for debugging)


def load_ply(ply_path: str) -> np.ndarray:
    """
    Load Gaussian splat positions from PLY file.
    Returns positions as (N, 3) float32 array.
    """
    with open(ply_path, 'rb') as f:
        # Parse header
        header_lines = []
        while True:
            line = f.readline().decode('ascii').strip()
            header_lines.append(line)
            if line == 'end_header':
                break
        
        # Parse header info
        vertex_count = 0
        properties = []
        is_binary = False
        is_little_endian = True
        
        for line in header_lines:
            if line.startswith('element vertex'):
                vertex_count = int(line.split()[-1])
            elif line.startswith('property'):
                parts = line.split()
                prop_type = parts[1]
                prop_name = parts[2]
                properties.append((prop_name, prop_type))
            elif line.startswith('format'):
                if 'binary_little_endian' in line:
                    is_binary = True
                    is_little_endian = True
                elif 'binary_big_endian' in line:
                    is_binary = True
                    is_little_endian = False
                elif 'ascii' in line:
                    is_binary = False
        
        # Find x, y, z property indices
        prop_names = [p[0] for p in properties]
        try:
            x_idx = prop_names.index('x')
            y_idx = prop_names.index('y')
            z_idx = prop_names.index('z')
        except ValueError:
            raise ValueError("PLY file must have x, y, z properties")
        
        # Build dtype for binary reading
        type_map = {
            'float': 'f4',
            'double': 'f8',
            'int': 'i4',
            'uint': 'u4',
            'short': 'i2',
            'ushort': 'u2',
            'char': 'i1',
            'uchar': 'u1',
        }
        
        if is_binary:
            dtype_list = []
            for name, ptype in properties:
                np_type = type_map.get(ptype, 'f4')
                dtype_list.append((name, np_type))
            
            endian = '<' if is_little_endian else '>'
            dtype = np.dtype([(n, endian + t) for n, t in dtype_list])
            
            data = np.frombuffer(f.read(vertex_count * dtype.itemsize), dtype=dtype)
            positions = np.column_stack([data['x'], data['y'], data['z']]).astype(np.float32)
        else:
            # ASCII format
            positions = np.zeros((vertex_count, 3), dtype=np.float32)
            for i in range(vertex_count):
                line = f.readline().decode('ascii').strip()
                values = line.split()
                positions[i, 0] = float(values[x_idx])
                positions[i, 1] = float(values[y_idx])
                positions[i, 2] = float(values[z_idx])
    
    print(f"Loaded {len(positions)} Gaussians from PLY")
    return positions


def load_glb(glb_path: str) -> Tuple[np.ndarray, np.ndarray]:
    """
    Load mesh vertices and faces from GLB file.
    Returns:
        vertices: (V, 3) float32 array
        faces: (F, 3) int32 array of vertex indices
    """
    try:
        import pygltflib
    except ImportError:
        raise ImportError("Please install pygltflib: pip install pygltflib")
    
    gltf = pygltflib.GLTF2().load(glb_path)
    
    # Get binary data
    if gltf.buffers[0].uri is None:
        # GLB format - binary chunk is embedded
        binary_blob = gltf._glb_data
    else:
        # GLTF with external bin file
        bin_path = Path(glb_path).parent / gltf.buffers[0].uri
        with open(bin_path, 'rb') as f:
            binary_blob = f.read()
    
    all_vertices = []
    all_faces = []
    vertex_offset = 0
    
    for mesh in gltf.meshes:
        for primitive in mesh.primitives:
            # Get position accessor
            pos_accessor_idx = primitive.attributes.POSITION
            pos_accessor = gltf.accessors[pos_accessor_idx]
            pos_buffer_view = gltf.bufferViews[pos_accessor.bufferView]
            
            # Read positions
            pos_start = pos_buffer_view.byteOffset + (pos_accessor.byteOffset or 0)
            pos_count = pos_accessor.count
            pos_data = binary_blob[pos_start:pos_start + pos_count * 12]
            vertices = np.frombuffer(pos_data, dtype=np.float32).reshape(-1, 3)
            
            # Get indices accessor
            if primitive.indices is not None:
                idx_accessor = gltf.accessors[primitive.indices]
                idx_buffer_view = gltf.bufferViews[idx_accessor.bufferView]
                idx_start = idx_buffer_view.byteOffset + (idx_accessor.byteOffset or 0)
                idx_count = idx_accessor.count
                
                # Determine index type
                component_type = idx_accessor.componentType
                if component_type == 5121:  # UNSIGNED_BYTE
                    idx_dtype = np.uint8
                    idx_size = 1
                elif component_type == 5123:  # UNSIGNED_SHORT
                    idx_dtype = np.uint16
                    idx_size = 2
                elif component_type == 5125:  # UNSIGNED_INT
                    idx_dtype = np.uint32
                    idx_size = 4
                else:
                    raise ValueError(f"Unknown index component type: {component_type}")
                
                idx_data = binary_blob[idx_start:idx_start + idx_count * idx_size]
                indices = np.frombuffer(idx_data, dtype=idx_dtype).astype(np.int32)
                faces = indices.reshape(-1, 3) + vertex_offset
            else:
                # No indices - assume triangle list
                faces = np.arange(len(vertices)).reshape(-1, 3) + vertex_offset
            
            all_vertices.append(vertices)
            all_faces.append(faces)
            vertex_offset += len(vertices)
    
    vertices = np.vstack(all_vertices).astype(np.float32)
    faces = np.vstack(all_faces).astype(np.int32)
    
    print(f"Loaded mesh: {len(vertices)} vertices, {len(faces)} faces")
    return vertices, faces


def compute_face_data(vertices: np.ndarray, faces: np.ndarray) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """
    Precompute face centroids and normals.
    Returns:
        centroids: (F, 3) face centers
        normals: (F, 3) unit face normals
        face_vertices: (F, 3, 3) vertices of each face
    """
    v0 = vertices[faces[:, 0]]
    v1 = vertices[faces[:, 1]]
    v2 = vertices[faces[:, 2]]
    
    centroids = (v0 + v1 + v2) / 3.0
    
    edge1 = v1 - v0
    edge2 = v2 - v0
    normals = np.cross(edge1, edge2)
    norms = np.linalg.norm(normals, axis=1, keepdims=True)
    norms = np.maximum(norms, 1e-10)  # Avoid division by zero
    normals = normals / norms
    
    face_vertices = np.stack([v0, v1, v2], axis=1)
    
    return centroids, normals, face_vertices


def point_to_triangle_distance_and_projection(
    points: np.ndarray,
    v0: np.ndarray,
    v1: np.ndarray,
    v2: np.ndarray
) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """
    Compute the closest point on triangle for each input point.
    Uses vectorized barycentric projection with edge/vertex clamping.
    
    Returns:
        closest_points: (N, 3) closest point on triangle
        bary_coords: (N, 3) barycentric coordinates (clamped to triangle)
        distances: (N,) distance from point to closest point
    """
    # Triangle edges
    edge0 = v1 - v0
    edge1 = v2 - v0
    
    # Vector from v0 to points
    v0_to_p = points - v0
    
    # Compute dot products
    d00 = np.sum(edge0 * edge0, axis=1)
    d01 = np.sum(edge0 * edge1, axis=1)
    d11 = np.sum(edge1 * edge1, axis=1)
    d20 = np.sum(v0_to_p * edge0, axis=1)
    d21 = np.sum(v0_to_p * edge1, axis=1)
    
    # Compute barycentric coordinates
    denom = d00 * d11 - d01 * d01
    denom = np.where(np.abs(denom) < 1e-10, 1e-10, denom)
    
    v = (d11 * d20 - d01 * d21) / denom
    w = (d00 * d21 - d01 * d20) / denom
    u = 1.0 - v - w
    
    # Clamp to triangle - project to nearest edge/vertex if outside
    # This is a simplified clamping that works well for most cases
    u_clamped = np.clip(u, 0, 1)
    v_clamped = np.clip(v, 0, 1)
    w_clamped = np.clip(w, 0, 1)
    
    # Renormalize
    total = u_clamped + v_clamped + w_clamped
    total = np.where(total < 1e-10, 1.0, total)
    u_clamped = u_clamped / total
    v_clamped = v_clamped / total
    w_clamped = w_clamped / total
    
    # Compute closest point using clamped barycentric coords
    closest = (u_clamped[:, np.newaxis] * v0 + 
               v_clamped[:, np.newaxis] * v1 + 
               w_clamped[:, np.newaxis] * v2)
    
    distances = np.linalg.norm(points - closest, axis=1)
    bary = np.stack([u_clamped, v_clamped, w_clamped], axis=1)
    
    return closest, bary, distances


def compute_mapping_cpu(
    gaussian_positions: np.ndarray,
    vertices: np.ndarray,
    faces: np.ndarray,
    k_nearest: int = 8
) -> MappingResult:
    """
    CPU implementation of barycentric mapping.
    Uses KDTree on face centroids for initial nearest-face search,
    then refines with exact point-to-triangle distance.
    """
    print("Computing mapping on CPU...")
    t0 = time.time()
    
    # Precompute face data
    centroids, normals, face_verts = compute_face_data(vertices, faces)
    
    # Build KDTree on centroids for fast approximate nearest face search
    print("  Building KDTree on face centroids...")
    tree = KDTree(centroids)
    
    n_gaussians = len(gaussian_positions)
    face_indices = np.zeros(n_gaussians, dtype=np.int32)
    bary_coords = np.zeros((n_gaussians, 3), dtype=np.float32)
    normal_offsets = np.zeros(n_gaussians, dtype=np.float32)
    min_distances = np.full(n_gaussians, np.inf, dtype=np.float32)
    
    # Process in batches for memory efficiency
    batch_size = 10000
    n_batches = (n_gaussians + batch_size - 1) // batch_size
    
    print(f"  Processing {n_gaussians} Gaussians in {n_batches} batches...")
    
    for batch_idx in range(n_batches):
        start = batch_idx * batch_size
        end = min(start + batch_size, n_gaussians)
        batch_points = gaussian_positions[start:end]
        
        # Find k nearest face centroids
        _, candidate_faces = tree.query(batch_points, k=k_nearest)
        
        # For each point, check all candidate faces and find the actual nearest
        for i, point in enumerate(batch_points):
            global_idx = start + i
            candidates = candidate_faces[i] if k_nearest > 1 else [candidate_faces[i]]
            
            best_dist = np.inf
            best_face = 0
            best_bary = np.array([1/3, 1/3, 1/3])
            
            for face_idx in candidates:
                if face_idx >= len(faces):
                    continue
                    
                v0, v1, v2 = face_verts[face_idx]
                
                # Compute exact distance to this triangle
                _, bary, dist = point_to_triangle_distance_and_projection(
                    point[np.newaxis, :],
                    v0[np.newaxis, :],
                    v1[np.newaxis, :],
                    v2[np.newaxis, :]
                )
                
                if dist[0] < best_dist:
                    best_dist = dist[0]
                    best_face = face_idx
                    best_bary = bary[0]
            
            face_indices[global_idx] = best_face
            bary_coords[global_idx] = best_bary
            min_distances[global_idx] = best_dist
            
            # Compute signed offset along normal
            # Project point onto face plane and get signed distance
            face_normal = normals[best_face]
            v0 = face_verts[best_face, 0]
            point_to_v0 = point - v0
            normal_offsets[global_idx] = np.dot(point_to_v0, face_normal)
        
        if (batch_idx + 1) % 10 == 0 or batch_idx == n_batches - 1:
            print(f"    Batch {batch_idx + 1}/{n_batches} complete")
    
    elapsed = time.time() - t0
    print(f"  CPU mapping completed in {elapsed:.2f}s")
    
    return MappingResult(face_indices, bary_coords, normal_offsets, min_distances)


def compute_mapping_cuda(
    gaussian_positions: np.ndarray,
    vertices: np.ndarray,
    faces: np.ndarray,
    k_nearest: int = 8
) -> MappingResult:
    """
    CUDA implementation of barycentric mapping using CuPy.
    Significantly faster for large point clouds.
    """
    print("Computing mapping on CUDA...")
    t0 = time.time()
    
    # Transfer to GPU
    positions_gpu = cp.asarray(gaussian_positions)
    vertices_gpu = cp.asarray(vertices)
    faces_gpu = cp.asarray(faces)
    
    # Compute face data on GPU
    v0 = vertices_gpu[faces_gpu[:, 0]]
    v1 = vertices_gpu[faces_gpu[:, 1]]
    v2 = vertices_gpu[faces_gpu[:, 2]]
    
    centroids = (v0 + v1 + v2) / 3.0
    
    edge1 = v1 - v0
    edge2 = v2 - v0
    normals = cp.cross(edge1, edge2)
    norms = cp.linalg.norm(normals, axis=1, keepdims=True)
    norms = cp.maximum(norms, 1e-10)
    normals = normals / norms
    
    n_gaussians = len(gaussian_positions)
    n_faces = len(faces)
    
    # For CUDA, we'll use a brute-force approach with batching
    # This is actually faster than KDTree for GPU due to parallelism
    batch_size = 1000  # Process this many Gaussians at a time
    face_batch_size = 50000  # Process this many faces at a time
    
    face_indices = cp.zeros(n_gaussians, dtype=cp.int32)
    bary_coords = cp.zeros((n_gaussians, 3), dtype=cp.float32)
    normal_offsets = cp.zeros(n_gaussians, dtype=cp.float32)
    min_distances = cp.full(n_gaussians, cp.inf, dtype=cp.float32)
    
    n_batches = (n_gaussians + batch_size - 1) // batch_size
    print(f"  Processing {n_gaussians} Gaussians in {n_batches} batches...")
    
    for batch_idx in range(n_batches):
        start = batch_idx * batch_size
        end = min(start + batch_size, n_gaussians)
        batch_points = positions_gpu[start:end]  # (B, 3)
        batch_size_actual = end - start
        
        batch_face_indices = cp.zeros(batch_size_actual, dtype=cp.int32)
        batch_bary = cp.zeros((batch_size_actual, 3), dtype=cp.float32)
        batch_min_dist = cp.full(batch_size_actual, cp.inf, dtype=cp.float32)
        batch_normal_offset = cp.zeros(batch_size_actual, dtype=cp.float32)
        
        # Process faces in batches to avoid memory issues
        for face_start in range(0, n_faces, face_batch_size):
            face_end = min(face_start + face_batch_size, n_faces)
            
            v0_batch = v0[face_start:face_end]  # (F, 3)
            v1_batch = v1[face_start:face_end]
            v2_batch = v2[face_start:face_end]
            normals_batch = normals[face_start:face_end]
            
            # Expand for broadcasting: points (B, 1, 3), vertices (1, F, 3)
            points_exp = batch_points[:, cp.newaxis, :]  # (B, 1, 3)
            v0_exp = v0_batch[cp.newaxis, :, :]  # (1, F, 3)
            v1_exp = v1_batch[cp.newaxis, :, :]
            v2_exp = v2_batch[cp.newaxis, :, :]
            
            # Compute barycentric coordinates for all point-face pairs
            edge0 = v1_exp - v0_exp  # (1, F, 3)
            edge1_bc = v2_exp - v0_exp
            v0_to_p = points_exp - v0_exp  # (B, F, 3)
            
            d00 = cp.sum(edge0 * edge0, axis=2)  # (1, F)
            d01 = cp.sum(edge0 * edge1_bc, axis=2)
            d11 = cp.sum(edge1_bc * edge1_bc, axis=2)
            d20 = cp.sum(v0_to_p * edge0, axis=2)  # (B, F)
            d21 = cp.sum(v0_to_p * edge1_bc, axis=2)
            
            denom = d00 * d11 - d01 * d01
            denom = cp.where(cp.abs(denom) < 1e-10, 1e-10, denom)
            
            v_bc = (d11 * d20 - d01 * d21) / denom  # (B, F)
            w_bc = (d00 * d21 - d01 * d20) / denom
            u_bc = 1.0 - v_bc - w_bc
            
            # Clamp to triangle
            u_clamped = cp.clip(u_bc, 0, 1)
            v_clamped = cp.clip(v_bc, 0, 1)
            w_clamped = cp.clip(w_bc, 0, 1)
            
            total = u_clamped + v_clamped + w_clamped
            total = cp.where(total < 1e-10, 1.0, total)
            u_clamped = u_clamped / total
            v_clamped = v_clamped / total
            w_clamped = w_clamped / total
            
            # Compute closest points
            closest = (u_clamped[:, :, cp.newaxis] * v0_exp + 
                      v_clamped[:, :, cp.newaxis] * v1_exp + 
                      w_clamped[:, :, cp.newaxis] * v2_exp)  # (B, F, 3)
            
            distances = cp.linalg.norm(points_exp - closest, axis=2)  # (B, F)
            
            # Find minimum distance face for this batch of faces
            min_face_idx = cp.argmin(distances, axis=1)  # (B,)
            min_dist = distances[cp.arange(batch_size_actual), min_face_idx]  # (B,)
            
            # Update where this batch has smaller distances
            update_mask = min_dist < batch_min_dist
            
            batch_face_indices = cp.where(update_mask, min_face_idx + face_start, batch_face_indices)
            batch_min_dist = cp.where(update_mask, min_dist, batch_min_dist)
            
            # Update barycentric coords
            u_best = u_clamped[cp.arange(batch_size_actual), min_face_idx]
            v_best = v_clamped[cp.arange(batch_size_actual), min_face_idx]
            w_best = w_clamped[cp.arange(batch_size_actual), min_face_idx]
            
            batch_bary = cp.where(update_mask[:, cp.newaxis], 
                                  cp.stack([u_best, v_best, w_best], axis=1),
                                  batch_bary)
            
            # Compute normal offset for best faces
            best_normals = normals_batch[min_face_idx - face_start]  # Need to handle offset
            best_v0 = v0_batch[min_face_idx - face_start]
            point_to_v0 = batch_points - best_v0
            new_normal_offset = cp.sum(point_to_v0 * best_normals, axis=1)
            batch_normal_offset = cp.where(update_mask, new_normal_offset, batch_normal_offset)
        
        # Store results
        face_indices[start:end] = batch_face_indices
        bary_coords[start:end] = batch_bary
        normal_offsets[start:end] = batch_normal_offset
        min_distances[start:end] = batch_min_dist
        
        if (batch_idx + 1) % 10 == 0 or batch_idx == n_batches - 1:
            print(f"    Batch {batch_idx + 1}/{n_batches} complete")
    
    # Transfer back to CPU
    face_indices = cp.asnumpy(face_indices)
    bary_coords = cp.asnumpy(bary_coords)
    normal_offsets = cp.asnumpy(normal_offsets)
    min_distances = cp.asnumpy(min_distances)
    
    elapsed = time.time() - t0
    print(f"  CUDA mapping completed in {elapsed:.2f}s")
    
    return MappingResult(face_indices, bary_coords, normal_offsets, min_distances)


def save_mapping(result: MappingResult, output_path: str, format: str = 'npz'):
    """Save mapping result to file."""
    if format == 'npz':
        np.savez_compressed(
            output_path,
            face_indices=result.face_indices,
            bary_coords=result.bary_coords,
            normal_offsets=result.normal_offsets,
            distances=result.distances
        )
    elif format == 'bin':
        # Binary format for Unity: 
        # Header: int32 count
        # Per gaussian: int32 face_idx, float32[3] bary, float32 offset
        with open(output_path, 'wb') as f:
            n = len(result.face_indices)
            f.write(struct.pack('<i', n))
            for i in range(n):
                f.write(struct.pack('<i', result.face_indices[i]))
                f.write(struct.pack('<3f', *result.bary_coords[i]))
                f.write(struct.pack('<f', result.normal_offsets[i]))
    elif format == 'json':
        data = {
            'count': len(result.face_indices),
            'face_indices': result.face_indices.tolist(),
            'bary_coords': result.bary_coords.tolist(),
            'normal_offsets': result.normal_offsets.tolist(),
            'distances': result.distances.tolist()
        }
        with open(output_path, 'w') as f:
            json.dump(data, f)
    else:
        raise ValueError(f"Unknown format: {format}")
    
    print(f"Saved mapping to {output_path}")


def reconstruct_positions(
    vertices: np.ndarray,
    faces: np.ndarray,
    result: MappingResult
) -> np.ndarray:
    """
    Reconstruct Gaussian positions from mapping data.
    Useful for verification and animation.
    """
    v0 = vertices[faces[result.face_indices, 0]]
    v1 = vertices[faces[result.face_indices, 1]]
    v2 = vertices[faces[result.face_indices, 2]]
    
    # Barycentric interpolation
    positions = (result.bary_coords[:, 0:1] * v0 +
                 result.bary_coords[:, 1:2] * v1 +
                 result.bary_coords[:, 2:3] * v2)
    
    # Compute face normals
    edge1 = v1 - v0
    edge2 = v2 - v0
    normals = np.cross(edge1, edge2)
    norms = np.linalg.norm(normals, axis=1, keepdims=True)
    norms = np.maximum(norms, 1e-10)
    normals = normals / norms
    
    # Add normal offset
    positions = positions + result.normal_offsets[:, np.newaxis] * normals
    
    return positions


def main():
    parser = argparse.ArgumentParser(
        description='Compute barycentric mapping from Gaussian splats to mesh faces'
    )
    parser.add_argument('ply_file', help='Input PLY file with Gaussian splats')
    parser.add_argument('glb_file', help='Input GLB file with mesh')
    parser.add_argument('-o', '--output', default='mapping.npz',
                        help='Output file (default: mapping.npz)')
    parser.add_argument('-f', '--format', choices=['npz', 'bin', 'json'], default='npz',
                        help='Output format (default: npz)')
    parser.add_argument('--cpu', action='store_true',
                        help='Force CPU computation even if CUDA is available')
    parser.add_argument('-k', '--k-nearest', type=int, default=8,
                        help='Number of nearest faces to check (default: 8)')
    parser.add_argument('--verify', action='store_true',
                        help='Verify mapping by reconstructing positions')
    
    args = parser.parse_args()
    
    # Load data
    print(f"Loading Gaussian splats from {args.ply_file}...")
    gaussian_positions = load_ply(args.ply_file)
    
    print(f"Loading mesh from {args.glb_file}...")
    vertices, faces = load_glb(args.glb_file)
    
    # Compute mapping
    use_cuda = CUDA_AVAILABLE and not args.cpu
    
    if use_cuda:
        print("CUDA is available, using GPU acceleration")
        result = compute_mapping_cuda(gaussian_positions, vertices, faces, args.k_nearest)
    else:
        if not args.cpu and not CUDA_AVAILABLE:
            print("CUDA not available (install cupy for GPU acceleration)")
        print("Using CPU computation")
        result = compute_mapping_cpu(gaussian_positions, vertices, faces, args.k_nearest)
    
    # Print statistics
    print("\nMapping Statistics:")
    print(f"  Total Gaussians: {len(result.face_indices)}")
    print(f"  Distance to faces - min: {result.distances.min():.6f}, "
          f"max: {result.distances.max():.6f}, mean: {result.distances.mean():.6f}")
    print(f"  Normal offsets - min: {result.normal_offsets.min():.6f}, "
          f"max: {result.normal_offsets.max():.6f}, mean: {result.normal_offsets.mean():.6f}")
    
    # Verify if requested
    if args.verify:
        print("\nVerifying mapping by reconstructing positions...")
        reconstructed = reconstruct_positions(vertices, faces, result)
        error = np.linalg.norm(reconstructed - gaussian_positions, axis=1)
        print(f"  Reconstruction error - max: {error.max():.6f}, mean: {error.mean():.6f}")
    
    # Save result
    save_mapping(result, args.output, args.format)
    
    print("\nDone!")


if __name__ == '__main__':
    main()


