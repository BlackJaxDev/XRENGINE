#ifndef XR_DDGI_ATTRIBUTE_ADDRESS_INCLUDED
#define XR_DDGI_ATTRIBUTE_ADDRESS_INCLUDED
// Validate before multiplying so malformed strides or indices cannot wrap into
// an otherwise valid SSBO address. The host rejects these layouts before dispatch.
bool DDGIAttributeAddress(uint vertex, uint stride, uint offset, uint width, uint count, out uint word)
{
    word = 0u;
    if (stride == 0u || offset > count || width > count - offset ||
        vertex > (count - offset - width) / stride)
        return false;
    word = vertex * stride + offset;
    return true;
}
#endif
