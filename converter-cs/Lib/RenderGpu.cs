// RenderGpu.cs - the Hq outfit picture drawn on the graphics card (Direct3D 11 through SharpDX). Same scene as the CPU
// renderer (Render.cs: posed triangles in view space, materials, framing, lights) and the same shading, written in HLSL:
//   1. shadow pass: the key light's depth (orthographic), solid parts and hair in two maps (hair lets light through);
//   2. main pass: supersampled picture, colors already in sRGB 0..255 and alpha 255 where the figure is (like the CPU),
//      read back and reduced on the CPU by Render.cs.
// Device: the graphics card (feature level 11), else WARP (Windows' software Direct3D, same shaders on the CPU).
// Shaders are compiled at run time by Windows' d3dcompiler_47.dll.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace CustomOutfits
{
    public static partial class OutfitRender
    {
        /// <summary>What the GPU needs from Render(): framing, lights and the key light's shadow basis.</summary>
        private sealed class GpuFrame
        {
            public int IW, IH, SM;
            public bool Persp, Dark;
            public double Fpx, Cx, Cy, Sc, ShX, ShY, LKey, MnY, MxY, Lx0, Ly0, Lsc;
            public double[] Key, Fill, KeyC, RimC, LX, LY;
        }

        private const string Hlsl = @"
cbuffer Frame : register(b0)
{
    float4 screen;  // IW, IH, perspective, fpx
    float4 ortho;   // cx, cy, sc, dark
    float4 shiftZ;  // shX, shY, zA, zB  (depth: ortho (zA - z) * zB; perspective (-z - zA) * zB)
    float4 key;     // key direction, L_KEY
    float4 fill;    // fill direction, SM
    float4 keyC;    // key color, mnY
    float4 rimC;    // rim color, mxY
    float4 lx;      // shadow basis X, lx0
    float4 ly;      // shadow basis Y, ly0
    float4 smP;     // lsc, lzMax, lzScale, soft shadow radius (map uv)
    float4 q;       // quality pass, MSAA samples, picture pixels per AO pixel, output scale
    float4 aoP;     // AO radius (view units), AO width, AO height, show the AO alone (tests)
};
cbuffer Material : register(b1)
{
    float4 flags;   // has color, has ATOS, has normal map, color alpha is a cut-out
    float4 flags2;  // no alpha (mods), skin, cull back faces, hair shader
    float4 tint;    // rgb, has tint
    float4 spec1;   // rgb, sharpness
    float4 spec2;   // rgb, sharpness
    float4 hairP;   // intensity, shift, eyeball, hair coverage scale
    float4 extra;   // face material (its painted scalp: darker, like hair roots), scalp darkening, -, -
};
Texture2D texColor : register(t0);
Texture2D texAtos : register(t1);
Texture2D texNrm : register(t2);
Texture2D<float> smSolid : register(t3);
Texture2D<float> smHair : register(t4);
Texture2D<float> texAO : register(t5);   // quality: contact shadow (1 = open), AO resolution
Texture2D<float4> gPos : register(t6);   // quality: view-space position (w = 1 on the figure), AO resolution
Texture2D<float4> gNrm : register(t7);   // quality: view-space normal facing the camera
SamplerState samp : register(s0);
SamplerComparisonState cmpLit : register(s1);  // reference >= depth in the map: the light reaches it
SamplerComparisonState cmpHair : register(s2); // reference < depth in the hair map: hair in front of the light
SamplerState sampClamp : register(s3);

// per-pixel noise (interleaved gradient noise): rotates sample patterns; the supersampling averages it out
float Ign(float2 p) { return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))); }

// view space -> picture pixel (same as VSMain)
float2 ProjPx(float3 p)
{
    if (screen.z > 0.5) { float iz = 1.0 / max(1e-4, -p.z); return float2(screen.x / 2 + p.x * screen.w * iz, screen.y / 2 - p.y * screen.w * iz); }
    return float2(screen.x / 2 + shiftZ.x + (p.x - ortho.x) * ortho.z, screen.y / 2 + shiftZ.y - (p.y - ortho.y) * ortho.z);
}

struct VIn { float3 pos : POSITION; float3 nrm : NORMAL; float3 tan : TANGENT; float2 uv : TEXCOORD0; float sg : TEXCOORD1; };
struct VOut { float4 pos : SV_Position; float3 vp : TEXCOORD2; float3 nrm : NORMAL; float3 tan : TANGENT; float2 uv : TEXCOORD0; float sg : TEXCOORD1; };

VOut VSMain(VIn v)
{
    VOut o;
    float IW = screen.x, IH = screen.y, px, py, w, d;
    if (screen.z > 0.5)
    {
        float iz = 1.0 / max(1e-4, -v.pos.z);
        px = IW / 2 + v.pos.x * screen.w * iz; py = IH / 2 - v.pos.y * screen.w * iz;
        w = -v.pos.z; d = (-v.pos.z - shiftZ.z) * shiftZ.w;
    }
    else
    {
        px = IW / 2 + shiftZ.x + (v.pos.x - ortho.x) * ortho.z; py = IH / 2 + shiftZ.y - (v.pos.y - ortho.y) * ortho.z;
        w = 1; d = (shiftZ.z - v.pos.z) * shiftZ.w;
    }
    o.pos = float4((px / IW * 2 - 1) * w, (1 - py / IH * 2) * w, d * w, w);
    o.vp = v.pos; o.nrm = v.nrm; o.tan = v.tan; o.uv = v.uv; o.sg = v.sg;
    return o;
}

// ---- shadow pass: depth along the key light (bigger = closer to the light), cut-outs respected
struct SOut { float4 pos : SV_Position; float lz : TEXCOORD2; float2 uv : TEXCOORD0; };
SOut VSShadow(VIn v)
{
    SOut o;
    float SM = fill.w;
    float sx = (dot(lx.xyz, v.pos) - lx.w) * smP.x + 2, sy = (dot(ly.xyz, v.pos) - ly.w) * smP.x + 2;
    float lz = dot(key.xyz, v.pos);
    o.pos = float4(sx / SM * 2 - 1, 1 - sy / SM * 2, (smP.y - lz) * smP.z, 1);
    o.lz = lz; o.uv = v.uv;
    return o;
}
float PSShadow(SOut i) : SV_Target
{
    if (flags.y > 0.5 && texAtos.Sample(samp, i.uv).r * 255 < 128) discard;
    if (flags.x > 0.5 && flags.w > 0.5 && texColor.Sample(samp, i.uv).a * 255 < (q.x > 0.5 ? 40 : 128)) discard; // (quality: thin strands shadow too)
    return i.lz;
}

// ---- main pass
float ToLin(float c) { return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }
float ToSrgb(float l) { return 255 * (l <= 0.0031308 ? l * 12.92 : 1.055 * pow(l, 1 / 2.4) - 0.055); }

float Lit(float3 p)
{
    float SM = fill.w;
    float sx = (dot(lx.xyz, p) - lx.w) * smP.x + 2, sy = (dot(ly.xyz, p) - ly.w) * smP.x + 2;
    float sz = dot(key.xyz, p), bias = 0.012;
    int cx0 = (int)sx, cy0 = (int)sy, lit = 0, hair = 0, n = 0;
    [unroll] for (int dy = -3; dy <= 3; dy++)
        [unroll] for (int dx = -3; dx <= 3; dx++)
        {
            int xx = cx0 + dx * 2, yy = cy0 + dy * 2;
            n++;
            if (xx < 0 || yy < 0 || xx >= (int)SM || yy >= (int)SM) { lit++; continue; }
            if (sz + bias >= smSolid.Load(int3(xx, yy, 0))) lit++;
            if (sz + bias < smHair.Load(int3(xx, yy, 0))) hair++;
        }
    return lit / (float)n * (1 - 0.45 * hair / n);
}

// quality: 32 filtered comparisons on a disc rotated per pixel (smooth penumbra instead of 49 hard steps)
static const float2 Poisson[16] = {
    float2(-0.94201624, -0.39906216), float2(0.94558609, -0.76890725), float2(-0.09418410, -0.92938870), float2(0.34495938, 0.29387760),
    float2(-0.91588581, 0.45771432), float2(-0.81544232, -0.87912464), float2(-0.38277543, 0.27676845), float2(0.97484398, 0.75648379),
    float2(0.44323325, -0.97511554), float2(0.53742981, -0.47373420), float2(-0.26496911, -0.41893023), float2(0.79197514, 0.19090188),
    float2(-0.24188840, 0.99706507), float2(-0.81409955, 0.91437590), float2(0.19984126, 0.78641367), float2(0.14383161, -0.14100790) };
float LitSoft(float3 p, float2 pix)
{
    float SM = fill.w;
    float2 uv = float2((dot(lx.xyz, p) - lx.w) * smP.x + 2, (dot(ly.xyz, p) - ly.w) * smP.x + 2) / SM;
    float ref = dot(key.xyz, p) + 0.012;
    float a = Ign(pix) * 6.2831853, s = sin(a), c = cos(a);
    float lit = 0, hair = 0;
    [unroll] for (int k = 0; k < 16; k++)
    {
        float2 d = float2(Poisson[k].x * c - Poisson[k].y * s, Poisson[k].x * s + Poisson[k].y * c) * smP.w;
        float2 d2 = float2(-d.y, d.x) * 0.55;
        lit += smSolid.SampleCmpLevelZero(cmpLit, uv + d, ref) + smSolid.SampleCmpLevelZero(cmpLit, uv + d2, ref);
        hair += smHair.SampleCmpLevelZero(cmpHair, uv + d, ref) + smHair.SampleCmpLevelZero(cmpHair, uv + d2, ref);
    }
    return lit / 32 * (1 - 0.6 * hair / 32);
}

static float3 gN, gBv, gAlb, gLin;
static float gMetal, gRough, gNv, gA2, gSkin;
// hair painted on the scalp of a face texture: darker than skin, yellower (less blue), more green than skin
float ScalpTone(float3 c)
{
    if (c.r < 1e-3) return 0;
    float gr = c.g / c.r, br = c.b / c.r;
    return smoothstep(0.78, 0.84, gr) * (1 - smoothstep(0.70, 0.76, br)) * (1 - smoothstep(0.55, 0.70, c.r));
}
// quality: share of hair in front of this point nearby on screen (the scalp seen between strands; not dirt on a forehead)
float HairNear(float2 pix, float z)
{
    float2 ap = pix / q.z;
    float n = 0;
    [unroll] for (int k = 0; k < 12; k++)
    {
        float a = k * 0.5235988, r = 3 + (k % 3) * 3;
        float4 P = gPos.Load(int3(int2(ap + float2(cos(a), sin(a)) * r), 0));
        if (P.w > 1.5 && P.z > z) n++;
    }
    return saturate(n / 12 * 2.5);
}
float SkinTone(float3 c)
{
    if (c.r < 1e-3) return 0;
    float gr = c.g / c.r, br = c.b / c.r;
    return smoothstep(0.28, 0.42, c.r) * (1 - smoothstep(0.07, 0.14, abs(gr - 0.77))) * (1 - smoothstep(0.1, 0.2, abs(br - 0.66)));
}
static bool gHasBv;

void Light(float3 L, float3 lc, float inten, float sh, bool spec)
{
    float nl = dot(gN, L);
    float3 diff;
    diff = lerp(max(0, nl).xxx, max(0, (nl + float3(0.5, 0.3, 0.25)) / (1 + float3(0.5, 0.3, 0.25))), gSkin);
    gLin += gAlb * (1 - gMetal) * diff * inten * lc * sh;
    if (!spec || nl <= 0) return;
    float3 H = normalize(L + float3(0, 0, 1));
    if (flags2.w > 0.5 && gHasBv && spec1.w > 0)
    {
        // hair: two strand highlights (Kajiya-Kay) along the strand direction, shifted along the normal
        float3 T1 = normalize(gBv + gN * (-hairP.y * 0.5)), T2 = normalize(gBv + gN * (hairP.y * 0.5));
        float t1 = dot(T1, H), t2 = dot(T2, H);
        float p1 = pow(sqrt(max(0, 1 - t1 * t1)), spec1.w), p2 = pow(sqrt(max(0, 1 - t2 * t2)), max(1, spec2.w));
        float att = min(1, nl * 2);
        gLin += (spec1.rgb * p1 * 0.6 + spec2.rgb * gAlb * p2 * 10) * hairP.x * att * inten * lc * sh;
        return;
    }
    float nh = max(0, dot(gN, H)), vh = max(0, H.z);
    float dd = nh * nh * (gA2 - 1) + 1, Dg = gA2 / (3.14159265 * dd * dd);
    float kk = (gRough + 1) * (gRough + 1) / 8, G = nl / (nl * (1 - kk) + kk) * (gNv / (gNv * (1 - kk) + kk));
    float fw = pow(1 - vh, 5);
    float3 f0 = 0.04 * (1 - gMetal) + gAlb * gMetal, F = f0 + (1 - f0) * fw;
    gLin += Dg * G * F / (4 * nl * gNv) * nl * inten * lc * sh * 3.14159265 * (0.15 - 0.08 * gSkin);
}

float4 PSMain(VOut i, bool front : SV_IsFrontFace, out uint cov : SV_Coverage) : SV_Target
{
    if (flags2.z > 0.5 && !front) discard;
    float ao = 1, colA = 255, cutA = 1, hairA = 1;
    bool hasCut = false, quality = q.x > 0.5;
    cov = 0xffffffff;
    if (flags.y > 0.5) { float4 a = texAtos.Sample(samp, i.uv); if (quality) { cutA = a.r; hasCut = true; } else if (a.r * 255 < 128) discard; ao = a.b; }
    float3 col;
    if (flags.x > 0.5)
    {
        float4 c = texColor.Sample(samp, i.uv);
        if (flags.w > 0.5) { if (quality) { hairA = c.a; hasCut = true; } else if (c.a * 255 < 128) discard; }
        colA = c.a * 255;
        col = float3(ToLin(c.r), ToLin(c.g), ToLin(c.b));
        if (tint.w > 0.5) col *= tint.rgb;
        // face: the scalp painted under the hair (not skin coloured) reads as skin in a still picture; darken it like roots
        if (extra.x > 0.5 && quality) col *= lerp(1, extra.y, ScalpTone(c.rgb) * HairNear(i.pos.xy, i.vp.z));
    }
    else col = ToLin(150.0 / 255).xxx;
    if (hasCut)
    {
        // quality: the cut-out edge covers part of the pixel's samples (anti-aliased strands instead of a hard cut);
        // the mask is rotated per pixel and per depth so stacked hair layers cover different samples
        uint S = (uint)q.y;
        // cut-out maps (ATOS): a sharp, anti-aliased edge; hair alpha: partial coverage as in the game (thin and see-through
        // strand tips stay visible instead of vanishing below 50%), dithered between sample counts
        float e = saturate((cutA - 0.5) / max(fwidth(cutA), 1e-3) + 0.5) * saturate(hairA * hairP.w);
        float noise = Ign(i.pos.yx * 1.7 + frac(i.vp.z * 53.0) * 37);
        uint n = flags.w > 0.5 ? (uint)min(S, floor(e * S + noise)) : (uint)round(e * S);
        if (n == 0) discard;
        uint full = S >= 32 ? 0xffffffff : ((1u << S) - 1), m = n >= S ? full : ((1u << n) - 1);
        uint rot = (uint)(Ign(i.pos.xy + frac(i.vp.z * 37.0) * 64) * S) % S;
        cov = rot == 0 ? m : (((m << rot) | (m >> (S - rot))) & full);
    }

    float3 ng = normalize(i.nrm);
    bool flip = ng.z < 0;
    if (flip) ng = -ng;
    gRough = 0.75; gN = ng; gHasBv = false; gBv = 0;
    if (flags.z > 0.5)
    {
        float4 h = texNrm.Sample(samp, i.uv) * 255;
        float mx = h.r / 127.5 - 1, my = h.g / 127.5 - 1, mz = sqrt(max(0, 1 - mx * mx - my * my));
        gRough = max(0.08, h.a / 255);
        float3 t = i.tan;
        if (flip) t = -t;
        float3 Tn = normalize(t - ng * dot(t, ng));
        float sg = i.sg >= 0 ? 1 : -1;
        float3 b = cross(ng, Tn) * sg;
        gBv = normalize(b); gHasBv = true;
        gN = normalize(Tn * mx + b * my + ng * mz);
    }
    float metal = (flags.w > 0.5 || flags.x < 0.5 || flags2.x > 0.5) ? 0 : colA / 255;
    gMetal = metal; gAlb = col; gLin = 0;
    // skin shading: skin materials by name, elsewhere by the colour (as on the CPU)
    gSkin = flags2.y > 0.5 ? 1 : (flags.x < 0.5 || flags.w > 0.5 || flags2.w > 0.5 || hairP.z > 0.5) ? 0 : SkinTone(pow(max(col, 0), 1 / 2.2));
    gA2 = pow(gRough * gRough, 2);
    gNv = max(1e-3, gN.z);
    float shadow = quality ? LitSoft(i.vp, i.pos.xy) : Lit(i.vp);
    if (hairP.z > 0.5) shadow = 0.55 + 0.45 * shadow; // eyeball: softer lid shadow
    float ssao = quality ? texAO.SampleLevel(sampClamp, i.pos.xy / screen.xy, 0) : 1;
    if (hairP.z > 0.5) ssao = 1; // eyeball: it sits deep in the socket by design (contact shadow would darken it)
    float3 white = 1;
    float3 KEY = key.xyz, FILL = fill.xyz;
    if (ortho.w > 0.5)
    {
        Light(KEY, keyC.rgb, key.w * 0.9, 0.12 + 0.88 * shadow, true);
        Light(normalize(float3(-FILL.x, FILL.y, FILL.z)), white, 0.15, 1, false);
        Light(normalize(float3(-0.75, 0.35, -0.55)), rimC.rgb, 0.5, 1, true);
    }
    else
    {
        float hgt = saturate((i.vp.y - keyC.w) / max(1e-6, rimC.w - keyC.w));
        float spot = 0.15 + 0.85 * pow(hgt, 2.5);
        Light(KEY, keyC.rgb, key.w * 0.9 * spot, 0.2 + 0.8 * shadow, true);
        Light(FILL, white, 0.06, 1, false);
        Light(normalize(float3(0.85, 0.2, -0.5)), white, 1.3, 1, true);
        Light(normalize(float3(-0.85, 0.2, -0.5)), white, 1.3, 1, true);
        float edge = pow(1 - abs(ng.z), 6) * 0.5 * ao;
        gLin += edge * (0.05 + col);
    }
    float up = gN.y * 0.5 + 0.5;
    float3 amb = float3(0.04 + 0.05 * up, 0.045 + 0.055 * up, 0.055 + 0.07 * up);
    float fe = pow(1 - gNv, 5) * (1 - gRough);
    float3 f0 = 0.04 * (1 - metal) + col * metal;
    gLin += col * metal * (0.35 + 0.45 * up) * (1 - gRough * 0.5) * ao;
    gLin += (col * (1 - metal) * amb * 1.6 + (f0 + (1 - f0) * fe) * amb * 0.8 * (1 - gRough * 0.7)) * ao;
    gLin *= 0.4 + 0.6 * ao;
    if (aoP.w > 0.5) return float4(ToSrgb(ssao).xxx, 255) * q.w;
    if (quality) gLin *= 0.3 + 0.7 * ssao; // contact shadow: creases, where parts touch, under the hair
    float lum = dot(gLin, float3(0.2126, 0.7152, 0.0722));
    float3 l = ortho.w > 0.5 ? gLin : lum + (gLin - lum) * 0.92;
    l = l / (1 + l * 0.15);
    return float4(saturate(float3(ToSrgb(l.r), ToSrgb(l.g), ToSrgb(l.b)) / 255) * 255, 255) * q.w;
}

// ---- quality: position/normal of the visible surface (AO resolution), contact shadow, depth-aware blur
struct GOut { float4 pos : SV_Target0; float4 nrm : SV_Target1; };
GOut PSGeo(VOut i, bool front : SV_IsFrontFace)
{
    if (flags2.z > 0.5 && !front) discard;
    if (flags.y > 0.5 && texAtos.Sample(samp, i.uv).r * 255 < 128) discard;
    if (flags.x > 0.5 && flags.w > 0.5 && texColor.Sample(samp, i.uv).a * 255 < 40) discard; // (thin strands count for the contact shadow)
    float3 n = normalize(i.nrm);
    if (n.z < 0) n = -n;
    GOut o; o.pos = float4(i.vp, flags.w > 0.5 ? 2 : 1); o.nrm = float4(n, 0); // (w = 2: hair)
    return o;
}
struct FOut { float4 pos : SV_Position; };
FOut VSFull(uint id : SV_VertexID)
{
    FOut o; float2 t = float2((id << 1) & 2, id & 2);
    o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float PSAO(FOut i) : SV_Target
{
    int2 px = int2(i.pos.xy);
    float4 P = gPos.Load(int3(px, 0));
    if (P.w < 0.5) return 1;
    float3 N = normalize(gNrm.Load(int3(px, 0)).xyz);
    float R = aoP.x, a = Ign(i.pos.xy) * 6.2831853;
    float3 r0 = float3(cos(a), sin(a), 0.3);
    float3 T = normalize(r0 - N * dot(r0, N)), B = cross(N, T);
    float occ = 0;
    [unroll] for (int k = 0; k < 16; k++)
    {
        float t = (k + 0.5) / 16, phi = k * 2.3999632, z = sqrt(1 - t * 0.85), rr = sqrt(1 - z * z);
        float3 dir = float3(rr * cos(phi), rr * sin(phi), z) * lerp(0.15, 1, t * t);
        float3 s = P.xyz + (T * dir.x + B * dir.y + N * dir.z) * R;
        int2 sp = int2(ProjPx(s) / q.z);
        if (sp.x < 0 || sp.y < 0 || sp.x >= (int)aoP.y || sp.y >= (int)aoP.z) continue;
        float4 S = gPos.Load(int3(sp, 0));
        if (S.w < 0.5) continue;
        if (S.z > s.z + R * 0.04) occ += smoothstep(0, 1, R / max(1e-5, abs(P.z - S.z)));
    }
    float o = 1 - occ / 16;
    return o * o;
}
float PSBlur(FOut i) : SV_Target
{
    int2 px = int2(i.pos.xy);
    float4 P = gPos.Load(int3(px, 0));
    if (P.w < 0.5) return 1;
    float s = 0, w = 0;
    [unroll] for (int dy = -3; dy <= 3; dy++)
        [unroll] for (int dx = -3; dx <= 3; dx++)
        {
            int2 qq = px + int2(dx, dy);
            float4 Q = gPos.Load(int3(qq, 0));
            if (Q.w < 0.5) continue;
            float ww = exp(-abs(Q.z - P.z) / (aoP.x * 0.5));
            s += texAO.Load(int3(qq, 0)) * ww; w += ww;
        }
    return s / max(w, 1e-4);
}
";

        private static readonly object GpuLock = new object();

        /// <summary>Draws the triangles on the GPU: RGBA floats (IW x IH, sRGB 0..255, alpha 0..255 = coverage). Quality
        /// first (MSAA with soft cut-out edges, full mip chains with anisotropic filtering, a 4096 shadow map with soft
        /// filtered penumbras, contact shadows); if that fails (memory, old card), the basic pass (same as the CPU).
        /// CO_GPU_BASIC=1: basic only (comparisons).</summary>
        private static float[] DrawGpu(List<Tri> tris, GpuFrame f, out string device)
        {
            if (Environment.GetEnvironmentVariable("CO_GPU_BASIC") != "1")
            {
                try { var r = DrawGpuOnce(tris, f, true, out device); device += ", quality"; return r; }
                catch (Exception e) { GpuQualityError = e.Message; }
            }
            var b = DrawGpuOnce(tris, f, false, out device);
            device += ", basic";
            return b;
        }

        private static double AoRadius => double.TryParse(Environment.GetEnvironmentVariable("CO_AO_R"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.05;

        /// <summary>Why the quality pass failed last time (diagnostics), or null.</summary>
        public static string GpuQualityError;

        /// <summary>The .tex mips from the largest one not above 4096 to the smallest, as pointers into the pinned file;
        /// null if the top size is not a multiple of 4 (compressed) or a mip lies outside the file.</summary>
        private static DataBox[] MipChain(Tex t, Format fmt, IntPtr file, out int w0, out int h0)
        {
            int n = TexDecode.MipCount(t.File), top = 0;
            TexDecode.MipInfo(t.File, 0, out w0, out h0, out _, out _, out _, out _);
            while (top + 1 < n && Math.Max(w0 >> top, h0 >> top) > 4096) top++;
            TexDecode.MipInfo(t.File, top, out w0, out h0, out _, out _, out _, out _);
            bool bc = fmt != Format.R8G8B8A8_UNorm;
            if (bc && (w0 % 4 != 0 || h0 % 4 != 0)) return null;
            int bpb = fmt == Format.BC1_UNorm ? 8 : 16;
            var boxes = new List<DataBox>();
            for (int l = top; l < n; l++)
            {
                TexDecode.MipInfo(t.File, l, out int w, out int h, out _, out long off, out _, out _);
                int pitch = bc ? Math.Max(1, (w + 3) / 4) * bpb : w * 4, rows = bc ? Math.Max(1, (h + 3) / 4) : h;
                if (off <= 0 || off + (long)pitch * rows > t.File.Length) break;
                boxes.Add(new DataBox(file + (int)off, pitch, pitch * rows));
                if (w == 1 && h == 1) break;
            }
            return boxes.Count == 0 ? null : boxes.ToArray();
        }

        private static float[] DrawGpuOnce(List<Tri> tris, GpuFrame f, bool quality, out string device)
        {
            lock (GpuLock) // one picture at a time (the converter may draw from several threads)
            {
                D3D11Device dev;
                try
                {
                    if (Environment.GetEnvironmentVariable("CO_FORCE_WARP") == "1") throw new NotSupportedException(); // (test: as without a card)
                    dev = new D3D11Device(DriverType.Hardware, DeviceCreationFlags.None, FeatureLevel.Level_11_0); device = "hardware";
                }
                catch { dev = new D3D11Device(DriverType.Warp, DeviceCreationFlags.None, FeatureLevel.Level_11_0); device = "warp"; }
                var trash = new List<IDisposable> { dev };
                T Keep<T>(T x) where T : IDisposable { trash.Add(x); return x; }
                try
                {
                    if (device == "hardware") using (var dxgi = dev.QueryInterface<SharpDX.DXGI.Device>()) using (var ad = dxgi.Adapter) device = "hardware: " + ad.Description.Description.Trim();
                    var ctx = dev.ImmediateContext;
                    ShaderBytecode Compile(string entry, string profile)
                    {
                        var r = ShaderBytecode.Compile(Hlsl, entry, profile, ShaderFlags.OptimizationLevel3, EffectFlags.None);
                        if (r.Bytecode == null) throw new Exception("shader " + entry + ": " + r.Message);
                        return Keep(r.Bytecode);
                    }
                    var vsMainBc = Compile("VSMain", "vs_5_0");
                    var vsMain = Keep(new VertexShader(dev, vsMainBc));
                    var psMain = Keep(new PixelShader(dev, Compile("PSMain", "ps_5_0")));
                    var vsShadowBc = Compile("VSShadow", "vs_5_0");
                    var vsShadow = Keep(new VertexShader(dev, vsShadowBc));
                    var psShadow = Keep(new PixelShader(dev, Compile("PSShadow", "ps_5_0")));
                    VertexShader vsFull = null;
                    PixelShader psGeo = null, psAo = null, psBlur = null;
                    if (quality)
                    {
                        vsFull = Keep(new VertexShader(dev, Compile("VSFull", "vs_5_0")));
                        psGeo = Keep(new PixelShader(dev, Compile("PSGeo", "ps_5_0")));
                        psAo = Keep(new PixelShader(dev, Compile("PSAO", "ps_5_0")));
                        psBlur = Keep(new PixelShader(dev, Compile("PSBlur", "ps_5_0")));
                    }
                    // quality sizes: MSAA samples (8 while the picture is small, else 4), shadow map 4096, AO at half size
                    int samples = 1, SM = f.SM, aoDiv = 2;
                    double lsc = f.Lsc;
                    if (quality)
                    {
                        samples = (long)f.IW * f.IH <= 8_000_000 && dev.CheckMultisampleQualityLevels(Format.R8G8B8A8_UNorm, 8) > 0 ? 8 : 4;
                        if (dev.CheckMultisampleQualityLevels(Format.R8G8B8A8_UNorm, samples) == 0) throw new NotSupportedException("no MSAA");
                        SM = 4096; lsc = f.Lsc * (SM - 4.0) / (f.SM - 4.0);
                    }
                    int aoW = (f.IW + aoDiv - 1) / aoDiv, aoH = (f.IH + aoDiv - 1) / aoDiv;
                    if (quality) device = device + $", {samples}x MSAA";
                    var elements = new[]
                    {
                        new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                        new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                        new InputElement("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
                        new InputElement("TEXCOORD", 0, Format.R32G32_Float, 36, 0),
                        new InputElement("TEXCOORD", 1, Format.R32_Float, 44, 0),
                    };
                    var layout = Keep(new InputLayout(dev, ShaderSignature.GetInputSignature(vsMainBc), elements));

                    // vertices grouped by material (one draw each)
                    var groups = tris.GroupBy(t => t.M).ToList();
                    var vdata = new float[tris.Count * 3 * 12];
                    var ranges = new List<(Mat M, int Start, int Count)>();
                    int vi = 0;
                    double zMin = 1e9, zMax = -1e9, lzMin = 1e9, lzMax = -1e9;
                    foreach (var g in groups)
                    {
                        int start = vi / 12;
                        foreach (var t in g)
                        {
                            var T = t.T ?? new float[9];
                            void V(float x, float y, float z, int c, float u, float v, float sg)
                            {
                                vdata[vi++] = x; vdata[vi++] = y; vdata[vi++] = z;
                                vdata[vi++] = t.N[c * 3]; vdata[vi++] = t.N[c * 3 + 1]; vdata[vi++] = t.N[c * 3 + 2];
                                vdata[vi++] = T[c * 3]; vdata[vi++] = T[c * 3 + 1]; vdata[vi++] = T[c * 3 + 2];
                                vdata[vi++] = u; vdata[vi++] = v; vdata[vi++] = sg;
                                zMin = Math.Min(zMin, z); zMax = Math.Max(zMax, z);
                                double lz = f.Key[0] * x + f.Key[1] * y + f.Key[2] * z;
                                lzMin = Math.Min(lzMin, lz); lzMax = Math.Max(lzMax, lz);
                            }
                            V(t.X0, t.Y0, t.Z0, 0, t.U0, t.V0, t.Sg0);
                            V(t.X1, t.Y1, t.Z1, 1, t.U1, t.V1, t.Sg1);
                            V(t.X2, t.Y2, t.Z2, 2, t.U2, t.V2, t.Sg2);
                        }
                        ranges.Add((g.Key, start, vi / 12 - start));
                    }
                    var vb = Keep(D3D11Buffer.Create(dev, BindFlags.VertexBuffer, vdata));

                    // textures (each once)
                    var srvOf = new Dictionary<Tex, ShaderResourceView>();
                    ShaderResourceView Srv(Tex t)
                    {
                        if (t == null) return null;
                        if (srvOf.TryGetValue(t, out var s)) return s;
                        // compressed blocks straight from the .tex (BC sizes must be multiples of 4); else decoded pixels
                        Format fmt = Format.R8G8B8A8_UNorm;
                        byte[] data = null;
                        long off = 0;
                        int pitch = t.W * 4;
                        if (t.File != null && (t.W % 4 == 0 && t.H % 4 == 0 || t.Format == 28 || t.Format == 29))
                        {
                            fmt = t.Format == 71 || t.Format == 72 ? Format.BC1_UNorm : t.Format == 77 || t.Format == 78 ? Format.BC3_UNorm
                                : t.Format == 98 || t.Format == 99 ? Format.BC7_UNorm : Format.R8G8B8A8_UNorm;
                            TexDecode.MipInfo(t.File, t.Level, out _, out _, out _, out off, out _, out int size);
                            int bpb = fmt == Format.BC1_UNorm ? 8 : 16;
                            pitch = fmt == Format.R8G8B8A8_UNorm ? t.W * 4 : (t.W / 4) * bpb;
                            int need = fmt == Format.R8G8B8A8_UNorm ? pitch * t.H : pitch * (t.H / 4);
                            if (off + need <= t.File.Length) data = t.File; else fmt = Format.R8G8B8A8_UNorm;
                        }
                        if (data == null) { data = t.Px; off = 0; pitch = t.W * 4; }
                        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
                        try
                        {
                            // quality: the whole mip chain from the largest level up to 4096 (the card picks the level per
                            // pixel and filters between them: no shimmering from a single large level)
                            if (quality && data == t.File)
                            {
                                var full = MipChain(t, fmt, h.AddrOfPinnedObject(), out int w0, out int h0);
                                if (full != null)
                                {
                                    var texM = Keep(new Texture2D(dev, new Texture2DDescription
                                    {
                                        Width = w0, Height = h0, MipLevels = full.Length, ArraySize = 1, Format = fmt,
                                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                                    }, full));
                                    return srvOf[t] = Keep(new ShaderResourceView(dev, texM));
                                }
                            }
                            var tex = Keep(new Texture2D(dev, new Texture2DDescription
                            {
                                Width = t.W, Height = t.H, MipLevels = 1, ArraySize = 1, Format = fmt,
                                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                            }, new DataRectangle(h.AddrOfPinnedObject() + (int)off, pitch)));
                            return srvOf[t] = Keep(new ShaderResourceView(dev, tex));
                        }
                        finally { h.Free(); }
                    }

                    D3D11Buffer Cb(int floats) => Keep(new D3D11Buffer(dev, floats * 4, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
                    var cbFrame = Cb(48);
                    var cbMat = Cb(28);
                    float F(double x) => (float)x;
                    bool persp = f.Persp;
                    double zA = persp ? 0.01 : zMax, zB = persp ? 1 / Math.Max(1e-3, -zMin + 1 - 0.01) : 1 / Math.Max(1e-6, zMax - zMin + 1e-3);
                    double lzScale = 1 / Math.Max(1e-6, lzMax - lzMin + 1e-3);
                    ctx.UpdateSubresource(new[]
                    {
                        f.IW, f.IH, persp ? 1 : 0, F(f.Fpx),
                        F(f.Cx), F(f.Cy), F(f.Sc), f.Dark ? 1 : 0,
                        F(f.ShX), F(f.ShY), F(zA), F(zB),
                        F(f.Key[0]), F(f.Key[1]), F(f.Key[2]), F(f.LKey),
                        F(f.Fill[0]), F(f.Fill[1]), F(f.Fill[2]), SM,
                        F(f.KeyC[0]), F(f.KeyC[1]), F(f.KeyC[2]), F(f.MnY),
                        F(f.RimC[0]), F(f.RimC[1]), F(f.RimC[2]), F(f.MxY),
                        F(f.LX[0]), F(f.LX[1]), F(f.LX[2]), F(f.Lx0),
                        F(f.LY[0]), F(f.LY[1]), F(f.LY[2]), F(f.Ly0),
                        F(lsc), F(lzMax + 1e-3), F(lzScale), 7f / 2048,
                        quality ? 1 : 0, samples, aoDiv, quality ? 1f / 255 : 1,
                        F(AoRadius * (f.MxY - f.MnY)), aoW, aoH, Environment.GetEnvironmentVariable("CO_AO_SHOW") == "1" ? 1 : 0,
                    }, cbFrame);
                    void SetMaterial(Mat m)
                    {
                        bool cut = m.Color != null && (m.Color.Alpha || m.HairAlpha);
                        bool hair = m.Spec2 != null && m.Sharp1 > 0;
                        double[] s1 = m.Spec1 ?? new double[3], s2 = m.Spec2 ?? new double[3];
                        ctx.UpdateSubresource(new[]
                        {
                            m.Color != null ? 1f : 0, m.Atos != null ? 1f : 0, m.Nrm != null ? 1f : 0, cut ? 1f : 0,
                            m.Color != null && m.Color.NoAlpha ? 1f : 0, m.Skin ? 1f : 0, m.Cull ? 1f : 0, hair ? 1f : 0,
                            F(m.Tint?[0] ?? 1), F(m.Tint?[1] ?? 1), F(m.Tint?[2] ?? 1), m.Tint != null ? 1f : 0,
                            F(s1[0]), F(s1[1]), F(s1[2]), F(m.Sharp1),
                            F(s2[0]), F(s2[1]), F(s2[2]), F(m.Sharp2),
                            F(m.SpecInt), F(m.Shift), m.Eye ? 1f : 0, 2f, // hair coverage: alpha x2 (fuller hair, chosen on renders of the Grace mod)
                            m.Face ? 1f : 0, 0.35f, 0, 0, // face: painted scalp between strands at 35% brightness
                        }, cbMat);
                        ctx.PixelShader.SetShaderResources(0, Srv(m.Color), Srv(m.Atos), Srv(m.Nrm));
                    }

                    var sampler = Keep(new SamplerState(dev, new SamplerStateDescription
                    {
                        Filter = quality ? Filter.Anisotropic : Filter.MinMagMipLinear, MaximumAnisotropy = quality ? 16 : 1,
                        AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
                        ComparisonFunction = Comparison.Never, MaximumLod = float.MaxValue,
                    }));
                    SamplerState Cmp(Comparison c) => Keep(new SamplerState(dev, new SamplerStateDescription
                    {
                        Filter = Filter.ComparisonMinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
                        AddressW = TextureAddressMode.Clamp, ComparisonFunction = c, MaximumLod = float.MaxValue,
                    }));
                    var clampS = Keep(new SamplerState(dev, new SamplerStateDescription
                    {
                        Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                        ComparisonFunction = Comparison.Never, MaximumLod = float.MaxValue,
                    }));
                    var raster = Keep(new RasterizerState(dev, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid, IsDepthClipEnabled = true, IsMultisampleEnabled = samples > 1 }));
                    var depthState = Keep(new DepthStencilState(dev, new DepthStencilStateDescription { IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.Less }));
                    ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                    ctx.InputAssembler.InputLayout = layout;
                    ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vb, 48, 0));
                    ctx.Rasterizer.State = raster;
                    ctx.OutputMerger.SetDepthStencilState(depthState);
                    ctx.VertexShader.SetConstantBuffers(0, cbFrame, cbMat);
                    ctx.PixelShader.SetConstantBuffers(0, cbFrame, cbMat);
                    ctx.PixelShader.SetSampler(0, sampler);
                    if (quality) ctx.PixelShader.SetSamplers(1, Cmp(Comparison.GreaterEqual), Cmp(Comparison.Less), clampS);

                    (Texture2D Tex, RenderTargetView Rtv, DepthStencilView Dsv) Target(int w, int h, Format fmt, bool srv, int ms = 1)
                    {
                        var t = Keep(new Texture2D(dev, new Texture2DDescription
                        {
                            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = fmt, SampleDescription = new SampleDescription(ms, 0),
                            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | (srv ? BindFlags.ShaderResource : 0),
                        }));
                        var d = Keep(new Texture2D(dev, new Texture2DDescription
                        {
                            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.D32_Float, SampleDescription = new SampleDescription(ms, 0),
                            Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
                        }));
                        return (t, Keep(new RenderTargetView(dev, t)), Keep(new DepthStencilView(dev, d)));
                    }

                    // 1. shadow maps of the key light (solid parts / hair)
                    var smSolid = Target(SM, SM, Format.R32_Float, true);
                    var smHair = Target(SM, SM, Format.R32_Float, true);
                    ctx.VertexShader.Set(vsShadow);
                    ctx.PixelShader.Set(psShadow);
                    ctx.Rasterizer.SetViewport(0, 0, SM, SM);
                    foreach (var (tgt, hairPass) in new[] { (smSolid, false), (smHair, true) })
                    {
                        ctx.OutputMerger.SetTargets(tgt.Dsv, tgt.Rtv);
                        ctx.ClearRenderTargetView(tgt.Rtv, new RawColor4(-1e9f, 0, 0, 0));
                        ctx.ClearDepthStencilView(tgt.Dsv, DepthStencilClearFlags.Depth, 1f, 0);
                        foreach (var r in ranges.Where(r => r.M.HairAlpha == hairPass)) { SetMaterial(r.M); ctx.Draw(r.Count, r.Start); }
                    }
                    ctx.OutputMerger.SetTargets((DepthStencilView)null, (RenderTargetView)null);
                    var srvSolid = Keep(new ShaderResourceView(dev, smSolid.Tex));
                    var srvHair = Keep(new ShaderResourceView(dev, smHair.Tex));

                    // quality: contact shadows (screen-space ambient occlusion at half size, then a depth-aware blur)
                    ShaderResourceView srvAo = null, srvGpos = null;
                    if (quality)
                    {
                        var gp = Target(aoW, aoH, Format.R32G32B32A32_Float, true);
                        var gn = Target(aoW, aoH, Format.R16G16B16A16_Float, true);
                        ctx.VertexShader.Set(vsMain);
                        ctx.PixelShader.Set(psGeo);
                        ctx.Rasterizer.SetViewport(0, 0, aoW, aoH);
                        ctx.OutputMerger.SetTargets(gp.Dsv, gp.Rtv, gn.Rtv);
                        ctx.ClearRenderTargetView(gp.Rtv, new RawColor4(0, 0, 0, 0));
                        ctx.ClearRenderTargetView(gn.Rtv, new RawColor4(0, 0, 1, 0));
                        ctx.ClearDepthStencilView(gp.Dsv, DepthStencilClearFlags.Depth, 1f, 0);
                        foreach (var r in ranges) { SetMaterial(r.M); ctx.Draw(r.Count, r.Start); }
                        ctx.OutputMerger.SetTargets((DepthStencilView)null, (RenderTargetView)null);
                        var srvGp = Keep(new ShaderResourceView(dev, gp.Tex));
                        srvGpos = srvGp;
                        var srvGn = Keep(new ShaderResourceView(dev, gn.Tex));
                        var aoRaw = Target(aoW, aoH, Format.R16_Float, true);
                        var aoBlur = Target(aoW, aoH, Format.R16_Float, true);
                        ctx.InputAssembler.InputLayout = null;
                        ctx.VertexShader.Set(vsFull);
                        ctx.PixelShader.SetShaderResources(6, srvGp, srvGn);
                        ctx.PixelShader.Set(psAo);
                        ctx.OutputMerger.SetTargets(aoRaw.Rtv);
                        ctx.Draw(3, 0);
                        ctx.OutputMerger.SetTargets((RenderTargetView)null);
                        var srvRaw = Keep(new ShaderResourceView(dev, aoRaw.Tex));
                        ctx.PixelShader.SetShaderResource(5, srvRaw);
                        ctx.PixelShader.Set(psBlur);
                        ctx.OutputMerger.SetTargets(aoBlur.Rtv);
                        ctx.Draw(3, 0);
                        ctx.OutputMerger.SetTargets((RenderTargetView)null);
                        ctx.PixelShader.SetShaderResources(5, 3, new ShaderResourceView[3]);
                        srvAo = Keep(new ShaderResourceView(dev, aoBlur.Tex));
                        ctx.InputAssembler.InputLayout = layout;
                    }

                    // 2. the picture (quality: MSAA in 8-bit sRGB values, resolved below)
                    var main = quality ? Target(f.IW, f.IH, Format.R8G8B8A8_UNorm, false, samples) : Target(f.IW, f.IH, Format.R32G32B32A32_Float, false);
                    ctx.VertexShader.Set(vsMain);
                    ctx.PixelShader.Set(psMain);
                    ctx.Rasterizer.SetViewport(0, 0, f.IW, f.IH);
                    ctx.OutputMerger.SetTargets(main.Dsv, main.Rtv);
                    ctx.ClearRenderTargetView(main.Rtv, new RawColor4(0, 0, 0, 0));
                    ctx.ClearDepthStencilView(main.Dsv, DepthStencilClearFlags.Depth, 1f, 0);
                    ctx.PixelShader.SetShaderResources(3, srvSolid, srvHair, srvAo, srvGpos);
                    foreach (var r in ranges) { SetMaterial(r.M); ctx.Draw(r.Count, r.Start); }

                    // read back
                    var img = new float[f.IW * f.IH * 4];
                    Texture2D src = main.Tex;
                    Format rf = quality ? Format.R8G8B8A8_UNorm : Format.R32G32B32A32_Float;
                    if (quality)
                    {
                        src = Keep(new Texture2D(dev, new Texture2DDescription
                        {
                            Width = f.IW, Height = f.IH, MipLevels = 1, ArraySize = 1, Format = rf,
                            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.None,
                        }));
                        ctx.ResolveSubresource(main.Tex, 0, src, 0, rf);
                    }
                    var staging = Keep(new Texture2D(dev, new Texture2DDescription
                    {
                        Width = f.IW, Height = f.IH, MipLevels = 1, ArraySize = 1, Format = rf,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read,
                    }));
                    ctx.CopyResource(src, staging);
                    var box = ctx.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        if (!quality)
                            for (int y = 0; y < f.IH; y++) Marshal.Copy(box.DataPointer + y * box.RowPitch, img, y * f.IW * 4, f.IW * 4);
                        else
                        {
                            // resolved = average of the samples (background 0): colors are premultiplied by the coverage
                            var row = new byte[f.IW * 4];
                            for (int y = 0; y < f.IH; y++)
                            {
                                Marshal.Copy(box.DataPointer + y * box.RowPitch, row, 0, row.Length);
                                for (int x = 0, o = y * f.IW * 4; x < f.IW; x++, o += 4)
                                {
                                    int a = row[x * 4 + 3];
                                    if (a == 0) continue;
                                    for (int k = 0; k < 3; k++) img[o + k] = Math.Min(255f, row[x * 4 + k] * 255f / a);
                                    img[o + 3] = a;
                                }
                            }
                        }
                    }
                    finally { ctx.UnmapSubresource(staging, 0); }
                    ctx.ClearState();
                    return img;
                }
                finally
                {
                    for (int i = trash.Count - 1; i >= 0; i--) { try { trash[i].Dispose(); } catch { } }
                }
            }
        }
    }
}
