Shader "GaussianSplatting/Gaussian Splat GLES"
{
    // OpenGL ES 3.1 STRICT compatible version - uses exactly 4 SSBOs
    // OPTIMIZED: Uses PRECOMPUTED 3D covariance (computed on CPU at load time)
    // This eliminates per-vertex quaternion-to-matrix and matrix multiplication!
    //
    // Data packing with precomputed covariance:
    //   _SplatOrder: uint (sort indices)
    //   _SplatPosCovA: float4 (pos.xyz, cov3d.xx)
    //   _SplatCovB: float4 (cov3d.xy, cov3d.xz, cov3d.yy, cov3d.yz)
    //   _SplatCovCColor: float4 (cov3d.zz, unused, packHalf2x16(color.rg), packHalf2x16(color.ba))
    // SH coefficients are NOT supported in GLES mode
    
    Properties
    {
        _AlphaClip ("Alpha Clip", Range(0,1)) = 0.3
        _MinAlpha ("Min Alpha", Range(0,0.02)) = 0.0039215686
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Name "GaussianSplatGLES"
            Tags { "LightMode"="SRPDefaultUnlit" }

            Cull Off ZWrite Off ZTest LEqual
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma only_renderers gles3
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 gaussianUV : TEXCOORD0; float4 gaussianColor : TEXCOORD1; };

            // Exactly 4 SSBOs for GLES 3.1 strict compatibility
            // OPTIMIZED: Precomputed 3D covariance eliminates per-vertex matrix computation
            StructuredBuffer<uint> _SplatOrder;                  // SSBO 0: sort indices
            StructuredBuffer<float4> _SplatPosCovA;              // SSBO 1: pos.xyz, cov3d.xx
            StructuredBuffer<float4> _SplatCovB;                 // SSBO 2: cov3d.xy, cov3d.xz, cov3d.yy, cov3d.yz
            StructuredBuffer<float4> _SplatCovCColor;            // SSBO 3: cov3d.zz, unused, colorRG_packed, colorBA_packed
            
            int _SHBands;
            int _SHCoeffsPerSplat;
            uint _NumSplats;
            float4 _ViewportSize;
            float _IsOrtho;
            float _MinAlpha;
            float4x4 _SplatObjectToWorld;
            float4x4 _SplatWorldToObject;
            float _CamProjM00;
            float _CamProjM11;

            // Unpack two half-precision floats from a uint (stored as float bits)
            float2 UnpackHalf2x16(float packed)
            {
                uint bits = asuint(packed);
                uint hx = bits & 0xFFFF;
                uint hy = bits >> 16;
                return float2(f16tof32(hx), f16tof32(hy));
            }

            // NOTE: QuatToMat3 and ComputeCovariance removed - 3D covariance is now PRECOMPUTED on CPU!
            // This saves significant per-vertex computation (quaternion normalization, matrix multiply, etc.)

            bool InitCornerCov(float2 cornerUV, float3 viewPos, float4 centerCS, float proj00, float proj11, float3 covA, float3 covB, out float2 offsetCS, out float2 outUV)
            {
                float3x3 Vrk = float3x3(covA.x, covA.y, covA.z, covA.y, covB.x, covB.y, covA.z, covB.y, covB.z);
                float4x4 modelView = mul(UNITY_MATRIX_V, _SplatObjectToWorld);
                float3x3 W = (float3x3)modelView;
                
                float rawProj00 = _CamProjM00 != 0 ? _CamProjM00 : proj00;
                float rawProj11 = _CamProjM11 != 0 ? _CamProjM11 : proj11;
                float focalX = _ViewportSize.x * abs(rawProj00) * 0.5;
                float focalY = _ViewportSize.y * abs(rawProj11) * 0.5;
                
                float z = -viewPos.z;
                if (z <= 0.001) return false;
                float invZ = 1.0 / z;
                float invZ2 = invZ * invZ;

                float3x3 J = float3x3(
                    focalX*invZ, 0, focalX*viewPos.x*invZ2,
                    0, focalY*invZ, focalY*viewPos.y*invZ2,
                    0, 0, 0
                );

                float3x3 T = mul(J, W);
                float3x3 cov2d = mul(T, mul(Vrk, transpose(T)));

                cov2d[0][0] += 0.3;
                cov2d[1][1] += 0.3;

                float det = cov2d[0][0] * cov2d[1][1] - cov2d[0][1] * cov2d[0][1];
                if (det <= 0) return false;
                float mid = 0.5 * (cov2d[0][0] + cov2d[1][1]);
                float lambda1 = mid + sqrt(max(0.1, mid * mid - det));
                float lambda2 = max(0.1, mid - sqrt(max(0.1, mid * mid - det)));
                float radius = ceil(3.0 * sqrt(lambda1));
                
                float2 diag = normalize(float2(cov2d[0][1], lambda1 - cov2d[0][0]));
                if (abs(cov2d[0][1]) < 1e-4) diag = float2(1, 0);
                
                float2 v1 = sqrt(lambda1) * diag;
                float2 v2 = sqrt(lambda2) * float2(diag.y, -diag.x);
                
                float2 offset2D = cornerUV.x * v1 * 3.0 + cornerUV.y * v2 * 3.0;
                if (proj11 < 0) offset2D.y = -offset2D.y;
                offsetCS = offset2D * centerCS.w * _ViewportSize.zw * 2.0;
                outUV = cornerUV * 3.0;
                return true;
            }

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                uint idx = IN.vertexID / 6u;
                uint vidx = IN.vertexID % 6u;
                if (idx >= _NumSplats) { OUT.positionCS = 0; return OUT; }
                float2 uv = float2(vidx==1||vidx==2||vidx==4?1:-1, vidx==2||vidx==4||vidx==5?1:-1);
                uint id = _SplatOrder[idx];
                
                // Unpack data from packed buffers with PRECOMPUTED 3D covariance
                float4 posCovA = _SplatPosCovA[id];
                float4 covB = _SplatCovB[id];
                float4 covCColor = _SplatCovCColor[id];
                
                // Extract position
                float3 p = posCovA.xyz;
                
                // Extract PRECOMPUTED 3D covariance (6 unique values of symmetric 3x3 matrix)
                // covA = (xx, xy, xz), covB_vec = (yy, yz, zz)
                float3 ca = float3(posCovA.w, covB.x, covB.y);  // xx, xy, xz
                float3 cb = float3(covB.z, covB.w, covCColor.x); // yy, yz, zz
                
                // Extract color from half-precision packed values
                float2 colorRG = UnpackHalf2x16(covCColor.z);
                float2 colorBA = UnpackHalf2x16(covCColor.w);
                float4 c = float4(colorRG.x, colorRG.y, colorBA.x, colorBA.y);
                
                float3 wp = mul(_SplatObjectToWorld, float4(p, 1.0)).xyz;
                float3 vp = mul(UNITY_MATRIX_V, float4(wp, 1.0)).xyz;
                float4 cp = mul(UNITY_MATRIX_P, float4(vp, 1.0));
                
                // Use precomputed covariance directly - no quaternion-to-matrix conversion needed!
                float2 off; float2 guv;
                if (!InitCornerCov(uv, vp, cp, UNITY_MATRIX_P[0][0], UNITY_MATRIX_P[1][1], ca, cb, off, guv)) { OUT.positionCS=0; return OUT; }
                OUT.positionCS = cp + float4(off, 0, 0);
                OUT.gaussianUV = guv;
                OUT.gaussianColor = float4(max(c.rgb, 0), c.a);
                return OUT;
            }

            float4 Frag(Varyings IN) : SV_Target
            {
                float d2 = dot(IN.gaussianUV, IN.gaussianUV);
                if (d2 > 9.0) discard;
                float alpha = exp(-0.5 * d2) * IN.gaussianColor.a;
                if (alpha < _MinAlpha) discard;
                return float4(IN.gaussianColor.rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
