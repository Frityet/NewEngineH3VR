#ifndef NATIVEH3VR_STOCK_COLOR_GRADING_INCLUDED
#define NATIVEH3VR_STOCK_COLOR_GRADING_INCLUDED

TEXTURE3D(_H3VRStockLut3D);
TEXTURE2D(_H3VRStockLut2D);
float4 _H3VRStockGrade; // mode, stock exposure multiplier, inverse size, size minus one

float3 ApplyStockColorGrading(float3 color)
{
    if (_H3VRStockGrade.x < 0.5)
        return color;

    const float2 size = _H3VRStockGrade.zw;
    if (_H3VRStockGrade.x < 1.5)
        return ApplyLut3D(TEXTURE3D_ARGS(_H3VRStockLut3D, sampler_LinearClamp),
            saturate(LinearToLogC(color * _H3VRStockGrade.y)), size);

    float3 coordinate = _H3VRStockGrade.x < 2.5
        ? saturate(LinearToLogC(color * _H3VRStockGrade.y)) : saturate(color);
    return ApplyLut2D(TEXTURE2D_ARGS(_H3VRStockLut2D, sampler_LinearClamp), coordinate,
        float3(size.x * size.x, size));
}
#endif
