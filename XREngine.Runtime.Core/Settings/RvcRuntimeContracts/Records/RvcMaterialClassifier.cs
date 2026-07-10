namespace XREngine;

public static class RvcMaterialClassifier
{
    public static ERvcMaterialClass Classify(
        bool transparent,
        bool refractiveOrOrderDependent,
        bool expensiveAlphaTest,
        bool generatedMaterialTableOpaque,
        bool unlit,
        bool pbr)
    {
        if (transparent || refractiveOrOrderDependent)
            return ERvcMaterialClass.TransparentForwardPlusFallback;
        if (expensiveAlphaTest)
            return ERvcMaterialClass.TransparentForwardPlusFallback;
        if (generatedMaterialTableOpaque)
            return ERvcMaterialClass.GeneratedMaterialTableOpaque;
        if (pbr)
            return ERvcMaterialClass.OpaquePbr;
        if (unlit)
            return ERvcMaterialClass.UnlitOpaque;

        return ERvcMaterialClass.Unsupported;
    }
}
