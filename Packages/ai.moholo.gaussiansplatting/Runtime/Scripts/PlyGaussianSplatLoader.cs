using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// Coordinate system conversion options for importing gaussian splats.
    /// Most gaussian splat data comes from right-handed coordinate systems.
    /// </summary>
    public enum CoordinateConversion
    {
        None,
        /// <summary>Right-handed to Unity left-handed (negate Z position, adjust quaternion)</summary>
        RightHandedToUnity,
        /// <summary>Z-up right-handed to Unity Y-up left-handed</summary>
        ZUpRightHandedToUnity
    }

    public sealed class PlyGaussianSplat
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

    /// <summary>
    /// Minimal PLY loader for gaussian splat PLYs that include:
    /// x,y,z, rot_0..3, scale_0..2, f_dc_0..2, opacity, optional f_rest_*
    /// Supports ascii and binary_little_endian with float properties.
    /// </summary>
    public static class PlyGaussianSplatLoader
    {
        private const float SH_C0 = 0.28209479177387814f;

        private enum PlyFormat
        {
            Ascii,
            BinaryLittleEndian
        }

        private readonly struct PropertySpec
        {
            public readonly string Name;
            public readonly string Type;
            public PropertySpec(string name, string type) { Name = name; Type = type; }
        }

        private sealed class VertexLayout
        {
            public int Count;
            public readonly List<PropertySpec> Props = new List<PropertySpec>();
        }

        public static PlyGaussianSplat Load(TextAsset plyTextAsset, CoordinateConversion conversion = CoordinateConversion.RightHandedToUnity)
        {
            if (plyTextAsset == null) throw new ArgumentNullException(nameof(plyTextAsset));

            // Prefer bytes for binary PLYs.
            var bytes = plyTextAsset.bytes;
            if (bytes == null || bytes.Length == 0)
                throw new InvalidDataException("PLY asset has no data.");

            return Load(bytes, conversion);
        }

        public static PlyGaussianSplat Load(byte[] plyBytes, CoordinateConversion conversion = CoordinateConversion.RightHandedToUnity)
        {
            if (plyBytes == null) throw new ArgumentNullException(nameof(plyBytes));
            if (plyBytes.Length == 0) throw new InvalidDataException("PLY data has no bytes.");

            // IMPORTANT: for binary PLY, do NOT rely on StreamReader.BaseStream.Position for header end.
            // StreamReader buffers ahead, so BaseStream.Position is not the exact "end_header" byte offset.
            // Scan raw bytes for end_header newline instead.
            int headerBytes = FindHeaderEndOffset(plyBytes);

            using var ms = new MemoryStream(plyBytes, writable: false);
            // parse header text exactly
            string headerText = Encoding.ASCII.GetString(plyBytes, 0, headerBytes);
            var (format, vertexLayout) = ParseHeader(headerText);

            ms.Position = headerBytes;
            using var reader = new StreamReader(ms, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);

            // Extract required property indices.
            int Idx(string name) => vertexLayout.Props.FindIndex(p => p.Name == name);

            var idxX = Idx("x");
            var idxY = Idx("y");
            var idxZ = Idx("z");

            var idxRw = Idx("rot_0");
            var idxRx = Idx("rot_1");
            var idxRy = Idx("rot_2");
            var idxRz = Idx("rot_3");
            
            // Fallback for different property names
            if (idxRw < 0) idxRw = Idx("q0");
            if (idxRx < 0) idxRx = Idx("q1");
            if (idxRy < 0) idxRy = Idx("q2");
            if (idxRz < 0) idxRz = Idx("q3");

            var idxSx = Idx("scale_0");
            var idxSy = Idx("scale_1");
            var idxSz = Idx("scale_2");

            var idxCr = Idx("f_dc_0");
            var idxCg = Idx("f_dc_1");
            var idxCb = Idx("f_dc_2");
            var idxA = Idx("opacity");

            if (idxX < 0 || idxY < 0 || idxZ < 0)
                throw new InvalidDataException("PLY is missing x/y/z properties.");
            if (idxRw < 0 || idxRx < 0 || idxRy < 0 || idxRz < 0)
                throw new InvalidDataException("PLY is missing rot_0..rot_3 properties.");
            if (idxSx < 0 || idxSy < 0 || idxSz < 0)
                throw new InvalidDataException("PLY is missing scale_0..scale_2 properties.");
            if (idxCr < 0 || idxCg < 0 || idxCb < 0 || idxA < 0)
                throw new InvalidDataException("PLY is missing f_dc_0..2 and/or opacity properties.");

            // SH bands detection: same sizes as GSplatData.shBands
            int shProps = 0;
            for (int i = 0; i < 45; i++)
            {
                if (Idx($"f_rest_{i}") < 0) break;
                shProps++;
            }
            int shBands = shProps switch
            {
                9 => 1,
                24 => 2,
                45 => 3,
                _ => 0
            };
            int shCoeffsPerSplat = shBands switch
            {
                1 => 3,
                2 => 8,
                3 => 15,
                _ => 0
            };

            int shFloatCount = shCoeffsPerSplat * 3;
            var shIdx = new int[shFloatCount];
            if (shCoeffsPerSplat > 0)
            {
                for (int i = 0; i < shFloatCount; i++)
                {
                    shIdx[i] = Idx($"f_rest_{i}");
                    if (shIdx[i] < 0)
                        throw new InvalidDataException($"PLY indicates SH bands={shBands} but is missing f_rest_{i}.");
                }
            }

            var count = vertexLayout.Count;
            var centers = new Vector3[count];
            var rotations = new Vector4[count];
            var scales = new Vector3[count];
            var colors = new Vector4[count];
            var shCoeffs = shCoeffsPerSplat > 0 ? new Vector3[count * shCoeffsPerSplat] : Array.Empty<Vector3>();

            if (format == PlyFormat.Ascii)
            {
                ReadAsciiVertices(reader, vertexLayout, count, (vals, i) =>
                {
                    Fill(vals, i);
                });
            }
            else
            {
                using var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true);
                ReadBinaryLittleEndianVertices(br, vertexLayout, count, (vals, i) =>
                {
                    Fill(vals, i);
                });
            }

            return new PlyGaussianSplat
            {
                Count = count,
                Centers = centers,
                Rotations = rotations,
                Scales = scales,
                Colors = colors,
                ShBands = shBands
                ,
                ShCoeffsPerSplat = shCoeffsPerSplat,
                ShCoeffs = shCoeffs
            };

            void Fill(float[] vals, int i)
            {
                float px = vals[idxX];
                float py = vals[idxY];
                float pz = vals[idxZ];

                // PLY uses rot_0 as w, rot_1..3 as x,y,z (matching the JS code)
                float qx = vals[idxRx];
                float qy = vals[idxRy];
                float qz = vals[idxRz];
                float qw = vals[idxRw];

                float sx = Mathf.Exp(vals[idxSx]);
                float sy = Mathf.Exp(vals[idxSy]);
                float sz = Mathf.Exp(vals[idxSz]);

                // Apply coordinate system conversion
                switch (conversion)
                {
                    case CoordinateConversion.RightHandedToUnity:
                        // Right-handed (OpenGL/WebGL) to left-handed (Unity): negate Z
                        pz = -pz;
                        // For quaternion: negate x and y to flip the handedness
                        qx = -qx;
                        qy = -qy;
                        break;

                    case CoordinateConversion.ZUpRightHandedToUnity:
                        // Z-up right-handed to Y-up left-handed: swap Y/Z, negate new Z
                        float tmpP = py;
                        py = pz;
                        pz = -tmpP;
                        // Quaternion: swap y/z components, negate to flip handedness
                        float tmpQ = qy;
                        qy = qz;
                        qz = -tmpQ;
                        // Also swap scale Y/Z
                        float tmpS = sy;
                        sy = sz;
                        sz = tmpS;
                        break;
                }

                centers[i] = new Vector3(px, py, pz);

                var q = new Quaternion(qx, qy, qz, qw);
                q.Normalize();
                if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
                rotations[i] = new Vector4(q.x, q.y, q.z, q.w);

                scales[i] = new Vector3(sx, sy, sz);

                float r = 0.5f + vals[idxCr] * SH_C0;
                float g = 0.5f + vals[idxCg] * SH_C0;
                float b = 0.5f + vals[idxCb] * SH_C0;
                float a = Sigmoid(vals[idxA]);
                colors[i] = new Vector4(r, g, b, a);

                if (shCoeffsPerSplat > 0)
                {
                    // Layout matches GSplatResource.updateSHData():
                    // f_rest_[0..N-1] -> R coeffs, f_rest_[N..2N-1] -> G, f_rest_[2N..3N-1] -> B
                    int baseIdx = i * shCoeffsPerSplat;
                    for (int j = 0; j < shCoeffsPerSplat; j++)
                    {
                        float sr = vals[shIdx[j]];
                        float sg = vals[shIdx[j + shCoeffsPerSplat]];
                        float sb = vals[shIdx[j + shCoeffsPerSplat * 2]];
                        shCoeffs[baseIdx + j] = new Vector3(sr, sg, sb);
                    }
                }
            }
        }

        private static float Sigmoid(float v)
        {
            if (v > 0) return 1f / (1f + Mathf.Exp(-v));
            float t = Mathf.Exp(v);
            return t / (1f + t);
        }

        private static (PlyFormat format, VertexLayout vertex) ParseHeader(string headerText)
        {
            using var sr = new StringReader(headerText);

            var line = sr.ReadLine();
            if (line == null || !line.StartsWith("ply", StringComparison.Ordinal))
                throw new InvalidDataException("Not a PLY file.");

            var format = PlyFormat.Ascii;
            var vertex = new VertexLayout();

            bool inVertex = false;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.StartsWith("format ", StringComparison.Ordinal))
                {
                    if (line.Contains("ascii")) format = PlyFormat.Ascii;
                    else if (line.Contains("binary_little_endian")) format = PlyFormat.BinaryLittleEndian;
                    else throw new NotSupportedException($"PLY format not supported: {line}");
                }
                else if (line.StartsWith("element ", StringComparison.Ordinal))
                {
                    inVertex = false;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && parts[1] == "vertex")
                    {
                        vertex.Count = int.Parse(parts[2], CultureInfo.InvariantCulture);
                        inVertex = true;
                    }
                }
                else if (line.StartsWith("property ", StringComparison.Ordinal) && inVertex)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;

                    // property list <countType> <itemType> <name>
                    if (parts.Length >= 5 && parts[1] == "list")
                    {
                        // Not supported; gaussian splat PLYs should not use list properties on vertex.
                        continue;
                    }

                    // scalar property <type> <name>
                    var type = parts[1];
                    var name = parts[2];
                    vertex.Props.Add(new PropertySpec(name, type));
                }
                else if (line.StartsWith("end_header", StringComparison.Ordinal))
                {
                    if (vertex.Count <= 0) throw new InvalidDataException("PLY has no vertex element/count.");
                    if (vertex.Props.Count == 0) throw new InvalidDataException("PLY vertex has no properties.");
                    return (format, vertex);
                }
            }

            throw new InvalidDataException("PLY header missing end_header.");
        }

        private static int FindHeaderEndOffset(byte[] bytes)
        {
            // Find "end_header" then consume through the next '\n' (supports \r\n too).
            var needle = Encoding.ASCII.GetBytes("end_header");
            int idx = IndexOf(bytes, needle);
            if (idx < 0)
                throw new InvalidDataException("PLY header missing end_header.");

            int nl = Array.IndexOf(bytes, (byte)'\n', idx);
            if (nl < 0)
                throw new InvalidDataException("PLY header end_header line missing newline.");

            return nl + 1;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0) return 0;
            if (needle.Length > haystack.Length) return -1;

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static void ReadAsciiVertices(StreamReader reader, VertexLayout layout, int count, Action<float[], int> onVertex)
        {
            var vals = new float[layout.Props.Count];
            for (int i = 0; i < count; i++)
            {
                var line = reader.ReadLine();
                if (line == null) throw new EndOfStreamException("Unexpected EOF reading PLY ascii vertices.");
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < layout.Props.Count)
                    throw new InvalidDataException($"PLY ascii vertex line {i} has {parts.Length} columns, expected {layout.Props.Count}.");

                for (int p = 0; p < layout.Props.Count; p++)
                    vals[p] = float.Parse(parts[p], CultureInfo.InvariantCulture);

                onVertex(vals, i);
            }
        }

        private static void ReadBinaryLittleEndianVertices(BinaryReader br, VertexLayout layout, int count, Action<float[], int> onVertex)
        {
            var vals = new float[layout.Props.Count];
            for (int i = 0; i < count; i++)
            {
                for (int p = 0; p < layout.Props.Count; p++)
                {
                    vals[p] = ReadScalarAsFloat(br, layout.Props[p].Type);
                }
                onVertex(vals, i);
            }
        }

        private static float ReadScalarAsFloat(BinaryReader br, string plyType)
        {
            // Common gaussian splat PLYs use float for everything.
            return plyType switch
            {
                "float" or "float32" => br.ReadSingle(),
                "double" or "float64" => (float)br.ReadDouble(),
                "uchar" or "uint8" => br.ReadByte(),
                "char" or "int8" => (sbyte)br.ReadByte(),
                "ushort" or "uint16" => br.ReadUInt16(),
                "short" or "int16" => br.ReadInt16(),
                "uint" or "uint32" => br.ReadUInt32(),
                "int" or "int32" => br.ReadInt32(),
                _ => throw new NotSupportedException($"Unsupported PLY scalar type: {plyType}")
            };
        }
    }
}


