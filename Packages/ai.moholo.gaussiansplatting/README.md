# Gaussian Splatting for Unity

A Unity package for rendering Gaussian Splatting point clouds. Supports runtime PLY loading, GPU sorting, and full spherical harmonics rendering.

## Features

- **Runtime PLY Loading**: Load Gaussian Splat PLY files at runtime from StreamingAssets or URLs
- **GPU Sorting**: Fast GPU-based radix sort for efficient back-to-front rendering
- **Spherical Harmonics**: Full support for SH coefficients (bands 1-3)
- **URP Integration**: Works with Universal Render Pipeline via Render Features
- **Editor Import**: Import PLY files directly in the Unity Editor
- **Cross-Platform**: Supports Windows, Mac, Linux, Android, iOS, and WebGL

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

### Method 1: Editor Import

1. Place your `.ply` file in your project's `Assets` folder
2. Unity will automatically import it as a `GaussianSplatAsset`
3. Create a Material using the `GaussianSplatting/Gaussian Splat` shader
4. Add a `GaussianSplatRenderer` component to a GameObject
5. Assign the imported asset and material to the renderer

### Method 2: Runtime Loading

1. Place your `.ply` file in `Assets/StreamingAssets/GaussianSplatting/`
2. Create a Material using the `GaussianSplatting/Gaussian Splat` shader
3. Add a `GaussianSplatRenderer` component to a GameObject
4. Configure the renderer:
   - Set `Ply File Name` to your PLY filename (e.g., "mysplat.ply")
   - Assign the material
   - Optionally set a target camera

5. Press Play - the PLY will load automatically!

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
- `UseRenderFeature`: Use URP Render Feature for proper matrix setup
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

- **Use Bitonic GPU Sorting**: For most mobile and desktop scenarios, Bitonic sort is significantly faster and more stable than the default Radix sort. It is now the recommended sorting algorithm for Gaussian Splatting.
- Adjust `SortEveryNFrames` to reduce sorting overhead
- Use `MaxSplatsToLoad` to limit splat count for testing
- Enable `UseRenderFeature` for proper URP integration

## Platform Notes

### Android/iOS/WebGL
- PLY files must be in `StreamingAssets/GaussianSplatting/`
- Loading uses `UnityWebRequest` automatically
- No Editor-time import available

### Desktop (Windows/Mac/Linux)
- Supports both Editor import and runtime loading
- Can use direct file paths or StreamingAssets

## License

This is a commercial asset sold through the Unity Asset Store. Purchasers may use and modify this asset for their projects, but may not redistribute or resell it. See LICENSE.md for complete terms.

## Support

For issues and questions, please refer to the package documentation or contact the package author.

