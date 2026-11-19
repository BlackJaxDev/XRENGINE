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
// Costs of component type conversions. Integer to float is preferred over float to integer.
#define XR_INTEGER_TO_FLOAT_COST 400
#define XR_FLOAT_TO_INTEGER_COST 800
#define XR_UNSIGNED_TO_SIGNED_COST 300

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

// Explicit constructors between vectors of the same size and a different component type, for example int2(uv).
// The built-in constructor does the same. Without an exact constructor, the scalar truncations above let Slang
// resolve int2(uv) in a generic call as a splat of int(uv), which gives an invalid vector of vectors.
// The constructors are also implicit, because an explicit constructor hides the built-in implicit conversion.
#define XR_VECTOR_COMPONENT_CAST(TYPE, SOURCE, COST) \
    extension vector<TYPE, 2> { __implicit_conversion(COST) __init(vector<SOURCE, 2> v) { this = vector<TYPE, 2>(TYPE(v.x), TYPE(v.y)); } } \
    extension vector<TYPE, 3> { __implicit_conversion(COST) __init(vector<SOURCE, 3> v) { this = vector<TYPE, 3>(TYPE(v.x), TYPE(v.y), TYPE(v.z)); } } \
    extension vector<TYPE, 4> { __implicit_conversion(COST) __init(vector<SOURCE, 4> v) { this = vector<TYPE, 4>(TYPE(v.x), TYPE(v.y), TYPE(v.z), TYPE(v.w)); } }

XR_VECTOR_COMPONENT_CAST(float, int, XR_INTEGER_TO_FLOAT_COST)
XR_VECTOR_COMPONENT_CAST(float, uint, XR_INTEGER_TO_FLOAT_COST)
XR_VECTOR_COMPONENT_CAST(int, float, XR_FLOAT_TO_INTEGER_COST)
XR_VECTOR_COMPONENT_CAST(uint, float, XR_FLOAT_TO_INTEGER_COST)
XR_VECTOR_COMPONENT_CAST(int, uint, XR_UNSIGNED_TO_SIGNED_COST)

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

// Intrinsic functions with vector arguments of different sizes. The built-in functions are generic
// over one vector type, so the implicit conversions above do not apply to them. The overloads are generic
// over the component type, as the built-in functions are. A non-generic overload would win over every
// built-in function and make calls such as pow(2, 3) ambiguous. A low overload rank makes the built-in
// function win when it also matches.
#define XR_MIXED_FUNCTION_RANK [OverloadRank(-100)]

#define XR_MIXED_BINARY_FUNCTION(NAME, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_FUNCTION_RANK __generic<T : __BuiltinFloatingPointType> \
    vector<T, SIZE> NAME(vector<T, SIZE_A> a, vector<T, SIZE_B> b) { return NAME(a SWIZZLE_A, b SWIZZLE_B); }

#define XR_MIXED_SCALAR_FUNCTION(NAME, SIZE_A, SIZE_B, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_FUNCTION_RANK __generic<T : __BuiltinFloatingPointType> \
    T NAME(vector<T, SIZE_A> a, vector<T, SIZE_B> b) { return NAME(a SWIZZLE_A, b SWIZZLE_B); }

#define XR_MIXED_FUNCTIONS(SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_BINARY_FUNCTION(min, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_BINARY_FUNCTION(max, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_BINARY_FUNCTION(pow, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_BINARY_FUNCTION(step, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_BINARY_FUNCTION(atan2, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_BINARY_FUNCTION(fmod, SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_SCALAR_FUNCTION(dot, SIZE_A, SIZE_B, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_SCALAR_FUNCTION(distance, SIZE_A, SIZE_B, SWIZZLE_A, SWIZZLE_B)

XR_MIXED_FUNCTIONS(2, 3, 2, , .xy)
XR_MIXED_FUNCTIONS(2, 4, 2, , .xy)
XR_MIXED_FUNCTIONS(3, 4, 3, , .xyz)
XR_MIXED_FUNCTIONS(3, 2, 2, .xy, )
XR_MIXED_FUNCTIONS(4, 2, 2, .xy, )
XR_MIXED_FUNCTIONS(4, 3, 3, .xyz, )

// lerp with end points of different sizes and a scalar weight.
#define XR_MIXED_LERP(SIZE_A, SIZE_B, SIZE, SWIZZLE_A, SWIZZLE_B) \
    XR_MIXED_FUNCTION_RANK __generic<T : __BuiltinFloatingPointType> \
    vector<T, SIZE> lerp(vector<T, SIZE_A> a, vector<T, SIZE_B> b, T t) { return lerp(a SWIZZLE_A, b SWIZZLE_B, vector<T, SIZE>(t)); }

XR_MIXED_LERP(2, 3, 2, , .xy)
XR_MIXED_LERP(2, 4, 2, , .xy)
XR_MIXED_LERP(3, 4, 3, , .xyz)
XR_MIXED_LERP(3, 2, 2, .xy, )
XR_MIXED_LERP(4, 2, 2, .xy, )
XR_MIXED_LERP(4, 3, 3, .xyz, )

// lerp with one scalar end point. Without these exact overloads, the scalar would match several of the overloads above.
#define XR_SCALAR_ARGUMENT_LERP(SIZE) \
    __generic<T : __BuiltinFloatingPointType> vector<T, SIZE> lerp(vector<T, SIZE> a, T b, T t) { return lerp(a, vector<T, SIZE>(b), vector<T, SIZE>(t)); } \
    __generic<T : __BuiltinFloatingPointType> vector<T, SIZE> lerp(T a, vector<T, SIZE> b, T t) { return lerp(vector<T, SIZE>(a), b, vector<T, SIZE>(t)); }

XR_SCALAR_ARGUMENT_LERP(2)
XR_SCALAR_ARGUMENT_LERP(3)
XR_SCALAR_ARGUMENT_LERP(4)

// A scalar argument would match several mixed-size overloads through promotion. These exact overloads
// remove that ambiguity. They give the same result as the built-in functions.
#define XR_SCALAR_ARGUMENT_FUNCTION_SIZE(NAME, SIZE) \
    __generic<T : __BuiltinFloatingPointType> vector<T, SIZE> NAME(vector<T, SIZE> a, T b) { return NAME(a, vector<T, SIZE>(b)); } \
    __generic<T : __BuiltinFloatingPointType> vector<T, SIZE> NAME(T a, vector<T, SIZE> b) { return NAME(vector<T, SIZE>(a), b); }

#define XR_SCALAR_ARGUMENT_DOT(SIZE) \
    __generic<T : __BuiltinFloatingPointType> T dot(vector<T, SIZE> a, T b) { return dot(a, vector<T, SIZE>(b)); } \
    __generic<T : __BuiltinFloatingPointType> T dot(T a, vector<T, SIZE> b) { return dot(vector<T, SIZE>(a), b); }

#define XR_SCALAR_ARGUMENT_FUNCTIONS(SIZE) \
    XR_SCALAR_ARGUMENT_FUNCTION_SIZE(min, SIZE) \
    XR_SCALAR_ARGUMENT_FUNCTION_SIZE(max, SIZE) \
    XR_SCALAR_ARGUMENT_FUNCTION_SIZE(pow, SIZE) \
    XR_SCALAR_ARGUMENT_FUNCTION_SIZE(step, SIZE) \
    XR_SCALAR_ARGUMENT_FUNCTION_SIZE(atan2, SIZE) \
    XR_SCALAR_ARGUMENT_FUNCTION_SIZE(fmod, SIZE) \
    XR_SCALAR_ARGUMENT_DOT(SIZE)

XR_SCALAR_ARGUMENT_FUNCTIONS(2)
XR_SCALAR_ARGUMENT_FUNCTIONS(3)
XR_SCALAR_ARGUMENT_FUNCTIONS(4)

// Matrix and vector products where the vector is smaller than the matrix. FXC uses the leading
// rows or columns of the matrix. The result has the full matrix size, so that code can truncate it further.
float4 mul(float4x4 m, float3 v) { return float4(dot(m[0].xyz, v), dot(m[1].xyz, v), dot(m[2].xyz, v), dot(m[3].xyz, v)); }
float4 mul(float4x4 m, float2 v) { return float4(dot(m[0].xy, v), dot(m[1].xy, v), dot(m[2].xy, v), dot(m[3].xy, v)); }
float3 mul(float3x3 m, float2 v) { return float3(dot(m[0].xy, v), dot(m[1].xy, v), dot(m[2].xy, v)); }
float4 mul(float3 v, float4x4 m) { return v.x * m[0] + v.y * m[1] + v.z * m[2]; }
float4 mul(float2 v, float4x4 m) { return v.x * m[0] + v.y * m[1]; }
float3 mul(float2 v, float3x3 m) { return v.x * m[0] + v.y * m[1]; }

// Matrix and vector products where the vector is larger than the matrix. FXC uses the leading vector components.
float3 mul(float3x3 m, float4 v) { return mul(m, v.xyz); }
float3 mul(float4 v, float3x3 m) { return mul(v.xyz, m); }

// FXC builds a matrix from any mix of scalars and vectors with the correct component count, for example
// float4x4(red, 0, green, 0, blue, 0, 0, 0, 0, 1) with float3 rows. Slang accepts only all scalars or one
// vector for each row. MatrixConstructorRewriter changes such a constructor to a chain of add calls on this
// type, which collects the components in row order. Slang selects the add overload from the argument type.
struct XrMatrixComponents
{
    float values[16];
    int count;

    XrMatrixComponents add(float value)
    {
        XrMatrixComponents result = this;
        result.values[result.count] = value;
        result.count = result.count + 1;
        return result;
    }

    XrMatrixComponents add(float2 value) { return add(value.x).add(value.y); }
    XrMatrixComponents add(float3 value) { return add(value.x).add(value.y).add(value.z); }
    XrMatrixComponents add(float4 value) { return add(value.x).add(value.y).add(value.z).add(value.w); }

#define XR_MATRIX_COMPONENTS_TO(R, C) \
    float##R##x##C toFloat##R##x##C() \
    { \
        float##R##x##C result; \
        for (int row = 0; row < R; row++) \
            for (int column = 0; column < C; column++) \
                result[row][column] = values[row * C + column]; \
        return result; \
    }

    XR_MATRIX_COMPONENTS_TO(2, 2)
    XR_MATRIX_COMPONENTS_TO(2, 3)
    XR_MATRIX_COMPONENTS_TO(2, 4)
    XR_MATRIX_COMPONENTS_TO(3, 2)
    XR_MATRIX_COMPONENTS_TO(3, 3)
    XR_MATRIX_COMPONENTS_TO(3, 4)
    XR_MATRIX_COMPONENTS_TO(4, 2)
    XR_MATRIX_COMPONENTS_TO(4, 3)
    XR_MATRIX_COMPONENTS_TO(4, 4)
#undef XR_MATRIX_COMPONENTS_TO
};

XrMatrixComponents XrMatrixComponentsStart()
{
    XrMatrixComponents result;
    for (int index = 0; index < 16; index++)
        result.values[index] = 0.0;
    result.count = 0;
    return result;
}

#endif
