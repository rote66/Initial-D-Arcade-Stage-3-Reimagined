#pragma once
#include "math_types.h"
#include <array>
#include <cmath>

namespace idas3::portable {
// Row-major matrices for the existing row-vector, D3D-depth scene ABI.
using Matrix = std::array<float,16>;
inline Matrix multiply(const Matrix& a,const Matrix& b){
    Matrix result{};
    for(unsigned row=0;row<4;++row)for(unsigned col=0;col<4;++col)
        for(unsigned k=0;k<4;++k)result[row*4+col]+=a[row*4+k]*b[k*4+col];
    return result;
}
inline Matrix lookAt(Vec3 eye,Vec3 target,Vec3 up,bool leftHanded){
    const auto z=normalized(leftHanded?target-eye:eye-target);
    const auto x=normalized(cross(up,z)),y=cross(z,x);
    return {x.x,y.x,z.x,0,x.y,y.y,z.y,0,x.z,y.z,z.z,0,
        -dot(x,eye),-dot(y,eye),-dot(z,eye),1};
}
inline Matrix perspective(float fov,float aspect,float nearClip,float farClip,bool leftHanded){
    const float y=1.f/std::tan(fov*.5f),range=farClip/(leftHanded?farClip-nearClip:nearClip-farClip);
    return {y/aspect,0,0,0,0,y,0,0,0,0,range,leftHanded?1.f:-1.f,
        0,0,leftHanded?-nearClip*range:nearClip*range,0};
}
}
