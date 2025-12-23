# Gaussian Splatting Demo Scene

This demo scene demonstrates how to use the Gaussian Splatting package.

## Scene Setup

The scene includes:
- **Main Camera** - Configured for URP with proper camera settings
- **Directional Light** - Basic lighting setup
- **GSExample** - Prefab instance showing Gaussian Splat rendering

## Setup Instructions

### 1. URP Configuration (Required)

Before using this scene, you need to set up URP in your project:

1. **Create/Assign URP Asset:**
   - Go to **Project Settings** → **Graphics**
   - Assign a URP Asset (or create one: **Assets** → **Create** → **Rendering** → **URP Asset**)

2. **Add Render Feature:**
   - Select your URP Renderer Asset
   - Click **Add Renderer Feature** → **Gaussian Splat Render Feature**
   - This enables proper rendering with correct matrix setup

3. **Enable Render Feature on Component:**
   - Select the **GSExample** GameObject in the scene
   - In the **Gaussian Splat Renderer** component, enable **Use Render Feature**

### 2. Loading PLY Files

The `GSExample` prefab is configured to load from a URL by default. You can:

**Option A: Use URL (Default)**
- The `PlyUrl` field is already set to a sample URL
- The splat will load automatically when you press Play
- Works in both Editor and Play mode

**Option B: Use Local File**
- Clear the `PlyUrl` field
- Place your `.ply` file in `Assets/StreamingAssets/GaussianSplatting/`
- Set `Ply File Name` to your filename (e.g., "mysplat.ply")

### 3. Material Setup

The prefab references a default material (`GaussianSplat.mat`) included in the package. You can:
- Use the default material as-is
- Create your own material using the `GaussianSplatting/Gaussian Splat` shader
- Adjust shader properties like `_AlphaClip` and `_MinAlpha`

## Troubleshooting

**Nothing visible?**
- Make sure URP Render Feature is added to your URP Renderer
- Check that `Use Render Feature` is enabled on the component
- Verify the PLY URL is accessible or the local file exists
- Check the Console for any error messages

**Rendering issues?**
- Ensure your project is using URP (not Built-in Render Pipeline)
- Verify the URP Renderer Asset is assigned in Project Settings
- Check that the Render Feature is enabled in the URP Renderer

## Notes

- The scene uses standard URP components (camera data, light data) which are fine to include
- No project-specific URP assets are referenced
- The scene will work with any URP setup once the Render Feature is configured

