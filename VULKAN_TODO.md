# Vulkan backend TODO

Replacing the GL 4.6 renderer. What exists today:

| Piece | Size | Fate |
|---|---|---|
| `Source.ShaderAPI.Gl46` | 11 files, 5227 lines | reimplement as `Source.ShaderAPI.Vulkan` |
| `Source.StdShader.Gl46` | 14 files, 2482 lines | port shader classes |
| `Game.Assets/hl2/shaders/*_gl460.{vs,fs,glsl}` | 17 files | recompile to SPIR-V |
| `Dependencies/OpenGL` | 15156 lines | keep until GL backend is dropped |
| `Source.MaterialSystem` | 21 files, 10621 lines | **should not need changes** if the seams hold |

The contract a Vulkan backend must satisfy is `Source.Common/ShaderAPI/`: `IShaderAPI` (~100 members),
`IShaderShadow` (327 lines), `IShaderDevice`, `IShaderUtil`.

---

## Phase 0 — decide first

- [ ] Pick a binding. Silk.NET.Vulkan is the sane default (actively maintained, same ecosystem as the SDL3 usage).
- [ ] Keep GL alive alongside Vulkan rather than deleting it. `GraphicsDriver` is already a flags enum, so both can coexist and be A/B'd. Deleting GL first means debugging a black screen with no reference.
- [ ] Shader toolchain: GLSL -> SPIR-V via glslang/shaderc, run at build time, output next to the GLSL.

## Phase 1 — widen the seams (all in existing code, no Vulkan yet)

Do this whole phase before writing any Vulkan. It is independently testable — GL must still work at the end of it.

- [ ] `Source.Common/ShaderAPI/IShaderDevice.cs:9-24` — add `Vulkan` to `GraphicsDriver`.
- [ ] `Source.Common/ShaderAPI/IShaderDevice.cs:46-59` — the shader-extension switch returns `gl460.fs` etc. Add the Vulkan case (`.spv`, or `vulkan.fs.spv`).
- [ ] `Source.SDLManager/SDL3_LauncherManager.cs:104` — driver is hardcoded to `OpenGL46` (there is already a `// todo, dont hardcode`). Drive it from config/cmdline.
- [ ] `Source.Common/MaterialSystem/MaterialSystem_Config.cs:40` — same hardcode, same fix.
- [ ] `Source.Bitmap/ImageLoader.cs:223-227` — raw `GL_RGBA32F` / `GL_COMPRESSED_*` constants live in a driver-neutral project. Replace with a neutral format enum + per-backend translation table.
- [ ] `Game.UI/OptionsSubVideo.cs:194` — calls `Gl46.glGetStringSafe(GL_VERSION)` directly for the version string. Move behind `IShaderDevice`.
- [ ] `Source.SDLManager/SDL3_LauncherManager.cs:70` — `SDL3_OpenGL46_Context : IGraphicsContext` wraps `SDL_GL_MakeCurrent`/`SwapWindow`. Add a sibling Vulkan context; note `Present` is a queue submit, not a swap, so check `IGraphicsContext` still fits.
- [ ] Window creation needs `SDL_WINDOW_VULKAN` instead of the GL flag.

## Phase 2 — device bring-up

- [ ] Instance + validation layers (turn them on from day one, behind a convar).
- [ ] Physical device selection, queue families (graphics + present, possibly transfer).
- [ ] Surface via `SDL_Vulkan_CreateSurface`.
- [ ] Swapchain + recreation on resize/alt-tab. This will be the first thing that breaks.
- [ ] Frames in flight, command pools/buffers, semaphores + fences.
- [ ] Goal for this phase: clear the screen to a colour. Nothing else.

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

- [ ] 17 GLSL files -> SPIR-V. `common_gl460.glsl` is an include, so a preprocessor pass is needed first.
- [ ] Explicit `layout(set=, binding=)` on every resource — GL let these be implicit, Vulkan will not.
- [ ] Port the 14 classes in `Source.StdShader.Gl46`; `BaseShader`/`BaseVSShader` hold the binding logic
      and are where most of the porting effort sits.

## Phase 6 — cutover

- [ ] `Source.Launcher/Program.cs` wires `.WithComponent<IShaderAPI, ShaderAPIGl46>()` and
      `.WithStdShader<StdShaderGl46>()` — select by cmdline (`-vulkan`) / config instead.
- [ ] Fall back to GL if Vulkan init fails, log loudly.
- [ ] Only after parity: delete `Dependencies/OpenGL` and the Gl46 projects.

## Notes

- ~7.7k lines of backend code to reimplement. The MaterialSystem's 10.6k lines should stay untouched —
  if they don't, the seams in Phase 1 were wrong.
- RenderDoc + validation layers from the first triangle, not after things break.
