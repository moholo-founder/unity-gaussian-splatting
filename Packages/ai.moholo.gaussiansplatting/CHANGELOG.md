# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

