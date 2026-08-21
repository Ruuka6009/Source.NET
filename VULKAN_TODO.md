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
      Validation layers were unavailable on the test machine (no Vulkan SDK installed) — install it before Phase 3.

## Phase 3 — the actual hard part: state machine -> pipelines

`IShaderAPI.cs` defines `GraphicsBoardState`, documented as "a basic representation of the graphics
state machine" — mutable GL-style global state. Vulkan wants immutable `VkPipeline` objects, and this
mismatch is the bulk of the work.

- [ ] Good news first: `IShaderShadow` (327 lines) is already the *snapshot* concept — Source's shader
      shadow state is effectively a pipeline description. Map shadow state -> `VkGraphicsPipelineCreateInfo`.
- [ ] Pipeline cache keyed on hash of (shadow state + vertex format + render target format). Creating
      pipelines mid-frame will stutter; pre-warm from materials at load.
- [ ] Use `VK_KHR_dynamic_rendering` to skip render pass/framebuffer objects entirely.
- [ ] Keep viewport/scissor/blend constants as dynamic state so they don't multiply the pipeline count.

## Phase 4 — resources

- [ ] Buffers: `VertexBufferGl46`, `IndexBufferGl46`, `MeshGl46`, `DynamicMeshGl46`, `BufferedMeshGl46`, `MeshMgr`
      -> `VkBuffer` + a suballocator (VMA-equivalent; do not `vkAllocateMemory` per buffer).
- [ ] Dynamic meshes need a per-frame ring buffer — they currently assume GL's orphaning behaviour.
- [ ] Textures: `CreateTextureFlags` (cubemap/rendertarget/dynamic/depth/sRGB) -> `VkImage` + view + sampler,
      explicit layout transitions, mip upload via staging.
- [ ] Compressed formats already in use (RGTC, DXT) map to `VK_FORMAT_BC*`.
- [ ] Descriptor set layouts replacing `glUniform*` and texture bind points. Push constants for per-draw data.

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
