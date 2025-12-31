Gaussian Splatting - StreamingAssets

Place your .ply gaussian splat files in this folder (Assets/StreamingAssets/GaussianSplatting/) 
for runtime loading on Android and other platforms.

Included files:
- testsplat.ply (example file for testing)

Example usage:
1. Copy your .ply file to this folder (e.g., mysplat.ply)
2. Add RuntimePlyLoader component to your GameObject
3. Set the plyFileName field to your file name (e.g., "mysplat.ply")
4. The RuntimePlyLoader will automatically find and assign to GaussianSplatRenderer on the same GameObject

The RuntimePlyLoader uses UnityWebRequest for cross-platform compatibility, which is 
required for Android builds where StreamingAssets are packaged in the APK/JAR and 
direct file access is not available.

Path structure:
- Editor: Assets/StreamingAssets/GaussianSplatting/yourfile.ply
- Runtime: Application.streamingAssetsPath + "/GaussianSplatting/yourfile.ply"
- Android: jar:file:///data/app/.../base.apk!/assets/GaussianSplatting/yourfile.ply

Note: The PLY files in Assets/GaussianSplatting/*.ply are for Editor-time import only.
For runtime loading, files must be in this StreamingAssets folder.

