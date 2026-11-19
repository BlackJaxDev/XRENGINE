// Engine-authored compatibility include for translated Unity programs.
// Unity compiles HLSL with the legacy FXC rules. These rules truncate a larger
// vector or matrix to a smaller one without a cast, and give a warning only.
// Slang reports an error for the same code. This file adds the implicit
// truncating conversions and the mixed-size operators, so that the Slang
// frontend accepts the same programs as Unity. The results are the same as FXC:
// the operation uses the leading components.
#ifndef XR_HLSL_LEGACY_CONVERSIONS_INCLUDED
#define XR_HLSL_LEGACY_CONVERSIONS_INCLUDED

// A high cost makes overload resolution prefer every exact or widening match.
#define XR_TRUNCATION_COST 900

// Vector to smaller vector. The extensions name concrete element types, because a generic
// element type would also match vector types and make constructors such as int2(0, x) ambiguous.
#define XR_VECTOR_TRUNCATION(TYPE) \
    extension vector<TYPE, 2> \
    { \
        __implicit_conversion(XR_TRUNCATION_COST) __init(vector<TYPE, 3> value) { this = value.xy; } \
        __implicit_conversion(XR_TRUNCATION_COST) __init(vector<TYPE, 4> value) { this = value.xy; } \
    } \
    extension vector<TYPE, 3> \
    { \
        __implicit_conversion(XR_TRUNCATION_COST) __init(vector<TYPE, 4> value) { this = value.xyz; } \
    }

XR_VECTOR_TRUNCATION(float)
XR_VECTOR_TRUNCATION(int)
XR_VECTOR_TRUNCATION(uint)
XR_VECTOR_TRUNCATION(bool)

// Vector to scalar. FXC also converts the component type, for example from float2 to int.
// There is no conversion from an int vector to uint: with it, Slang resolves uint2 + int2 to an invalid type.
#define XR_SCALAR_TRUNCATION(TYPE, SOURCE) \
    extension TYPE \
    { \
        __implicit_conversion(XR_TRUNCATION_COST) __init(vector<SOURCE, 2> value) { this = TYPE(value.x); } \
        __implicit_conversion(XR_TRUNCATION_COST) __init(vector<SOURCE, 3> value) { this = TYPE(value.x); } \
        __implicit_conversion(XR_TRUNCATION_COST) __init(vector<SOURCE, 4> value) { this = TYPE(value.x); } \
    }

XR_SCALAR_TRUNCATION(float, float)
XR_SCALAR_TRUNCATION(float, int)
XR_SCALAR_TRUNCATION(float, uint)
XR_SCALAR_TRUNCATION(int, int)
XR_SCALAR_TRUNCATION(int, float)
XR_SCALAR_TRUNCATION(int, uint)
XR_SCALAR_TRUNCATION(uint, uint)
XR_SCALAR_TRUNCATION(uint, float)

// Matrix to smaller square matrix.
extension matrix<float, 3, 3>
{
    __implicit_conversion(XR_TRUNCATION_COST) __init(matrix<float, 4, 4> value)
    {
        this = matrix<float, 3, 3>(value[0].xyz, value[1].xyz, value[2].xyz);
    }
}

extension matrix<float, 2, 2>
{
    __implicit_conversion(XR_TRUNCATION_COST) __init(matrix<float, 3, 3> value) { this = matrix<float, 2, 2>(value[0].xy, value[1].xy); }
    __implicit_conversion(XR_TRUNCATION_COST) __init(matrix<float, 4, 4> value) { this = matrix<float, 2, 2>(value[0].xy, value[1].xy); }
}

// Binary operators on vectors of different sizes. The result has the smaller size.
// SWIZZLE_A and SWIZZLE_B select the leading components of the larger operand.
#define XR_MIXED_ARITHMETIC(OP, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    __generic<T : __BuiltinArithmeticType> \
    vector<T, SIZE> operator OP(vector<T, SIZE_A> a, vector<T, SIZE_B> b) { return a SWIZZLE_A OP b SWIZZLE_B; }

#define XR_MIXED_COMPARISON(OP, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    __generic<T : __BuiltinArithmeticType> \
    vector<bool, SIZE> operator OP(vector<T, SIZE_A> a, vector<T, SIZE_B> b) { return a SWIZZLE_A OP b SWIZZLE_B; }

#define XR_MIXED_OPERATORS(SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_ARITHMETIC(+, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_ARITHMETIC(-, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_ARITHMETIC(*, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_ARITHMETIC(/, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_COMPARISON(<, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_COMPARISON(>, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_COMPARISON(<=, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_COMPARISON(>=, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_COMPARISON(==, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_COMPARISON(!=, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B)

XR_MIXED_OPERATORS(2, 3, 2, , .xy)
XR_MIXED_OPERATORS(2, 4, 2, , .xy)
XR_MIXED_OPERATORS(3, 4, 3, , .xyz)
XR_MIXED_OPERATORS(3, 2, 2, .xy, )
XR_MIXED_OPERATORS(4, 2, 2, .xy, )
XR_MIXED_OPERATORS(4, 3, 3, .xyz, )

// Compound assignment where the right operand is larger than the left operand.
#define XR_TRUNCATING_ASSIGNMENT(OP, SIZE_A, SIZE_B, SWIZZLE_B) \
    __generic<T : __BuiltinArithmeticType> \
    vector<T, SIZE_A> operator OP##=(inout vector<T, SIZE_A> a, vector<T, SIZE_B> b) { a = a OP b SWIZZLE_B; return a; }

#define XR_TRUNCATING_ASSIGNMENTS(SIZE_A, SIZE_B, SWIZZLE_B) \
    XR_TRUNCATING_ASSIGNMENT(+, SIZE_A, SIZE_B, SWIZZLE_B) \
    XR_TRUNCATING_ASSIGNMENT(-, SIZE_A, SIZE_B, SWIZZLE_B) \
    XR_TRUNCATING_ASSIGNMENT(*, SIZE_A, SIZE_B, SWIZZLE_B) \
    XR_TRUNCATING_ASSIGNMENT(/, SIZE_A, SIZE_B, SWIZZLE_B)

XR_TRUNCATING_ASSIGNMENTS(2, 3, .xy)
XR_TRUNCATING_ASSIGNMENTS(2, 4, .xy)
XR_TRUNCATING_ASSIGNMENTS(3, 4, .xyz)

// Compound assignment of a vector to a scalar.
#define XR_SCALAR_TRUNCATING_ASSIGNMENT(OP) \
    __generic<T : __BuiltinArithmeticType, let SIZE : int> \
    T operator OP##=(inout T a, vector<T, SIZE> b) { a = a OP b[0]; return a; }

XR_SCALAR_TRUNCATING_ASSIGNMENT(+)
XR_SCALAR_TRUNCATING_ASSIGNMENT(-)
XR_SCALAR_TRUNCATING_ASSIGNMENT(*)
XR_SCALAR_TRUNCATING_ASSIGNMENT(/)

#endif
