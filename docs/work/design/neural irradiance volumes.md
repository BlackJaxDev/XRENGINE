# Real-time Rendering with a Neural Irradiance Volume

## Executive summary

This report analyzes the 2026 entity["organization","arXiv","preprint repository"] paper *“Real-time Rendering with a Neural Irradiance Volume”* (accepted at entity["organization","Eurographics","european graphics assn"] 2026) by entity["people","Arno Coomans","computer graphics researcher"], entity["people","Giacomo Nazzaro","computer graphics researcher"], entity["people","Edoardo A. Dominici","computer graphics researcher"], entity["people","Christian Döring","computer graphics researcher"], entity["people","Floor Verhoeven","computer graphics researcher"], entity["people","Konstantinos Vardis","computer graphics researcher"], and entity["people","Markus Steinberger","computer graphics researcher"] (affiliations include entity["company","Huawei Technologies","telecom company"] and entity["organization","Graz University of Technology","university graz, at"]). citeturn18view0turn51search0

The paper’s central idea is to replace a traditional probe-grid “irradiance volume” with a *neural* representation that directly regresses **indirect diffuse irradiance** as a continuous 5D function of **3D position + 2D direction/normal** (their “Neural Irradiance Volume”, NIV). This is trained offline from path-traced irradiance samples and then evaluated in one batched inference pass per frame from a rasterization G-buffer (position & normal buffers), enabling real-time diffuse global illumination (GI) for **novel dynamic objects inserted after training**, without runtime ray tracing or denoising. citeturn18view0turn22view0turn24view0

Key results claimed by the authors are (i) **~1 ms/frame** inference at 1920×1080 on consumer GPUs, (ii) **1–5 MB** model memory for medium scenes, and (iii) **≥10× quality improvement** at the same memory budget compared to probe-grid baselines, with better handling of aliasing/leaks and higher-frequency diffuse effects such as contact shadows (via a training strategy that emphasizes surface samples). citeturn18view0turn24view0turn22view0

Relative to other neural GI caches (especially surface-based approaches requiring deferred ray queries + denoising), NIV’s distinguishing trade is: it learns a **pre-integrated** quantity (irradiance) that can be rendered **noise-free** for diffuse materials at runtime, at the cost of (a) assuming the scene is “mostly static” during deployment and (b) not natively modeling higher-order light transport interactions induced by newly inserted geometry. citeturn22view0turn25view0turn26view0

Important caveat for this report: the official PDF text is not directly accessible in the current tooling environment, so analysis is based on the official arXiv HTML rendering and cited primary references. Some numeric hyperparameters (notably the exact learning rate, batch size, and one hash-grid scaling factor) appear elided in the HTML extraction and are therefore treated as **unspecified** here (flagged explicitly in the reproducibility section). citeturn22view0turn24view0turn27view0

## Problem framing and contributions

### Why irradiance volumes exist and why they break at scale

Real-time applications often want plausible GI for *dynamic objects moving inside largely static environments* (e.g., characters in a baked level). A common solution is to precompute (“bake”) indirect lighting into a **3D grid of probes**, then interpolate at runtime to shade both static surfaces and new dynamic geometry. The paper emphasizes two recurrent probe-volume failure modes: (1) **cubic scaling** of memory/computation with grid resolution, and (2) **interpolation artifacts** (leaks, missed contact shadows, aliasing) plus scene-specific heuristics for placement/rejection. citeturn21view0turn24view0turn26view0

Probe volumes are conceptually rooted in the classic “irradiance volume” literature (a volumetric representation of irradiance for efficient shading), and the paper positions NIV as a “modernization” of that idea by swapping the discrete grid+basis-function representation for a continuous neural function. citeturn21view0turn28search1

### Goals, assumptions, and what “working” means here

The explicit goal is to render **diffuse** global illumination on *unseen dynamic objects* in a known static environment under strict real-time budgets, with only a **G-buffer** at runtime (no expensive per-frame ray tracing). citeturn18view0turn24view0

The paper inherits key assumptions typical in production irradiance-volume pipelines: the scene is *mostly static*, and dynamic additions are relatively small so their effect on overall indirect light propagation is limited (or artist-controlled). It also assumes **direct illumination** can be computed efficiently enough at runtime without introducing visible sampling noise, because NIV adds indirect diffuse to an otherwise conventional direct-lighting pipeline. citeturn21view0turn26view0

### Stated contributions and headline claims

The authors list four main contributions: (a) a heuristic-free precomputation scheme that “bakes” path-tracing-quality diffuse irradiance and can shade unseen moving objects; (b) much lower rendering cost than neural methods requiring ray tracing/denoising; (c) a compact representation improving the memory–error trade-off by roughly an order of magnitude vs probe grids; and (d) extension to a small number of scene variables (e.g., time-of-day) by conditioning the irradiance field on additional inputs. citeturn21view0turn26view0turn25view0

## Technical method and derivations

### From the rendering equation to the cached quantity

The general rendering equation expresses outgoing radiance as emitted plus reflected incident radiance integrated over directions (and recursively over paths). In its classic form: citeturn28search0turn28search4

\[
L_o(\mathbf{x}, \omega_o)
=
L_e(\mathbf{x}, \omega_o)
+
\int_{\Omega} f_r(\mathbf{x}, \omega_i, \omega_o)\, L_i(\mathbf{x}, \omega_i)\, (\mathbf{n}\cdot \omega_i)\, d\omega_i .
\]

The paper focuses on **diffuse illumination** (Lambertian BRDF), allowing the BRDF to factor out as a constant proportional to albedo, reducing the view-dependent complexity. They define an *irradiance function* in terms of **position** and an oriented **direction/normal parameter** over the sphere (their domain description treats this as a continuous spherical function). citeturn21view0turn22view0

A standard diffuse irradiance definition (consistent with the paper’s described simplification) is:

\[
E(\mathbf{x}, \mathbf{n})
=
\int_{\Omega^+(\mathbf{n})} L_i(\mathbf{x}, \omega_i)\, (\mathbf{n}\cdot \omega_i)\, d\omega_i,
\]

where \(E\) is irradiance (power per unit area), \(\mathbf{x}\in\mathbb{R}^3\) is position, \(\mathbf{n}\in\mathbb{S}^2\) is the surface normal (or more generally an oriented direction parameter), \(\Omega^+(\mathbf{n})\) is the hemisphere around \(\mathbf{n}\), and \(L_i\) is incoming radiance. This relationship is also deeply tied to the spherical-harmonic low-frequency properties exploited in classic irradiance approximations. citeturn35search0turn35search8turn21view0

For Lambertian reflectance, outgoing reflected radiance simplifies to:

\[
L_o^{\text{diff}}(\mathbf{x})
=
\frac{\rho(\mathbf{x})}{\pi}\, E(\mathbf{x}, \mathbf{n}),
\]

with \(\rho\) the diffuse albedo. The paper further distinguishes **indirect irradiance** (their cached target) from direct illumination, which is added at runtime. citeturn21view0turn22view0

### Neural Irradiance Volume as a learned 5D field

Traditional probe volumes typically map each spatial probe to a low-dimensional basis expansion over direction (commonly spherical harmonics), i.e. a representation of \(E(\mathbf{x},\cdot)\) and an evaluation step for a given \(\mathbf{n}\). NIV instead learns a direct mapping from the **5D input** to irradiance:

\[
\hat{E}_\theta(\mathbf{x}, \mathbf{n}) \approx E(\mathbf{x}, \mathbf{n}),
\]

where \(\theta\) are parameters of a compact neural model. The paper highlights this “direct 5D regression” as enabling (i) avoiding explicit basis choice (e.g., SH order selection) and (ii) enabling extensions such as higher-dimensional conditioning. citeturn22view0turn25view0

### Architecture: compact MLP plus coordinate encodings

The core network is a **4-layer fully connected MLP** with ReLU activations (except the last layer), inspired by compact real-time neural cache architectures in prior work. citeturn22view0turn29search0

To handle high-frequency variation, the inputs are encoded:

- For smaller models, they apply **frequency/positional encoding** to the input positions (motivated by Fourier-feature analyses of spectral bias and by NeRF-style positional encodings). citeturn22view0turn30search13turn30search4  
- For larger scenes at fixed MLP cost, they replace handcrafted encodings with a **multi-level hash-grid encoding** (Instant-NGP style), mapping 3D positions to multiresolution feature vectors stored in hash tables. citeturn22view0turn28search3turn28search11

The paper explicitly discusses **hash collisions** as a feature: many positions map to shared latent entries, and optimization “allocates capacity” where gradients dominate. Too many collisions degrade quality; they therefore treat hash table size as the main knob to tune the collision rate for irradiance fields. citeturn22view0turn28search3

Some encoding hyperparameters are specified in the paper text: latent dimension is 4, the coarsest grid side length is 16, and the number of levels varies from 2–8 depending on the target capacity; the scaling factor between levels is mentioned but its numeric value is not visible in the accessible HTML extraction. citeturn22view0turn27view0

### Unified volume and surface cache via a sampling trick

A major design move is that NIV aims to replace *both* (a) volumetric probes for dynamic objects and (b) separate surface caches (e.g., lightmaps) for static surfaces.

Instead of introducing a separate 2D structure, the paper “carves out” a 2D manifold inside the 5D domain: it samples a fraction of training points **on surfaces**, and sets the directional input deterministically to the **surface normal**. This makes static-surface shading a special low-dimensional subset of the learned field, allowing the model to spend capacity capturing contact shadows and high-frequency diffuse detail on surfaces. The authors report using **20%** of training samples on surfaces as a good balance. citeturn22view0turn24view0

### Why learn irradiance (pre-integrated) rather than incoming radiance

The paper argues that learning **irradiance** (already integrated over incident directions) provides two advantages:

1. **No runtime sampling variance:** if you learn incoming radiance \(L_i(\mathbf{x},\omega)\), you must still numerically integrate it for diffuse shading (multiple samples per shading point), or denoise, both of which raise runtime cost. citeturn22view0turn25view0  
2. **Easier function approximation:** irradiance is directionally smoother than full directional radiance, requiring less capacity and converging faster (they include an appendix experiment showing slower training and higher runtime when learning incoming radiance at the same budget). citeturn27view0turn22view0

### Training distribution, loss, and data curation

**Training samples.** The model is trained on position–direction pairs throughout the scene volume, with 20% sampled on surfaces with direction = normal. For each pair, they compute ground-truth **indirect irradiance** using path tracing. citeturn24view0turn27view0

**Discarding samples inside geometry.** They discard points “inside surfaces,” detected by checking whether the majority of normals at first hit are backfacing while estimating irradiance. They also discard samples collecting “null irradiance” in the visualization appendix, which is consistent with avoiding wasted capacity on invisible or irrelevant volume regions. citeturn24view0turn27view0

**Loss.** They use a “relative L2” loss that normalizes mean squared error by the squared network prediction, with a stop-gradient operator and a small constant for stability (inspired by the relative-error normalization commonly used in Noise2Noise-style reasoning). Because the HTML view elides the exact equation body, the following is a faithful reconstruction of the described structure, not necessarily the authors’ exact per-channel/per-sample reduction:

\[
\mathcal{L}(\theta)
=
\frac{\|\hat{E}_\theta - E^\star\|_2^2}
{\operatorname{sg}(\|\hat{E}_\theta\|_2^2)+\epsilon},
\]

where \(E^\star\) is the path-traced target irradiance, \(\operatorname{sg}(\cdot)\) is stop-gradient, and \(\epsilon\) is a small constant. citeturn24view0turn29search1

**Optimizer and compute profile.** They train in PyTorch with Adam, and use a renderer (Mitsuba3) to generate ground truth; scenes converge in ≤50k iterations. They report that training time is dominated by path tracing: for the Cornell Box, ~94% of compute is irradiance path tracing rather than network optimization. citeturn24view0turn30search3turn34search0

Numeric optimizer hyperparameters in the extracted HTML (learning rate, batch size, the final decayed learning rate) are not visible here and should be obtained from the official PDF/source when reproducing. citeturn24view0turn27view0

### Runtime rendering pipeline

At runtime, NIV plugs into a conventional deferred renderer:

1. Render primary visibility with rasterization → produce G-buffer (position \(\mathbf{x}\), normal \(\mathbf{n}\), albedo \(\rho\), etc.).  
2. Batch-evaluate the network for all pixels: \(\hat{E}_\theta(\mathbf{x},\mathbf{n})\).  
3. Convert irradiance to indirect diffuse radiance via the diffuse factor \(\rho/\pi\), add direct illumination and emission. citeturn24view0turn22view0

They also support **half-resolution** inference (quarter pixels) and note that irradiance is smooth in screen-space so aliasing is relatively mild; they mention joint-bilateral upsampling as an industry-style option for reconstruction. citeturn24view0turn34search2

For missing interactions (dynamic objects do not affect the baked field), they add a lightweight **dynamic ambient occlusion** pass computed only over dynamic geometry to approximate self-occlusion and occlusion cast onto static surfaces by newly added objects. citeturn24view0turn34search3

```mermaid
flowchart LR
  subgraph Offline_Precompute[Offline precompute]
    S[Static scene + lights] --> D[Sample (x, n) pairs in volume]
    D --> PT[Path trace indirect irradiance E*(x,n)]
    PT --> T[Train neural field E_hatθ(x,n)]
  end

  subgraph Runtime[Runtime frame]
    G[Rasterize -> G-buffer (x,n,albedo,...)] --> Q[Batch query E_hatθ(x,n)]
    Q --> I[Indirect diffuse = (albedo/pi) * E_hatθ]
    I --> C[Compose with direct lighting + emission]
    C --> F[Final frame]
    G --> AO[Optional dynamic AO from dynamic geometry]
    AO --> F
  end
```

citeturn24view0turn22view0turn21view0

## Intuition and toy scenarios

### Toy model: “grid probes” vs “continuous function”

Consider a 1D hallway parameterized by position \(x\in[0,1]\), and suppose indirect diffuse irradiance \(E(x)\) changes rapidly near doorways (sharp occlusion transitions) and smoothly elsewhere.

- A probe grid is like sampling \(E\) at fixed points \(x_k\) and interpolating. To capture sharp changes you must increase probe density everywhere → memory scales with resolution (in 3D, cubically). citeturn24view0turn21view0  
- NIV is instead a “compressed function” \(\hat{E}_\theta(x)\) trained to minimize reconstruction error. With a multiresolution encoding, the model can devote more representational precision to regions where the loss is high (near sharp transitions) and less elsewhere, avoiding uniform cubic scaling. citeturn22view0turn28search3

This is the paper’s “neural compression is adaptive + amortized” argument in a simplified setting. citeturn18view0turn22view0

### Why surface sampling helps (contact shadows intuition)

If you only sample uniformly in volume, many samples lie in empty air where irradiance changes slowly. But the visually important error is often on surfaces near contact points (e.g., an object near the floor), where irradiance can change over very small spatial distances. By forcing 20% of samples onto surfaces with direction fixed to the surface normal, the model is explicitly trained on the “shading manifold” your renderer will query at runtime, improving surface fidelity without requiring a separate 2D cache. citeturn22view0turn24view0

### Hash collisions as “shared cells” (why they don’t always hurt)

In a hash-grid encoder, two positions \(\mathbf{x}_a\) and \(\mathbf{x}_b\) might map to the same latent feature due to hashing. Superficially, this seems like a bug. The Instant-NGP viewpoint is that (a) multiresolution levels help disambiguate collisions, and (b) optimization implicitly prioritizes important regions: gradients from high-loss samples dominate. NIV applies the same idea to irradiance fields and empirically observes that *high collision rates mildly affect irradiance MSE while greatly reducing memory*. citeturn22view0turn28search3

## Empirical results and comparison to alternatives

### Runtime and memory characteristics of NIV

The paper reports that a 4-layer network can run at full HD in the **0.19–1.35 ms** range on an RTX 4090 depending on width and hash-grid levels, with corresponding memory from **0.003 MB** up to **~5.4 MB** in their table. citeturn24view0turn27view0

They also test an older GPU (RTX 2080 Ti) showing roughly ~4× slower inference, but remaining within a real-time regime for smaller configurations, and attribute performance primarily to matrix-multiplication throughput. citeturn27view0

### Scene-level illustration images

image_group{"layout":"carousel","aspect_ratio":"16:9","query":["Cornell Box global illumination rendering","Sponza atrium global illumination scene"],"num_per_query":1}

### Comparison table of the main approach families

| Method family | Representation | Runtime mechanism for dynamic objects | Typical artifacts / failure modes | NIV’s relative position |
|---|---|---|---|---|
| Probe-grid irradiance volumes (e.g., DDGI-style) | Regular 3D probe lattice + low-order basis over direction (often SH) | Interpolate probes in volume at shading points | Light leaking, poor contact shadows, cubic memory scaling; heuristics for placement/rejection | NIV aims to remove interpolation/placement heuristics and improve memory–quality scaling citeturn24view0turn22view0 |
| Neural surface cache (outgoing radiance on surfaces) | Neural field on known surfaces | Requires deferred ray queries to trained surfaces; variance must be denoised | Monte Carlo variance, denoiser blotchiness; runtime cost from ray tracing + denoising | NIV avoids runtime ray tracing/denoising by learning pre-integrated irradiance citeturn25view0turn22view0 |
| Variable-scene neural rendering (explicit scene parameters) | Neural field conditioned on many parameters | Can model higher-order interactions if trained on them | High inference cost; retraining when parameter set changes; performance scales with #variables/objects | NIV trades away higher-order interactions for speed and better scaling under fixed memory citeturn25view0turn26view0 |

The DDGI baseline details in the paper use a modern probe grid similar to the JCGT DDGI formulation, including visibility handling. The authors note that visibility via ray tracing reduces probe error but still cannot close the gap to NIV and adds significant runtime cost. citeturn24view0turn28search2turn28search22

### Probe-based baselines: why NIV wins at equal memory

For their probe baseline, the paper stores 2nd-order spherical harmonics (9 coefficients) at half precision (54 bytes per probe) and emphasizes the cubic scaling and inability to adapt capacity to where it matters most, leading to missed contact shadows and light-leak sensitivity (especially without expensive visibility). citeturn24view0turn35search8turn35search0

They report that across tested scenes, NIV improves quality by roughly an **order of magnitude** at a given memory budget, especially at lower capacities, and qualitatively captures irradiance bleed/shadows better. citeturn24view0turn18view0

The appendix comparison to an open-source industry implementation of DDGI (RTXGI/DDGI) highlights large memory differences in their setup (tens of MB for DDGI defaults vs sub-MB for NIV in that example), and mentions failure cases like probes placed inside dynamic geometry producing smudges. citeturn27view0turn35search3

### Neural surface cache: variance dominates quality, runtime dominates cost

The paper trains a neural surface cache similar to “real-time neural radiance caching” style models (augmented with a multiresolution hash encoding) and explains why dynamic objects require *deferred* lookups via ray tracing: the cache only knows radiance on static surfaces. This introduces sampling variance that then requires denoising (OptiX is referenced as an example denoiser). citeturn25view0turn29search0turn29search3

They report **~5–10 ms/frame** overhead from the ray tracing + denoising pieces (even with hardware RT and efficient denoising), and show that increasing cache capacity doesn’t help much when variance dominates; increasing spp helps but scales cost linearly. citeturn25view0

Quantitatively, their Table 2 shows lower FLIP and far lower MSE for NIV than the surface cache across several scenes at the same 5.40 MB capacity. citeturn25view0turn29search2

### Variable-scene methods: stronger physics if you can afford it, but not real-time here

The paper compares NIV to methods that explicitly train on variable scene parameters, describing two broad strategies: (i) feeding an explicit scene-parameter vector to a generator, or (ii) using learned encodings over those variables. They state major drawbacks in their experiments: inference exceeding **100 ms**, scaling with the number of dynamic objects/variables, and needing retraining when the set of parameters changes (objects added/removed). citeturn25view0turn39search8turn46view0

A key analytical point is their “lower bound” on irradiance-volume methods: since the irradiance field is trained without the dynamic objects, it cannot represent higher-order interactions induced by those objects (beyond what a dynamic AO hack may approximate). Variable-scene models can go below that bound given enough capacity/training because they can learn those interactions explicitly. citeturn25view0turn26view0turn39search8

## Practical implications and implementation considerations

### Where NIV fits in real renderers

NIV is designed for **deferred shading** pipelines: it consumes G-buffer position and normals, runs a single batched inference pass, and adds indirect diffuse as a cheap additive component. This makes it attractive as a drop-in replacement for probe volumes in engines that already do baked GI + dynamic objects, but want higher quality at lower memory and fewer heuristics. citeturn24view0turn18view0

Because the cached quantity is irradiance (pre-integrated), runtime shading is lightweight: a multiply by albedo/\(\pi\) plus compositing with direct/emissive. This is fundamentally cheaper than any approach that must integrate learned incident radiance at runtime or trace rays for deferred queries. citeturn22view0turn25view0

### Computational cost profile

**Runtime.** The paper’s timing table shows inference times around the 1 ms scale at full HD on a high-end GPU for their larger models; smaller configurations are sub-millisecond. Half-resolution evaluation is substantially cheaper and can be paired with full-resolution albedo for materials. citeturn24view0turn27view0

**Offline.** Training time is dominated by generating irradiance targets via path tracing. Reported convergence times include minutes for simple scenes and tens of minutes for larger ones on a single high-end GPU, with ~94% of compute spent in path tracing in one example. citeturn24view0

### Data requirements

NIV needs a scene where you can path trace indirect irradiance at many sampled points. The training set is conceptually a collection of \((\mathbf{x},\mathbf{n}) \mapsto E^\star\) tuples, plus a surface-sample subset. This is closer to “baking” than to dataset-based learning: you regenerate targets if the static environment’s geometry/materials/lights change. citeturn24view0turn21view0

### Hyperparameters that matter most in practice

From the paper’s discussion, the key knobs are:

- **Model capacity:** MLP width and hash-grid levels; their table enumerates widths 16/32/64 and hash levels 2–8 with memory/latency trade-offs. citeturn24view0turn27view0  
- **Hash table size / collision rate:** treated as the main parameter governing memory–quality trade-off for the hash encoding. citeturn22view0turn28search3  
- **Training sampling mix:** the 20% surface sampling and inside-geometry culling are critical to robust results and high-frequency surface detail. citeturn24view0turn27view0  
- **Dynamic AO strength/settings:** not fully specified, but it is the intended mechanism to mitigate missing occlusion interactions by dynamic objects. citeturn24view0

Several other values are referenced but not fully visible in the extracted HTML (learning rate, batch size, encoding scaling factor), so an implementation should consult the official PDF/source or perform a small hyperparameter search. citeturn24view0turn22view0

### Extending to limited “dynamic lighting” or other small parameter sets

The paper demonstrates conditioning NIV on 1–2 additional variables (e.g., directional light angle for time-of-day), trained by sampling those variables during precomputation, so runtime does not require updates. They caution that scaling to many variables degrades quality unless one uses heavier learned encodings (which reintroduces the inference-cost issues seen in variable-scene methods). citeturn25view0turn26view0

### Limitations and open problems highlighted by the authors

The limitations section is unusually concrete about where the approach breaks:

- **Direct illumination must be cheap + noise-free**; with many lights or sampling-based direct lighting, noise-free shading may be impossible without additional techniques. citeturn26view0turn34search1  
- **Irradiance-volume assumption**: dynamic objects do not contribute to scene GI; extending to glossy materials or higher-order occlusion would require additional modeling (they point to PRT and glossy extensions as related directions). citeturn26view0turn35search1  
- **Glossy materials**: possible via secondary rays to diffuse intersections, but then you reintroduce sampling variance; they suggest conditioning on roughness (citing Ref-NeRF-style ideas) as a possible direction. citeturn26view0turn37search0  
- **Online learning** is possible in principle but slower due to the larger volumetric domain; they suggest frustum-only sampling and loss-driven sampling as potential tactics. citeturn26view0turn39search8  
- **Scaling to very large scenes** eventually becomes capacity-limited; they propose spatial partitioning (KiloNeRF-like) or LOD strategies, and cite neural BVH / related neural subdivision ideas as a broader direction. citeturn26view0turn36search0turn38search0

## Reproducibility checklist and suggested experiments

### What you need to reproduce the main claims

**Assets and renderer setup**
- A static 3D scene with materials and lights, plus a bounding volume for sampling. citeturn24view0turn21view0  
- A path tracer to compute indirect irradiance targets \(E^\star(\mathbf{x},\mathbf{n})\) (the paper reports using Mitsuba3 + PyTorch). citeturn24view0turn31search5turn30search3  
- A deferred renderer that outputs G-buffer position/normal/albedo for runtime inference. citeturn24view0  

**Model and training**
- 4-layer MLP (ReLU), with either positional encoding or multires hash-grid encoding for positions. citeturn22view0turn28search3turn30search13  
- Training data: uniform volume samples plus ~20% surface samples with direction = normal; cull samples inside geometry/backfacing. citeturn24view0turn27view0  
- Loss: relative-L2 style normalization (stop-gradient denominator + constant). citeturn24view0turn29search1  
- Optimizer: Adam; convergence in ≤50k iterations reported. citeturn24view0turn34search0  

**Runtime**
- Batched inference over full-HD G-buffer (optionally half-res), compose with direct lighting and optional dynamic AO. citeturn24view0turn34search3  

**Unspecified / ambiguous in accessible text (must verify from PDF/source)**
- Exact learning rate, batch size, LR decay endpoint. citeturn24view0turn27view0  
- One hash-grid scaling factor value between levels (mentioned but elided in the HTML extraction). citeturn22view0  

### Experiments that most directly validate (or falsify) the paper’s claims

**Memory–error scaling**
- Reproduce the paper’s “order-of-magnitude better quality at fixed memory” by sweeping hash table sizes / number of levels, then comparing volumetric MSE on random \((\mathbf{x},\mathbf{n})\) samples against a probe grid with matched memory. citeturn24view0turn22view0  

**Ablations of the two critical training heuristics**
- Train with and without (i) surface sampling and (ii) inside-geometry culling; measure surface error (contact shadows) and volumetric error separately, matching their qualitative claim that combining both yields the most robust results. citeturn24view0turn22view0  

**Runtime comparison against a “best-case” surface cache**
- Implement a surface neural cache (NRC-style) with comparable capacity, then compare: (a) quality vs spp, (b) need for denoising, and (c) total frame time including ray tracing + denoiser. The paper claims the variance–cost trade dominates and NIV is 5–10 ms faster at similar quality. citeturn25view0turn29search0turn29search3  

**Generalization tests**
- Insert novel dynamic objects with varying sizes/coverage ratios (beyond the reported 5–10% coverage), and test when the irradiance-volume “small impact” assumption starts to cause unacceptable bias. citeturn25view0turn26view0  

**Higher-dimensional conditioning**
- Add 1–2 conditioning variables (e.g., directional light angle, moving occluder) and verify that (i) adding frequency encoding for those variables preserves near-constant inference time and (ii) increasing the number of variables produces visible reconstruction error at fixed capacity, as claimed. citeturn26view0turn25view0  

**Large scene scaling**
- Partition a large environment into tiles and train multiple NIVs (KiloNeRF-style spatial decomposition) and quantify the trade between seam artifacts, memory, and runtime overhead. citeturn26view0turn36search0  

## Key references and reading map

The paper is best understood as sitting at the intersection of (i) classical irradiance/probe caching and (ii) neural field compression for fast inference. The following primary sources are the most load-bearing for understanding the method and its comparisons:

- The rendering equation foundation (for the “what are we approximating” baseline). citeturn28search0  
- Original irradiance volume concept (historical probe-volume framing). citeturn28search1turn28search17  
- DDGI / irradiance fields with visibility (strong modern probe baseline). citeturn28search2turn28search22  
- Low-order spherical harmonics for irradiance (why 9-coefficient SH is a common diffuse probe representation). citeturn35search0turn35search8  
- Precomputed radiance transfer (extensions and the “higher-order effects need more structure” story). citeturn35search1turn35search5  
- Real-time neural radiance caching (surface-based neural cache, variance/denoising trade). citeturn29search0turn29search4  
- Instant-NGP hash-grid encoding (the core compression/enabling trick used by NIV for large scenes). citeturn28search3turn28search11  
- Noise2Noise (reference point for relative / normalized losses and noise reasoning used as inspiration). citeturn29search1turn29search5  
- Joint bilateral upsampling (runtime upsampling option for half-resolution inference). citeturn34search2turn34search10  
- Hardware ambient occlusion approximations (the kind of AO pass NIV uses for dynamic geometry interactions). citeturn34search3turn34search23  

These references together explain (a) why probe volumes exist, (b) why their scaling/aliasing problems are structural, and (c) why NIV’s specific combination—*pre-integrated irradiance + hash-grid encoding + batched MLP inference*—lands in a favorable place on the quality/memory/runtime frontier for diffuse GI under real-time constraints. citeturn24view0turn22view0turn18view0