// Synthetic classifier fixture only. This is not a shader implementation.
Shader "Hidden/Locked/Poiyomi Pro Grab Pass Synthetic"
{
    // Poiyomi 9.3.11
    // OPTIMIZER_ENABLED
    // The locked shader keeps its scene grab, so the importer reports the discarded grab feature.
    GrabPass { "_GrabTexture" }
}

