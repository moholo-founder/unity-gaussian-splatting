# Package Preparation Guide for Unity Asset Store

## ✅ What I've Done

1. **Created `Samples~/Demo` folder** - Moved your scene and prefab here (Unity Asset Store standard)
2. **Created `.sample.json`** - Configures the sample for Package Manager
3. **Updated `package.json`** - Added samples array entry
4. **Created `CHANGELOG.md`** - Version history
5. **Created `LICENSE.md`** - MIT License (update if you prefer different license)
6. **Created `ASSET_STORE_CHECKLIST.md`** - Complete submission checklist

## ⚠️ What You Need to Do

### 1. Handle URP Assets

**Important:** Your scene may reference URP assets for local testing, but they shouldn't be in the package.

**For your local development:**
- Keep URP assets in `Demo/` folder for testing ✅
- Use them to verify your scene works

**For package submission:**
- **DO NOT include** URP assets in `Samples~/Demo/` ❌
- The scene uses `m_RendererIndex: -1` which means "use default renderer"
- Users will set up their own URP in Project Settings
- Scene will work with any URP setup

**Files to exclude from package:**
- ❌ `Demo/GaussianSplattingURP.asset` (keep locally, don't package)
- ❌ `Demo/GaussianSplattingURP_Renderer.asset` (keep locally, don't package)
- ❌ `Demo/DefaultVolumeProfile.asset` (keep locally, don't package)
- ❌ `Demo/UniversalRenderPipelineGlobalSettings.asset` (keep locally, don't package)

**Files to include in package:**
- ✅ `Samples~/Demo/DemoScene.unity` (your example scene)
- ✅ `Samples~/Demo/GSExample.prefab` (your example prefab)
- ✅ `Samples~/Demo/README.md` (setup instructions)

**See `URP_ASSETS_SOLUTION.md` for detailed explanation.**

### 2. Update Sample Scene

**✅ Already Verified!** Your scene is properly configured:
- ✅ No references to removed URP assets (verified)
- ✅ Uses standard URP runtime components (correct)
- ✅ Material reference is valid (GUID matches)
- ✅ Documentation added (`Samples~/Demo/README.md`)

**See `SCENE_VERIFICATION.md` for detailed verification steps.**

**To test:**
1. Create a fresh Unity project with URP
2. Import your package
3. Import the sample via Package Manager
4. Set up URP Render Feature (see README in sample folder)
5. Open scene and press Play

### 3. Test the Package

Before submitting:

1. **Export the package:**
   ```bash
   # In Unity, right-click the package folder → Export Package
   # Or use: tar -czf package.tgz Packages/ai.moholo.gaussiansplatting
   ```

2. **Test in a fresh Unity project:**
   - Create a new Unity 2021.3+ project with URP
   - Import your package
   - Import the sample via Package Manager
   - Verify everything works

3. **Verify sample import:**
   - Go to **Window** → **Package Manager**
   - Select your package
   - Click **Import** on the "Gaussian Splatting Demo" sample
   - Verify scene opens and works

### 4. Update Documentation

Review and update:
- `README.md` - Ensure all instructions are clear
- `CHANGELOG.md` - Update date and version details
- `LICENSE.md` - Update copyright year and name if needed

### 5. Package Structure (Final)

Your package should look like this:

```
ai.moholo.gaussiansplatting/
├── package.json
├── README.md
├── CHANGELOG.md
├── LICENSE.md
├── ASSET_STORE_CHECKLIST.md
├── Samples~/
│   └── Demo/
│       ├── .sample.json
│       ├── DemoScene.unity
│       └── GSExample.prefab
├── Runtime/
│   ├── Scripts/
│   ├── Shaders/
│   ├── Materials/
│   └── Resources/
└── Editor/
    └── Scripts/
```

## Answers to Your Questions

### Q: Should I include the URP asset with my package?

**A: NO** - URP assets are project-specific. Each user's project has its own URP Renderer configuration. Including them would:
- Overwrite user's settings
- Cause conflicts
- Not work across different Unity versions/projects

**Instead:** Document how users should set up their own URP Renderer and add the Render Feature (already in README).

### Q: How can I include example scene and prefab?

**A: ✅ Already done!** Your scene and prefab are now in `Samples~/Demo/`:
- Unity Package Manager automatically detects samples in `Samples~` folders
- Users can import them via Package Manager UI
- The `.sample.json` file provides description and metadata

### Q: How do I prepare it for Unity Store?

**A: Follow these steps:**

1. **Remove URP assets** (see above)
2. **Test in clean project** (see above)
3. **Create package export:**
   - Option A: Unity → Right-click package → Export Package
   - Option B: Command line: `tar -czf gaussian-splatting.tgz Packages/ai.moholo.gaussiansplatting`
4. **Prepare store assets:**
   - Screenshots (at least 3, recommended 5-7)
   - Promotional image (628x628px)
   - Icon (128x128px)
   - Video demo (optional)
5. **Submit via Unity Asset Store Publisher Portal**

## Next Steps

1. ✅ Remove URP assets from `Demo/` folder (keep only scene/prefab)
2. ✅ Test sample scene works without URP assets
3. ✅ Update `CHANGELOG.md` with actual release date
4. ✅ Test package import in fresh project
5. ✅ Prepare store listing materials
6. ✅ Submit to Unity Asset Store

## Need Help?

- Unity Asset Store Submission Guide: https://assetstore.unity.com/publishing
- Package Manager Samples: https://docs.unity3d.com/Manual/cus-samples.html
- UPM Package Structure: https://docs.unity3d.com/Manual/upm-manifestPkg.html

