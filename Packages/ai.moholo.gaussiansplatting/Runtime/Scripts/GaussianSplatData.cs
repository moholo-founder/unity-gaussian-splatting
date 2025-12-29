using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GaussianSplatting
{
    public class GaussianSplatData
    {
        public int Count;
        public Vector3[] Centers = Array.Empty<Vector3>();
        public Vector4[] Rotations = Array.Empty<Vector4>(); // (x,y,z,w)
        public Vector3[] Scales = Array.Empty<Vector3>();    // exp() already applied
        public Vector4[] Colors = Array.Empty<Vector4>();    // rgb in [0..1], a in [0..1]

        // Optional SH (not yet wired into the shader in this minimal Unity port)
        public int ShBands; // 0..3
        public int ShCoeffsPerSplat;              // 0, 3, 8, 15
        public Vector3[] ShCoeffs = Array.Empty<Vector3>(); // length = Count * ShCoeffsPerSplat
    }
}