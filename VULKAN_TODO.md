# Vulkan backend TODO

Adding a Vulkan renderer **alongside** the GL 4.6 one. **GL is never deleted** — both backends stay
switchable per launch (`-gl` default / `-vulkan`); that is a project decision, not a transition state.

| Piece | Size | Fate |
|---|---|---|
| `Source.ShaderAPI.Gl46` | 11 files, 5227 lines | stays; sibling `Source.ShaderAPI.Vulkan` |
| `Source.StdShader.Gl46` | 14 files, 2482 lines | shader classes shared via interfaces |
| `Game.Assets/hl2/shaders/*_gl460.{vs,fs,glsl}` | 17 files | stay; `*_vk13` ports compiled to SPIR-V |
| `Dependencies/OpenGL` | 15156 lines | keep permanently |
| `Source.MaterialSystem` | 21 files, 10621 lines | **should not need changes** if the seams hold |

The contract a Vulkan backend must satisfy is `Source.Common/ShaderAPI/`: `IShaderAPI` (~100 members),
`IShaderShadow` (327 lines), `IShaderDevice`, `IShaderUtil`.

---

## Phase 0 — decide first

- [x] Pick a binding. **Decision: Silk.NET.Vulkan** (actively maintained, same ecosystem as the SDL3 usage).
- [x] Keep GL alive alongside Vulkan rather than deleting it. `GraphicsDriver` is already a flags enum, so both can coexist and be A/B'd. Deleting GL first means debugging a black screen with no reference. **Decision: GL stays; `-vulkan` opts in.**
- [x] Shader toolchain: **glslc** (Vulkan SDK 1.4.357 installed 2026-08-21). `Game.Assets.csproj`'s
      `CompileVulkanShaders` target compiles `hl2/shaders/*_vk13.{vs,fs}` to `.spv` next to the sources
      at build time (warns+skips without the SDK; `.spv` is gitignored). The launcher's asset copy now
      runs `AfterTargets=ResolveProjectReferences` so generated `.spv` get picked up.
      (Correction to an earlier note: shader sources live in `Game.Assets/hl2/shaders/` directly.)

## Phase 1 — widen the seams (all in existing code, no Vulkan yet) — DONE

Verified against code 2026-08-21; GL still builds and remains the default.

- [x] ~~add `Vulkan` to `GraphicsDriver`~~ — already existed (`1 << 60`). Added `Vulkan13 = Vulkan | 13` (targets Vulkan 1.3: dynamic rendering, sync2).
- [x] `IShaderDevice.cs` — `Extension()` now returns `vk13.{vs,fs,gs}.spv` for Vulkan.
- [x] `SDL3_LauncherManager.CreateGameWindow` — hardcode removed; reads `materials.GetCurrentConfigForVideoCard().Driver`. (The `SDL_WINDOW_VULKAN` switch case already existed.)
- [x] `MaterialSystem_Config.Driver` — still defaults to `OpenGL46`, but `MaterialSystem`'s ctor overrides it from the command line (`-vulkan` / `-gl`) before the shader system spins up. `SysModes.cs:115` already fed `PrepareContext` from this config, so it is the single source of truth.
- [x] `ImageLoader.cs` — GL constants/tables moved to `Source.ShaderAPI.Gl46/ImageFormatGl46.cs`. No new neutral enum needed: `ImageFormat` *is* the neutral enum; each backend owns its translation table (grep confirms only ShaderAPIGl46 consumed them).
- [x] `OptionsSubVideo.cs` — direct `glGetStringSafe(GL_VERSION)` replaced with `IShaderDevice.GetDriverVersionString()` (new interface member). Game.UI no longer touches OpenGL. (Labels still say "OpenGL level:" — cosmetic, fix when Vulkan works.)
- [x] `PrepareContext` — Vulkan returns true (no pre-window GL attributes needed).
- [ ] Sibling Vulkan `IGraphicsContext` — deferred to Phase 2 (needs the backend to exist). `IGraphicsContext` fits: `MakeCurrent` becomes a no-op, `SwapBuffers` means "present", `SetSwapInterval` selects present mode at swapchain creation. `CreateContext` still warns+null for Vulkan until then.

## Phase 2 — device bring-up

`Source.ShaderAPI.Vulkan` project exists (Silk.NET.Vulkan 2.23). Code written, compiles, **not yet
wired into the launcher and not yet run** — runtime-verify each box before ticking the goal.

- [x] Instance + validation layers — `VulkanCore.cs`; `vk_validation` convar (default 1), debug-utils messenger routed to Warning().
- [x] Physical device selection, queue families — discrete > integrated scoring; requires graphics+present, swapchain ext, 1.3 dynamic rendering + sync2; prefers a single graphics+present family.
- [x] Surface via `SDL_Vulkan_CreateSurface` — done through new `IGraphicsProvider` hooks (`GetVulkanInstanceExtensions` / `CreateVulkanSurface` / `DestroyVulkanSurface` / `GetVulkanPresentationSupport`) implemented in `SDL3_LauncherManager`, so the backend never touches SDL directly.
- [x] Swapchain + recreation — `VulkanSwapchain.cs`; sRGB BGRA8 preferred, mailbox > immediate when vsync off, `OldSwapchain` chained on recreate.
- [x] Frames in flight (2), command pools/buffers, semaphores + fences — `VulkanFrameLoop.cs`, sync2 (`QueueSubmit2`/`CmdPipelineBarrier2`), dynamic rendering clear pass, out-of-date -> `NeedsRecreate`.
- [x] `VulkanGraphicsContext : IGraphicsContext` — MakeCurrent no-op, SwapBuffers no-op (present at submit), SetSwapInterval flags a swapchain recreate.
- [x] **Goal: clear the screen to a colour.** `ShaderAPIVulkan` shim implements `IShaderAPI`/`IShaderDevice`/`IDebugTextureInfo`
      (all draw/texture/state calls swallowed; `ShadowStateVulkan`, `ShaderSystemVulkan`, `HardwareConfigVulkan`,
      `DummyMeshVulkan` back the material system). `Program.cs` selects it on `-vulkan`. Verified 2026-08-21 on an
      RTX 3060 (Vulkan 1.4): boots to the running engine loop, presents the clear colour every frame, no exceptions.
      (Validation layers now installed and on by default — `vk_validation 1`.)

## Phase 3 — the actual hard part: state machine -> pipelines

`IShaderAPI.cs` defines `GraphicsBoardState`, documented as "a basic representation of the graphics
state machine" — mutable GL-style global state. Vulkan wants immutable `VkPipeline` objects, and this
mismatch is the bulk of the work.

Implemented 2026-08-21 (session 3), runtime-verified on the RTX 3060 with validation layers ON,
zero validation errors: `-vulkan` boots to the main menu with real geometry rendering. (Session 4
added textures, so the menu now renders fully — see Phase 4.)

**Confirming the backend really is Vulkan** (checked 2026-08-21 by diffing loaded modules): with
`-vulkan` the process maps `vulkan-1.dll` and `SteamOverlayVulkanLayer64.dll` (Steam's overlay only
injects into a live VkInstance) and never loads `OpenGL46.dll`; with `-gl` it is the exact inverse —
no `vulkan-1.dll` at all. The two also load different shader assets (`*_vk13.spv` vs `*_gl460`).
`nvoglv64.dll`/`opengl32.dll` appear in both because SDL3 probes GL when it initialises video,
regardless of which backend the engine then uses.

- [x] `ShadowStateVulkan` captures full `GraphicsBoardState` + `VertexSharedStateVulkan`
      (NumBones) + `PixelSharedStateVulkan` (alpha test) exactly like `ShadowStateGl46`, but with
      **no GPU objects** — it is pure data. `Activate()` = publish self as current shadow to the API +
      `BindVertexShader/BindPixelShader` + replay collected material-var uniforms (→ push-constant flags).
- [x] Pipeline key = (GraphicsBoardState, vs module, ps module, VertexFormat, topology, color format,
      depth format). Manual `IEquatable` struct (no memcmp — struct padding). Cache in a Dictionary +
      last-key fast path (`VulkanPipelines.cs`). Pipelines use dynamic viewport/scissor; rest baked.
- [x] Dynamic rendering (no render pass objects). Y-flip via **negative viewport height** (GL parity,
      front face stays CW+cull on).
- [x] **Matrix upload convention — the gotcha of the session:** GL *transposes* every matrix on its
      way into the UBO (`ShaderAPIGl46.LoadMatrix`), so Vulkan must too; uploading raw
      System.Numerics matrices produced diagonal garbage geometry. After the transpose, the GLSL
      math matrix M has `M[r][c] == Matrix4x4.M(r+1)(c+1)`, so the GL→VK depth remap z' = 0.5*(z+w)
      edits fields **M31..M34 += M41..M44** on the projection matrix only (see `FixupProjection`).
      Also GL parity: `LoadMatrix` does NOT flush buffered primitives.
- [x] Vertex input from `VertexFormat`, same offset order as `ComputeVertexDescription`
      (`VulkanVertexLayout.cs`). GL attribute locations (Position=0 … TexCoord0=10) are the shader
      `layout(location)` contract. Attributes a format lacks bind to a 64-byte **zero buffer at
      binding 1, stride 0** (legal in Vulkan; GL's disabled-attrib default differs only in alpha=1 —
      accepted; validation emits harmless "attribute not consumed" perf warnings).
- [x] Descriptor layout contract (matches common_vk13.glsl): set 0 = dynamic-offset UBOs at bindings
      0 (matrices, 192B) / 2 (vertex shared, 16B) / 3 (pixel shared, 16B) / 4 (bones, 256×64B) /
      5 (vs_const, 4KB) / 6 (ps_const, 4KB); set 1 = 6 combined image samplers (Sampler enum index =
      binding); push constants = int flags (vertex|fragment, 4B).
- [x] Per-frame-in-flight **uniform ring buffer** (host-visible, 16MB, warn+wrap on overflow): each
      dirty block is memcpy'd to a fresh ring slice at draw-prep; one set-0 descriptor set per frame
      points at that frame's ring, rebound with new dynamic offsets when any block moved. All blocks
      dirty at frame start.
- [x] `LocateShaderUniform`: strips `$`; `"flags"` → id 0 (push constant); anything else → -1 (GL
      sampler-unit assignments like `"lightmaptexture"→1` are meaningless here — bindings are fixed).
- [ ] Pre-warm the pipeline cache from materials at load to avoid mid-frame creation stutter.

## Phase 4 — resources

Buffers/depth done 2026-08-21 (session 3); textures are the next milestone.

- [x] Chunked `VkDeviceMemory` allocator (`VulkanMemory.cs`: 64MB chunks per memory type, first-fit
      free list, merge on free; one mapped pointer per host-visible chunk). No per-buffer
      `vkAllocateMemory`.
- [x] All buffers host-visible+coherent for bring-up; device-local + staging is a later
      optimization. Sysmem shadow copy stays (MeshBuilder writes there, Unlock memcpys).
- [x] Ported `VertexBufferGl46/IndexBufferGl46/MeshGl46/DynamicMeshGl46/BufferedMeshGl46/MeshMgr`
      1:1 (`VertexBufferVulkan.cs`, `IndexBufferVulkan.cs`, `MeshVulkan.cs`). GL orphaning becomes:
      push live VkBuffer onto a **retire queue** (freed FramesInFlight+1 frame-ticks later) and
      allocate fresh. `RenderPass()` binds VB/IB + `CmdDrawIndexed(prim.NumIndices, 1,
      prim.FirstIndex, 0, 0)` (indices are absolute — GL parity).
- [x] Depth buffer (D32Sfloat) owned by the frame loop, recreated with the swapchain; frame loop is
      Begin/End recording (lazy frame start on first draw/clear; Present ends+submits+presents;
      mid-frame `ClearBuffers` = `CmdClearAttachments`; a frame with no draws still clears).
- [x] 1×1 white placeholder texture + sampler, still the fallback for any set-1 slot a material
      does not bind.
- [x] **Real textures — done 2026-08-21 (session 4), runtime-verified: the menu renders its actual
      background and text, zero validation errors.** `VulkanTextures.cs` +
      `ImageFormatVulkan.cs`:
  - `VulkanTexture` = VkImage(s) + allocation + view + sampler desc; GL's multi-copy textures
    (`NumCopies`/`SwitchNeeded`) are an array of images with `CurrentCopy`.
  - **Views are created lazily at bind time covering only the mips actually uploaded** — a VTF
    supplying fewer levels than the image was created with would otherwise sample uninitialised
    memory at the small end. Re-created (old one retired for FramesInFlight+1) when a new top mip
    arrives.
  - Uploads go through a 32MB staging ring into a **dedicated upload command buffer** (copies are
    illegal inside the frame's dynamic rendering pass), flushed with a fence wait when the ring
    fills, before each frame, and before any draw if work is still pending mid-frame.
  - Samplers are separate objects in Vulkan, so `TexWrap`/`TexMinFilter`/`TexMagFilter` record a
    `SamplerDesc` on the texture and a cache resolves it at bind time. Anisotropy +
    `textureCompressionBC` are now enabled device features.
  - `ImageFormatVulkan` owns the format table *and* the CPU conversions: 3-byte RGB, packed 16-bit,
    and odd channel orders are not reliably sampleable in Vulkan, so they convert to RGBA8888;
    DXT1/3/5 -> BC1/2/3, ATI1N/2N -> BC4/BC5 upload untouched.
  - **Sampler-unit routing — the gotcha of this session:** GL's `Sampler` index is *per shader*
    (Sampler1 is the lightmap in LightmappedGeneric but basetexture2 in WorldVertexTransition),
    resolved by the shader setting a sampler uniform to a unit number. The vk13 shaders have fixed
    set-1 bindings instead, so the same `LocateShaderUniform`/`SetShaderUniform` calls are used in
    reverse: the name maps to its binding, and the value the shader passes says which unit feeds
    it. `samplerForBinding` is reset per snapshot so a previous material's routing cannot leak.
  - set-1 descriptor sets are cached on the six (view, sampler) pairs; pools grow on demand and
    `DeleteTexture` invalidates any cached set referencing a dying view.
- [x] **Swapchain is UNORM, not sRGB** (changed this session): the GL backend never enables
      `GL_FRAMEBUFFER_SRGB`, so an `_Srgb` swapchain gamma-corrected once more than `-gl` did.
      `CreateTextureFlags.SRGB` is ignored for the same reason. A/B against `-gl` confirms the
      menus now match.
- [ ] `TexLock` hands out a zeroed CPU buffer rather than reading the image back (no host-visible
      copy exists), so a caller that updates only part of its locked rect would clear the rest.
      Fine for the rects the engine actually locks; revisit if a partial-update case appears.
- [ ] `DeleteTexture` does a full `vkDeviceWaitIdle` before freeing. Correct but heavy — replace
      with the frame-tick retire queue the buffers already use.
- [ ] Then render targets (`SetRenderTargetEx` warns and draws to the backbuffer today): dynamic
      rendering to texture RTs + layout transitions + `DoRenderTargetsNeedSeparateDepthBuffer`.
      This is the next milestone, and the last big piece before world rendering.
- [x] Bones: GL only ever uploads bone 0 = transposed model matrix (`SetSkinningMatrices`) +
      `LoadBoneMatrix` per-bone transposed writes — mirrored into the bones CPU block, dirty-flagged.
- [ ] Color meshes (static prop lighting streams) warn-once and are ignored; stencil state is
      ignored (D32 depth has no stencil aspect) — revisit with world rendering.

## Phase 5 — shaders

- [x] GLSL -> SPIR-V: 12 `*_vk13` sources ported (unlitgeneric, lightmappedgeneric,
      worldvertextransition, shadow, shadowmodel, white, writez + `common_vk13.glsl`), all compile with
      glslc and load into `VkShaderModule`s at runtime (verified: unlitgeneric loads during boot with
      zero validation errors). Binding convention documented in `common_vk13.glsl`:
      set 0 = UBOs (same binding numbers as GL), set 1 = material textures, push constants = `flags`.
      glslc needs no separate preprocessor pass — its `#include` handles `common_vk13.glsl` natively.
- [ ] **`vertexlitgeneric` is not ported** — it is the only combo-based shader (STATIC/DYNAMIC combo
      comments used as #defines). Precompiling every combo is 1024x640 variants; rewrite it with
      uniform/push-constant branching (interface-affecting combos like VERTEXCOLOR/CUBEMAP can be
      always-declared and branched) or specialization constants for the scalar ones.
- [ ] Runtime loading lives in `ShaderSystemVulkan.LoadVertexShader/LoadPixelShader` (reads the `.spv`
      via IFileSystem, caches modules); `ShadowStateVulkan` records the handles as future pipeline key
      pieces. Combos/defines are ignored at load — revisit with the vertexlitgeneric rework.
- [ ] Shader classes in `Source.StdShader.Gl46` run unmodified against the Vulkan stubs (they only
      talk to interfaces); `BaseShader`/`BaseVSShader` binding logic still needs a Vulkan-aware pass
      once descriptor sets exist (Phase 4).

## Phase 6 — backend selection (GL is permanent, no cutover)

- [x] `Source.Launcher/Program.cs` selects `ShaderAPIVulkan` vs `ShaderAPIGl46` on `-vulkan`
      (matching `MaterialSystem_Config.Driver`'s cmdline switch).
- [ ] Fall back to GL if Vulkan init fails, log loudly.
- [ ] ~~Only after parity: delete `Dependencies/OpenGL` and the Gl46 projects.~~ **Decision (2026-08-21):
      GL is never deleted. Both backends stay maintained and switchable per launch.**

## Notes

- ~7.7k lines of backend code to reimplement. The MaterialSystem's 10.6k lines should stay untouched —
  if they don't, the seams in Phase 1 were wrong.
- RenderDoc + validation layers from the first triangle, not after things break.
- Reference: [TF2Vulkan](https://github.com/PazerOP/TF2Vulkan) — the same idea for TF2's shaderapidx9.
  Transferable pieces: a `LogicalState` layer split into static (shadow) and dynamic state managers,
  resolved into a `GraphicsPipeline` cache at draw time (validates Phase 3); vk_mem_alloc for buffers
  (Phase 4); `FormatInfo`/`FormatConverter` as a dedicated module (matches the ImageFormatGl46 split).
  It died to unresolved device-lost errors — one more reason validation layers go on from day one.
- More comparable projects to investigate (user-supplied list, 2026-08-21) — Source-derived codebases
  with modern/Vulkan backends; mine them when a phase gets stuck (esp. textures, render targets,
  combo shaders):
  - Pragma Engine
  - XenEngine
  - OpenCSGO / Stephen Cusi's Vulkan work
  - LambdaComplexSource
  - Strata Source
  - nillerusr/source-engine and its forks (Android GL/Vulkan ports of the 2013 SDK)
  - Source SDK 2013 Vulkan forks
  - Xash3D Vulkan forks (GoldSrc, but architecturally relevant — see ref_vk's uniform/pipeline
    mapping of fixed-function state)
