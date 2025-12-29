# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased] - Architecture Refactor

### Changed
- **Separated loading from rendering**: `GaussianSplatRenderer` no longer handles data loading. Use loader classes like `RuntimePlyLoader` to load data and assign it to a renderer. This enables flexible loading strategies for different use cases (runtime loading, streaming, custom formats, etc.).
- **Replaced ScriptableObject with plain object**: `GaussianSplatData` is now a plain C# class instead of `GaussianSplatAsset` ScriptableObject, avoiding serialization overhead for always-dynamic loading. A wrapper could be written if serialization is needed in the future.
- **Renamed Samples~ to Samples**: The tilde suffix hid the folder from Unity's Project window.

### Added
- `RuntimePlyLoader`: Standalone component that loads PLY files. Drag a PLY file onto the PlyAsset field and set the TargetRenderer to load data into a renderer.
- `GaussianSplatData`: Plain C# data container class for splat data.
- Mobile GPU sorting shaders using shared memory atomics for Quest/Android compatibility (replaces wave intrinsics which aren't supported on mobile GPUs).
- Platform detection for automatic mobile shader selection in `GaussianSplatGPUSorter`.

### Removed
- `GaussianSplatAsset` ScriptableObject
- `PlyGaussianSplatImporter` editor script
- Internal loading logic from `GaussianSplatRenderer` (PlyUrl, PlyFileName fields)

### Migration Guide
1. Replace `GaussianSplatAsset` references with `GaussianSplatData`
2. Add a `RuntimePlyLoader` component to your scene
3. Assign your `GaussianSplatRenderer` to the RuntimePlyLoader's TargetRenderer field
4. Drag your PLY file onto the RuntimePlyLoader's PlyAsset field
5. The loader will automatically load the data into the renderer at runtime

## [1.0.0] - 2024-XX-XX

### Added
- Initial release of Gaussian Splatting package for Unity
- Runtime PLY file loading from StreamingAssets or URLs
- GPU-based radix sort for efficient back-to-front rendering
- Full support for spherical harmonics coefficients (bands 1-3)
- URP integration via ScriptableRendererFeature
- Editor-time PLY import and preview
- Cross-platform support (Windows, Mac, Linux, Android, iOS, WebGL)
- Example scene and prefab demonstrating usage

### Features
- `GaussianSplatRenderer` component for rendering Gaussian Splats
- `GaussianSplatRenderFeature` for proper URP integration
- `PlyGaussianSplatLoader` for parsing PLY files
- `GaussianSplatAsset` ScriptableObject for storing splat data
- GPU compute shader for fast sorting
- Material and shader for rendering splats

