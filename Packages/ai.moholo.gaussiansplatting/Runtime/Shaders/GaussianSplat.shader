Shader "GaussianSplatting/Gaussian Splat"
{
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
            Name "GaussianSplat"
            Tags { "LightMode"="SRPDefaultUnlit" }

            Cull Off ZWrite Off ZTest LEqual
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma exclude_renderers d3d11_9x
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings 
            { 
                float4 positionCS : SV_POSITION; 
                float2 gaussianUV : TEXCOORD0; 
                float4 gaussianColor : TEXCOORD1; 
            };

            StructuredBuffer<uint> _SplatOrder;
            StructuredBuffer<float3> _Centers;
            StructuredBuffer<float4> _Rotations;
            StructuredBuffer<float3> _Scales;
            StructuredBuffer<float4> _Colors;
            StructuredBuffer<float3> _SHCoeffs;
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

            Varyings MakeInvalidVaryings()
            {
                Varyings o;
                o.positionCS = float4(0, 0, 0, 0);
                o.gaussianUV = float2(0, 0);
                o.gaussianColor = float4(0, 0, 0, 0);
                return o;
            }

            float3x3 QuatToMat3(float4 q)
            {
                float w = q.x, x = q.y, y = q.z, z = q.w;
                float3x3 m;
                m[0] = float3(1.0-2.0*(y*y+z*z), 2.0*(x*y-w*z), 2.0*(x*z+w*y));
                m[1] = float3(2.0*(x*y+w*z), 1.0-2.0*(x*x+z*z), 2.0*(y*z-w*x));
                m[2] = float3(2.0*(x*z-w*y), 2.0*(y*z+w*x), 1.0-2.0*(x*x+y*y));
                return m;
            }

            void ComputeCovariance(float4 q, float3 s, out float3 covA, out float3 covB)
            {
                float3x3 R = QuatToMat3(normalize(q));
                float3x3 S;
                S[0] = float3(s.x, 0, 0);
                S[1] = float3(0, s.y, 0);
                S[2] = float3(0, 0, s.z);
                float3x3 M = mul(R, S);
                float3x3 V = mul(M, transpose(M));
                covA = float3(V[0][0], V[0][1], V[0][2]);
                covB = float3(V[1][1], V[1][2], V[2][2]);
            }

            bool InitCornerCov(float2 cornerUV, float3 viewPos, float4 centerCS, float proj00, float proj11, float3 covA, float3 covB, out float2 offsetCS, out float2 outUV)
            {
                offsetCS = float2(0, 0);
                outUV = float2(0, 0);

                float3x3 Vrk;
                Vrk[0] = float3(covA.x, covA.y, covA.z);
                Vrk[1] = float3(covA.y, covB.x, covB.y);
                Vrk[2] = float3(covA.z, covB.y, covB.z);

                float4x4 modelView = mul(UNITY_MATRIX_V, _SplatObjectToWorld);
                float3x3 W;
                W[0] = modelView[0].xyz;
                W[1] = modelView[1].xyz;
                W[2] = modelView[2].xyz;
                
                // Use camera's raw projection for focal length (not GPU-adjusted)
                // If _CamProjM00/_CamProjM11 are set, use them; otherwise fall back to passed values
                float rawProj00 = _CamProjM00 != 0 ? _CamProjM00 : proj00;
                float rawProj11 = _CamProjM11 != 0 ? _CamProjM11 : proj11;
                float focalX = _ViewportSize.x * abs(rawProj00) * 0.5;
                float focalY = _ViewportSize.y * abs(rawProj11) * 0.5;
                
                // Unity view space has negative Z for objects in front - convert to positive depth
                float z = -viewPos.z;
                if (z <= 0.001) return false;
                float invZ = 1.0 / z;
                float invZ2 = invZ * invZ;

                float3x3 J;
                J[0] = float3(focalX*invZ, 0, focalX*viewPos.x*invZ2);
                J[1] = float3(0, focalY*invZ, focalY*viewPos.y*invZ2);
                J[2] = float3(0, 0, 0);

                // cov2d = J * W * Vrk * W^T * J^T = T * Vrk * T^T where T = J * W
                float3x3 T = mul(J, W);
                float3x3 cov2d = mul(T, mul(Vrk, transpose(T)));

                cov2d[0][0] += 0.3;
                cov2d[1][1] += 0.3;

                float det = cov2d[0][0] * cov2d[1][1] - cov2d[0][1] * cov2d[0][1];
                if (det <= 0) return false;
                float mid = 0.5 * (cov2d[0][0] + cov2d[1][1]);
                float lambda1 = mid + sqrt(max(0.1, mid * mid - det));
                float lambda2 = max(0.1, mid - sqrt(max(0.1, mid * mid - det)));
                
                float2 diag = normalize(float2(cov2d[0][1], lambda1 - cov2d[0][0]));
                if (abs(cov2d[0][1]) < 1e-4) diag = float2(1, 0);
                
                float2 v1 = sqrt(lambda1) * diag;
                float2 v2 = sqrt(lambda2) * float2(diag.y, -diag.x);
                
                float2 offset2D = cornerUV.x * v1 * 3.0 + cornerUV.y * v2 * 3.0;
                // Flip Y if projection matrix has Y flipped (common in Unity render targets)
                if (proj11 < 0) offset2D.y = -offset2D.y;
                offsetCS = offset2D * centerCS.w * _ViewportSize.zw * 2.0;
                outUV = cornerUV * 3.0;
                return true;
            }

            static const float SH_C1 = 0.4886025;
            static const float SH_C2_0 = 1.092548;
            static const float SH_C2_1 = -1.092548;
            static const float SH_C2_2 = 0.31539;
            static const float SH_C2_3 = -1.092548;
            static const float SH_C2_4 = 0.54627;
            static const float SH_C3_0 = -0.59004;
            static const float SH_C3_1 = 2.8906;
            static const float SH_C3_2 = -0.4570;
            static const float SH_C3_3 = 0.3731;
            static const float SH_C3_4 = -0.4570;
            static const float SH_C3_5 = 1.4453;
            static const float SH_C3_6 = -0.5900;

            float3 EvalSH(uint id, float3 dir)
            {
                if (_SHBands <= 0) return float3(0, 0, 0);
                uint b = id * (uint)_SHCoeffsPerSplat;
                float x=dir.x, y=dir.y, z=dir.z;
                float3 res = SH_C1 * (-_SHCoeffs[b+0]*y + _SHCoeffs[b+1]*z - _SHCoeffs[b+2]*x);
                if (_SHBands > 1) {
                    float xx=x*x, yy=y*y, zz=z*z, xy=x*y, yz=y*z, xz=x*z;
                    res += _SHCoeffs[b+3]*(SH_C2_0*xy) + _SHCoeffs[b+4]*(SH_C2_1*yz) + _SHCoeffs[b+5]*(SH_C2_2*(2*zz-xx-yy)) + _SHCoeffs[b+6]*(SH_C2_3*xz) + _SHCoeffs[b+7]*(SH_C2_4*(xx-yy));
                    if (_SHBands > 2) {
                        res += _SHCoeffs[b+8]*(SH_C3_0*y*(3*xx-yy)) + _SHCoeffs[b+9]*(SH_C3_1*xy*z) + _SHCoeffs[b+10]*(SH_C3_2*y*(4*zz-xx-yy)) + _SHCoeffs[b+11]*(SH_C3_3*z*(2*zz-3*xx-3*yy)) + _SHCoeffs[b+12]*(SH_C3_4*x*(4*zz-xx-yy)) + _SHCoeffs[b+13]*(SH_C3_5*z*(xx-yy)) + _SHCoeffs[b+14]*(SH_C3_6*x*(xx-3*yy));
                    }
                }
                return res;
            }

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = MakeInvalidVaryings();
                
                uint idx = IN.vertexID / 6u;
                uint vidx = IN.vertexID % 6u;
                if (idx >= _NumSplats) return OUT;
                
                float2 uv = float2(vidx==1||vidx==2||vidx==4?1:-1, vidx==2||vidx==4||vidx==5?1:-1);
                uint id = _SplatOrder[idx];
                float3 p = _Centers[id];
                float4 r = _Rotations[id];
                float3 s = _Scales[id];
                float4 c = _Colors[id];
                float3 wp = mul(_SplatObjectToWorld, float4(p, 1.0)).xyz;
                float3 vp = mul(UNITY_MATRIX_V, float4(wp, 1.0)).xyz;
                float4 cp = mul(UNITY_MATRIX_P, float4(vp, 1.0));
                
                float3 ca, cb;
                float4 q = float4(r.w, r.x, r.y, r.z);
                ComputeCovariance(q, s, ca, cb);
                
                float2 off, guv;
                float p00 = UNITY_MATRIX_P[0][0];
                float p11 = UNITY_MATRIX_P[1][1];
                if (!InitCornerCov(uv, vp, cp, p00, p11, ca, cb, off, guv)) return OUT;
                
                OUT.positionCS = cp + float4(off, 0, 0);
                OUT.gaussianUV = guv;
                if (_SHBands > 0) {
                    float3 dir = normalize(wp - _WorldSpaceCameraPos);
                    float3x3 wtol;
                    wtol[0] = _SplatWorldToObject[0].xyz;
                    wtol[1] = _SplatWorldToObject[1].xyz;
                    wtol[2] = _SplatWorldToObject[2].xyz;
                    c.rgb += EvalSH(id, mul(wtol, dir));
                }
                OUT.gaussianColor = float4(max(c.rgb, float3(0, 0, 0)), c.a);
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
    
    Fallback Off
}
