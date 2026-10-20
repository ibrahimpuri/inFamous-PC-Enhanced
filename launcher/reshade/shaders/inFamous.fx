// inFamous.fx: ReShade effects for the inFamous PC port, driven by the inFamous Launcher's ReShade page.
//
// Every technique works on the finished frame (after DLSS), so it also touches the HUD; the strengths the
// launcher's looks use are mild enough that the HUD stays clean. Self-contained: no ReShade.fxh needed.
//
//   inFamous_Deband     smooths the 8-bit colour banding in the sky, fog and smoke
//   inFamous_AO         ambient occlusion from the scene depth (soft contact shadows in corners and under objects)
//   inFamous_Clarity    local contrast: brings out texture and surface detail without touching overall contrast
//   inFamous_Color      exposure, contrast, shadows / highlights, haze removal, vibrance, saturation, white balance
//   inFamous_Sharpen    contrast-adaptive sharpening (after AMD FidelityFX CAS, MIT licence)
//   inFamous_Vignette   darkens the frame edges
//   inFamous_DepthView  debug: shows the depth ReShade gets (white = near)
//   inFamous_AOView     debug: shows the occlusion inFamous_AO adds
//   inFamous_CompareBegin / _CompareEnd   debug: split screen, untouched frame on the left

// ---------------------------------------------------------------------------------------------------------------
// The game's depth: 24-bit, not reversed, 1.0 = far. The launcher sets these when it knows better.
#ifndef INFAMOUS_DEPTH_FAR
    #define INFAMOUS_DEPTH_FAR 1000.0
#endif

texture BackBufferTex : COLOR;
texture DepthBufferTex : DEPTH;
sampler BackBuffer      { Texture = BackBufferTex; };
sampler BackBufferPoint { Texture = BackBufferTex; MagFilter = POINT; MinFilter = POINT; MipFilter = POINT; };
sampler DepthBuffer     { Texture = DepthBufferTex; MagFilter = POINT; MinFilter = POINT; MipFilter = POINT; };

#define PX float2(BUFFER_RCP_WIDTH, BUFFER_RCP_HEIGHT)
static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);

uniform int FrameCount < source = "framecount"; >;

// ---------------------------------------------------------------------------------------------------------------
// Settings (the launcher writes them into its preset; the ReShade overlay shows them too)
uniform float DebandStrength < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Deband";
    ui_label = "Strength"; ui_tooltip = "How large a colour step still counts as banding. Higher smooths more, but can soften faint texture."; > = 0.5;

uniform float AOStrength < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Ambient occlusion";
    ui_label = "Strength"; > = 0.5;
uniform float AORadius < ui_type = "slider"; ui_min = 0.2; ui_max = 2.0; ui_step = 0.05; ui_category = "Ambient occlusion";
    ui_label = "Radius"; ui_tooltip = "How far the occlusion reaches, relative to the screen."; > = 1.0;

uniform float Clarity < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Clarity";
    ui_label = "Strength"; ui_tooltip = "Local contrast: makes brick, concrete and cloth detail stand out."; > = 0.35;

uniform float Exposure < ui_type = "slider"; ui_min = -1.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Exposure"; ui_tooltip = "In stops."; > = 0.0;
uniform float Contrast < ui_type = "slider"; ui_min = -0.5; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Contrast"; > = 0.0;
uniform float Shadows < ui_type = "slider"; ui_min = -1.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Shadows"; ui_tooltip = "Lifts (+) or deepens (-) the dark tones."; > = 0.0;
uniform float Highlights < ui_type = "slider"; ui_min = -1.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Highlights"; ui_tooltip = "Brightens (+) or recovers (-) the bright tones."; > = 0.0;
uniform float Dehaze < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Haze removal"; ui_tooltip = "Pulls the washed-out black level of the game's haze back down."; > = 0.0;
uniform float Vibrance < ui_type = "slider"; ui_min = -1.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Vibrance"; ui_tooltip = "Saturates dull colours more than vivid ones, so skin tones stay natural."; > = 0.0;
uniform float Saturation < ui_type = "slider"; ui_min = 0.0; ui_max = 2.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Saturation"; > = 1.0;
uniform float Warmth < ui_type = "slider"; ui_min = -1.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Warmth"; ui_tooltip = "Cooler (-) or warmer (+). inFamous is graded quite yellow."; > = 0.0;
uniform float Tint < ui_type = "slider"; ui_min = -1.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Colour";
    ui_label = "Tint"; ui_tooltip = "Magenta (-) or green (+)."; > = 0.0;

uniform float Sharpen < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Sharpen";
    ui_label = "Strength"; > = 0.5;

uniform float Vignette < ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01; ui_category = "Vignette";
    ui_label = "Strength"; > = 0.3;

// ---------------------------------------------------------------------------------------------------------------
void FullscreenVS(in uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD)
{
    uv.x = (id == 2) ? 2.0 : 0.0;
    uv.y = (id == 1) ? 2.0 : 0.0;
    pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

float Hash(float2 p)   // stable per-pixel noise in [0, 1)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float RawDepth(float2 uv) { return tex2Dlod(DepthBuffer, float4(uv, 0, 0)).x; }
float LinearDepth(float2 uv)   // 0 = camera, 1 = far plane
{
    float d = RawDepth(uv);
    return d / (INFAMOUS_DEPTH_FAR - d * (INFAMOUS_DEPTH_FAR - 1.0));
}

// ---------------------------------------------------------------------------------------------------------------
// Deband: where every sample in a random ring around the pixel is within a colour step or two of it, the area is a
// smooth gradient that 8 bits quantised into bands; replace the pixel by the ring's average and dither the result.
float4 PS_Deband(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float3 c = tex2D(BackBufferPoint, uv).rgb;
    float n1 = Hash(pos.xy), n2 = Hash(pos.yx + 17.0);
    float threshold = lerp(1.5, 4.0, DebandStrength) / 255.0;
    float radius = lerp(6.0, 20.0, DebandStrength) * (0.5 + 0.5 * n2) * (BUFFER_HEIGHT / 1080.0);
    float a = n1 * 6.2831853;
    float2 o = float2(cos(a), sin(a)) * radius * PX;
    float3 s1 = tex2Dlod(BackBuffer, float4(uv + o, 0, 0)).rgb;
    float3 s2 = tex2Dlod(BackBuffer, float4(uv - o, 0, 0)).rgb;
    float3 s3 = tex2Dlod(BackBuffer, float4(uv + float2(-o.y, o.x), 0, 0)).rgb;
    float3 s4 = tex2Dlod(BackBuffer, float4(uv + float2(o.y, -o.x), 0, 0)).rgb;
    float3 d = max(max(abs(s1 - c), abs(s2 - c)), max(abs(s3 - c), abs(s4 - c)));
    float w = saturate(2.0 - max(d.r, max(d.g, d.b)) / threshold);   // 1 well inside the threshold, 0 past twice it
    float3 o3 = lerp(c, (s1 + s2 + s3 + s4) * 0.25, w);
    o3 += (n1 - n2) * (w / 255.0);   // triangular dither, only where it smoothed
    return float4(o3, 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Ambient occlusion: a small horizon-style AO at half resolution from the linear depth, blurred depth-aware and
// multiplied in. The sky (depth 1) and the HUD-free far plane are left alone.
texture AOTex  { Width = BUFFER_WIDTH / 2; Height = BUFFER_HEIGHT / 2; Format = R8; };
texture AOTex2 { Width = BUFFER_WIDTH / 2; Height = BUFFER_HEIGHT / 2; Format = R8; };
sampler AOSamp  { Texture = AOTex; };
sampler AOSamp2 { Texture = AOTex2; };

float Lin(float d) { return d / (INFAMOUS_DEPTH_FAR - d * (INFAMOUS_DEPTH_FAR - 1.0)); }

// Raw (non-linear) depth is an affine function of 1/z, so across a flat surface it changes linearly on screen,
// whatever the game's near and far planes are. The AO extends the pixel's plane to each sample and counts how far
// the scene there sticks out in front of that plane (a wall rising from the floor, a ledge over a doorway).
float PS_AO(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float d0 = RawDepth(uv), z = Lin(d0);
    if (z > 0.98) return 1.0;
    // gradient per depth texel: with DLSS upscaling the depth buffer is smaller than the screen
    float2 dpx = 1.0 / float2(tex2Dsize(DepthBuffer, 0));
    float dxp = (RawDepth(uv + float2(dpx.x * 2.0, 0)) - d0) * 0.5, dxn = (d0 - RawDepth(uv - float2(dpx.x * 2.0, 0))) * 0.5;
    float dyp = (RawDepth(uv + float2(0, dpx.y * 2.0)) - d0) * 0.5, dyn = (d0 - RawDepth(uv - float2(0, dpx.y * 2.0))) * 0.5;
    float gx = abs(dxp) < abs(dxn) ? dxp : dxn;   // from the flatter neighbour, so depth edges don't outline
    float gy = abs(dyp) < abs(dyn) ? dyp : dyn;
    float radius = AORadius * 0.035 / max(z * 40.0, 0.35);   // screen height fraction, shrinks with distance
    float rpx = radius * BUFFER_HEIGHT;
    float n = Hash(pos.xy), n2 = Hash(pos.yx + 7.0);
    float occ = 0.0;
    [unroll] for (int i = 0; i < 12; i++)
    {
        float a = (n + i * 0.381966) * 6.2831853;            // golden-angle spiral, denser near the pixel
        float t = (i + n2) / 12.0;
        float2 o = float2(cos(a), sin(a)) * rpx * t * sqrt(t) * PX;   // uv offset
        float zp = Lin(clamp(d0 + gx * o.x / dpx.x + gy * o.y / dpx.y, 0.0, 0.9999)), zs = Lin(RawDepth(uv + o));
        float h = (zp - zs) / (zp * radius * 2.0);            // in front of the plane, in units of the radius
        occ += saturate((h - 0.08) * 2.2) * saturate(2.0 - h);   // occludes up to one radius out, then fades
    }
    occ *= saturate(2.0 - z * 4.0);                           // fades out into the distance
    return saturate(1.0 - occ / 12.0 * 1.8);
}

float AOBlur(sampler s, float2 uv, float2 dir)
{
    float z = LinearDepth(uv), sum = 0.0, wsum = 0.0;
    [unroll] for (int i = -3; i <= 3; i++)
    {
        float2 t = uv + dir * (i * 2.0) * PX;
        float w = exp(-i * i * 0.18) * saturate(1.0 - abs(LinearDepth(t) - z) / max(z * 0.05, 1e-4));
        sum += tex2Dlod(s, float4(t, 0, 0)).x * w; wsum += w;
    }
    return sum / max(wsum, 1e-4);
}
float PS_AOBlurH(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target { return AOBlur(AOSamp, uv, float2(1, 0)); }
float PS_AOBlurV(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target { return AOBlur(AOSamp2, uv, float2(0, 1)); }

float4 PS_AOApply(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float3 c = tex2D(BackBufferPoint, uv).rgb;
    float ao = tex2D(AOSamp, uv).x;
    float l = dot(c, kLuma);
    ao = lerp(1.0, ao, AOStrength * saturate(1.2 - l));   // less in bright areas: sunlit walls keep their light
    return float4(c * ao, 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Clarity: luma blurred at quarter resolution; the difference to it is the local detail, soft-clipped so strong
// edges (rooftops against the sky) don't halo, and added back.
texture ClarityTex  { Width = BUFFER_WIDTH / 4; Height = BUFFER_HEIGHT / 4; Format = R16F; };
texture ClarityTex2 { Width = BUFFER_WIDTH / 4; Height = BUFFER_HEIGHT / 4; Format = R16F; };
sampler ClaritySamp  { Texture = ClarityTex; };
sampler ClaritySamp2 { Texture = ClarityTex2; };

float PS_ClarityDown(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float l = 0.0;
    [unroll] for (int y = -1; y <= 1; y += 2) [unroll] for (int x = -1; x <= 1; x += 2)
        l += dot(tex2Dlod(BackBuffer, float4(uv + float2(x, y) * PX, 0, 0)).rgb, kLuma);
    return l * 0.25;
}
static const float kGauss[5] = { 0.2270270, 0.1945946, 0.1216216, 0.0540540, 0.0162162 };
float ClarityBlur(sampler s, float2 uv, float2 dir)
{
    float2 stp = dir * PX * 4.0 * 1.5;
    float l = tex2Dlod(s, float4(uv, 0, 0)).x * kGauss[0];
    [unroll] for (int i = 1; i < 5; i++)
        l += (tex2Dlod(s, float4(uv + stp * i, 0, 0)).x + tex2Dlod(s, float4(uv - stp * i, 0, 0)).x) * kGauss[i];
    return l;
}
float PS_ClarityH(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target { return ClarityBlur(ClaritySamp, uv, float2(1, 0)); }
float PS_ClarityV(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target { return ClarityBlur(ClaritySamp2, uv, float2(0, 1)); }

float4 PS_ClarityApply(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float3 c = tex2D(BackBufferPoint, uv).rgb;
    float l = dot(c, kLuma);
    float d = l - tex2D(ClaritySamp, uv).x;
    d = d / (1.0 + abs(d) * 6.0);                       // soft clip: small detail boosted, big edges barely
    float mid = saturate(1.0 - abs(l * 2.0 - 1.0) * 0.8);   // most in the midtones
    return float4(saturate(c + d * Clarity * 1.6 * mid), 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Colour
float4 PS_Color(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float3 c = tex2D(BackBufferPoint, uv).rgb;
    float h = Dehaze * 0.06;
    c = saturate((c - h) / (1.0 - h));
    c *= exp2(Exposure);
    float3 wb = float3(1.0 + Warmth * 0.07, 1.0 + Tint * 0.05, 1.0 - Warmth * 0.07);
    c *= wb / dot(wb, kLuma);
    c = saturate(c);
    float3 ic = 1.0 - c;
    c += Shadows * 0.25 * 4.0 * c * ic * ic * ic;
    c += Highlights * 0.25 * 4.0 * c * c * c * ic;
    c = saturate(c);
    float3 s = c * c * (3.0 - 2.0 * c);
    c = Contrast >= 0.0 ? lerp(c, s, Contrast) : lerp(c, 0.5.xxx, -Contrast * 0.5);
    float l = dot(c, kLuma);
    float sat = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
    c = lerp(l.xxx, c, Saturation * (1.0 + Vibrance * (1.0 - sat)));
    return float4(saturate(c), 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
// Contrast-adaptive sharpening: the sharpening weight follows how much headroom the 3x3 neighbourhood leaves, so
// flat areas and strong edges are not overdone and nothing rings.
float4 PS_Sharpen(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float3 a = tex2D(BackBufferPoint, uv + float2(-1, -1) * PX).rgb;
    float3 b = tex2D(BackBufferPoint, uv + float2( 0, -1) * PX).rgb;
    float3 c = tex2D(BackBufferPoint, uv + float2( 1, -1) * PX).rgb;
    float3 d = tex2D(BackBufferPoint, uv + float2(-1,  0) * PX).rgb;
    float3 e = tex2D(BackBufferPoint, uv).rgb;
    float3 f = tex2D(BackBufferPoint, uv + float2( 1,  0) * PX).rgb;
    float3 g = tex2D(BackBufferPoint, uv + float2(-1,  1) * PX).rgb;
    float3 h = tex2D(BackBufferPoint, uv + float2( 0,  1) * PX).rgb;
    float3 i = tex2D(BackBufferPoint, uv + float2( 1,  1) * PX).rgb;
    float3 mn = min(min(min(d, e), min(f, b)), h);
    mn += min(mn, min(min(a, c), min(g, i)));
    float3 mx = max(max(max(d, e), max(f, b)), h);
    mx += max(mx, max(max(a, c), max(g, i)));
    float3 amp = sqrt(saturate(min(mn, 2.0 - mx) / max(mx, 1e-4)));
    float3 w = amp * (-1.0 / lerp(8.0, 4.5, Sharpen));
    float3 o = ((b + d + f + h) * w + e) / (1.0 + 4.0 * w);
    return float4(saturate(lerp(e, o, saturate(Sharpen * 3.0))), 1.0);
}

// ---------------------------------------------------------------------------------------------------------------
float4 PS_Vignette(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float3 c = tex2D(BackBufferPoint, uv).rgb;
    float2 p = (uv - 0.5) * float2(BUFFER_WIDTH * BUFFER_RCP_HEIGHT, 1.0);
    float v = smoothstep(0.45, 1.25, length(p));
    return float4(c * (1.0 - v * Vignette * 0.8), 1.0);
}

float4 PS_AOView(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target   // after inFamous_AO: the AO term
{
    return float4(lerp(1.0, tex2D(AOSamp, uv).x, AOStrength).xxx, 1.0);
}

float4 PS_DepthView(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float z = LinearDepth(uv);
    return float4((1.0 - pow(saturate(z), 0.25)).xxx, 1.0);
}

// Split-screen compare (debug): CompareBegin keeps the untouched frame, CompareEnd shows it in the left half.
texture CompareTex { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; Format = RGBA8; };
sampler CompareSamp { Texture = CompareTex; MagFilter = POINT; MinFilter = POINT; MipFilter = POINT; };
float4 PS_CompareCopy(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target { return tex2D(BackBufferPoint, uv); }
float4 PS_CompareShow(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
    float x = pos.x - BUFFER_WIDTH * 0.5;
    if (x < -1.0) return tex2D(CompareSamp, uv);
    if (x < 1.0) return float4(1.0, 1.0, 1.0, 1.0);
    return tex2D(BackBufferPoint, uv);
}

// ---------------------------------------------------------------------------------------------------------------
technique inFamous_Deband < ui_label = "inFamous: Deband"; ui_tooltip = "Smooths colour banding in the sky, fog and smoke."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_Deband; }
}
technique inFamous_AO < ui_label = "inFamous: Ambient occlusion"; ui_tooltip = "Soft contact shadows from the scene depth."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_AO; RenderTarget = AOTex; }
    pass { VertexShader = FullscreenVS; PixelShader = PS_AOBlurH; RenderTarget = AOTex2; }
    pass { VertexShader = FullscreenVS; PixelShader = PS_AOBlurV; RenderTarget = AOTex; }
    pass { VertexShader = FullscreenVS; PixelShader = PS_AOApply; }
}
technique inFamous_Clarity < ui_label = "inFamous: Clarity"; ui_tooltip = "Local contrast for texture and surface detail."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_ClarityDown; RenderTarget = ClarityTex; }
    pass { VertexShader = FullscreenVS; PixelShader = PS_ClarityH; RenderTarget = ClarityTex2; }
    pass { VertexShader = FullscreenVS; PixelShader = PS_ClarityV; RenderTarget = ClarityTex; }
    pass { VertexShader = FullscreenVS; PixelShader = PS_ClarityApply; }
}
technique inFamous_Color < ui_label = "inFamous: Colour"; ui_tooltip = "Exposure, contrast, haze removal, vibrance and white balance."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_Color; }
}
technique inFamous_Sharpen < ui_label = "inFamous: Sharpen"; ui_tooltip = "Contrast-adaptive sharpening."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_Sharpen; }
}
technique inFamous_Vignette < ui_label = "inFamous: Vignette"; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_Vignette; }
}
technique inFamous_DepthView < ui_label = "inFamous: Depth view (debug)"; ui_tooltip = "Shows the depth buffer ReShade found: white near, black far."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_DepthView; }
}
technique inFamous_AOView < ui_label = "inFamous: Ambient occlusion view (debug)"; ui_tooltip = "Put after Ambient occlusion: shows only the occlusion it adds."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_AOView; }
}
technique inFamous_CompareBegin < ui_label = "inFamous: Compare, start (debug)"; ui_tooltip = "Put first: keeps the untouched frame for Compare, end."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_CompareCopy; RenderTarget = CompareTex; }
}
technique inFamous_CompareEnd < ui_label = "inFamous: Compare, end (debug)"; ui_tooltip = "Put last: the untouched frame on the left, the effects on the right."; >
{
    pass { VertexShader = FullscreenVS; PixelShader = PS_CompareShow; }
}
