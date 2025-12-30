# Gaussian Splatting for Unity

A Unity package for rendering Gaussian Splatting point clouds. Supports runtime PLY loading, GPU sorting, and full spherical harmonics rendering.

## Features

- **Runtime PLY Loading**: Load Gaussian Splat PLY files at runtime from StreamingAssets or URLs
- **GPU Sorting**: Fast GPU-based Bitonic sort (fastest) and Radix sort for efficient back-to-front rendering
- **Spherical Harmonics**: Full support for SH coefficients (bands 1-3) on Vulkan/Metal/D3D
- **URP Integration**: Works with Universal Render Pipeline via Render Features
- **Automatic API Selection**: Unified shader automatically selects optimal path for Vulkan/Metal/D3D vs GLES 3.1
- **Cross-Platform**: Supports Windows, Mac, Linux, Android, and iOS

## Requirements

- Unity 2021.3 or later
- Universal Render Pipeline (URP) 12.0.0 or later

## Installation

1. Add this package to your Unity project's `Packages/manifest.json`:
   ```json
   {
     "dependencies": {
       "ai.moholo.gaussiansplatting": "1.0.0"
     }
   }
   ```

2. Or install via Unity Package Manager using a local path or Git URL.

## Quick Start

### Runtime Loading

1. Place your `.ply` file in `Assets/StreamingAssets/GaussianSplatting/`
2. Create a Material using the `GaussianSplatting/Gaussian Splat` shader
3. Add a `GaussianSplatRenderer` component to a GameObject
4. Configure the renderer:
   - Set `Ply File Name` to your PLY filename (e.g., "mysplat.ply")
   - Assign the material
   - Optionally set a target camera

5. Press Play - the PLY will load automatically!

## Graphics API Support

The package automatically selects the optimal rendering path based on your device's graphics API:

### Three Graphics APIs Supported

1. **Vulkan/Metal/D3D11/D3D12** (Desktop & Modern Mobile)
   - Full-featured path with separate buffers
   - Supports Spherical Harmonics (SH) lighting
   - Uses standard GPU sorting with wave intrinsics
   - Best performance on desktop and modern mobile devices

2. **OpenGL ES 3.1** (Android Mobile)
   - Optimized path with packed buffers (exactly 4 SSBOs for GLES 3.1 strict compatibility)
   - Uses precomputed 3D covariance (computed on CPU at load time)
   - No SH support (GLES limitation)
   - Automatically selected on GLES 3.1 devices

**Automatic Selection**: The unified `GaussianSplatting/Gaussian Splat` shader contains SubShaders for each API. Unity automatically selects the correct SubShader based on the device's graphics API - you only need one material that works everywhere!

## Demo Scene

The package includes a demo scene in `Samples~/Demo/`:
- `DemoScene.unity`: Example scene with GSExample prefab
- `GSExample.prefab`: Pre-configured Gaussian Splat renderer with sample PLY URL
- `README.md`: Demo-specific documentation

Import the samples via Unity Package Manager to access the demo scene.

### URP Setup

For best results with URP, add the `GaussianSplatRenderFeature` to your URP Renderer:

1. Create or select your URP Renderer Asset:
   - Go to **Project Settings** → **Graphics** → **Scriptable Render Pipeline Settings**
   - Assign your URP Asset (or create one via **Assets** → **Create** → **Rendering** → **URP Asset**)
2. Add the Render Feature:
   - Select your URP Renderer Asset (found in your project's URP settings)
   - Click **Add Renderer Feature** → **Gaussian Splat Render Feature**
3. Enable `Use Render Feature` on your `GaussianSplatRenderer` component

**Note:** The package does not include URP assets as they are project-specific. You must configure your own URP Renderer.

## Components

### GaussianSplatRenderer

Main component for rendering Gaussian Splats.

**Properties:**
- `PlyUrl`: Full URL to load PLY from (optional, takes precedence over PlyFileName)
- `PlyFileName`: PLY filename in StreamingAssets/GaussianSplatting/ folder
- `Material`: Material using the Gaussian Splat shader
- `TargetCamera`: Optional camera to render to (defaults to main camera)
- `SortEveryNFrames`: How often to sort splats (1 = every frame)
- `UseRenderFeature`: Use URP Render Feature for proper matrix setup (recommended)
- `Sorting Algorithm`: Sort algorithm (Bitonic is default and fastest, Radix is alternative, None is for testing only and will look wrong)
- `EnableFrustumCulling`: Enable GPU frustum culling to skip off-screen splats
- `ScaleMultiplier`: Scale multiplier for all splats
- `MaxSplatsToLoad`: Limit number of splats to load (0 = load all)

### RuntimePlyLoader

Helper component for runtime PLY loading (optional, `GaussianSplatRenderer` can load directly).

**Properties:**
- `plyFileName`: Name of PLY file in StreamingAssets/GaussianSplatting/
- `loadOnStart`: Automatically load on Start
- `targetRenderer`: Optional renderer to assign loaded asset to

## PLY File Format

The package expects Gaussian Splat PLY files with the following properties:
- `x`, `y`, `z`: Position
- `rot_0`, `rot_1`, `rot_2`, `rot_3`: Rotation quaternion (or `q0`, `q1`, `q2`, `q3`)
- `scale_0`, `scale_1`, `scale_2`: Scale (log-space)
- `f_dc_0`, `f_dc_1`, `f_dc_2`: Base color (spherical harmonics DC term)
- `opacity`: Opacity (logit-space)
- `f_rest_*`: Optional spherical harmonics coefficients (bands 1-3)

## Coordinate Systems

The package supports coordinate conversion options:
- `None`: Use PLY coordinates as-is
- `RightHandedToUnity`: Convert right-handed (OpenGL) to Unity left-handed (most common)
- `ZUpRightHandedToUnity`: Convert Z-up right-handed to Y-up left-handed

## Shader Properties

The Gaussian Splat shader has these properties:
- `_AlphaClip`: Alpha clipping threshold (0-1)
- `_MinAlpha`: Minimum alpha value (0-0.02)

## Performance Tips

- **Bitonic Sort is Fastest**: Bitonic sort is the default and fastest sorting algorithm for Gaussian Splatting. It provides the best performance on both mobile and desktop platforms with fewer GPU dispatches than Radix sort.
- Adjust `SortEveryNFrames` to reduce sorting overhead (e.g., sort every 2-3 frames for static scenes)
- Use `MaxSplatsToLoad` to limit splat count for testing/debugging
- Enable `UseRenderFeature` for proper URP integration and correct matrix setup
- Disable `EnableFrustumCulling` if you experience culling issues (may help with matrix-related problems)

## Platform Notes

### VR Platforms
- **Meta Quest**: Supported via OpenGL ES 3.1 and Vulkan
  - OpenGL ES 3.1: Uses optimized packed buffer path
  - Vulkan: Uses full-featured path with Spherical Harmonics
- **Apple Vision Pro**: Supported via Metal (uses full-featured path with Spherical Harmonics)
- Both platforms automatically select the correct graphics API (Metal, Vulkan, or OpenGL ES) based on device capabilities

### Android/iOS
- PLY files must be in `StreamingAssets/GaussianSplatting/`
- Loading uses `UnityWebRequest` automatically
- Uses OpenGL ES 3.1 graphics API with optimized packed buffer path

### Desktop (Windows/Mac/Linux)
- Supports runtime loading from StreamingAssets or URLs
- Uses Vulkan/Metal/D3D11/D3D12 graphics APIs with full feature set

## License

This is a commercial asset sold through the Unity Asset Store. Purchasers may use and modify this asset for their projects, but may not redistribute or resell it. See LICENSE.md for complete terms.

## Support

For issues and questions, please refer to the package documentation or contact the package author.

