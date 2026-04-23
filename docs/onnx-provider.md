# ONNX Runtime: CPU vs GPU build

arista-mcp uses ONNX Runtime for the embedder (snowflake-arctic-embed-m-v1.5)
and reranker (ms-marco-MiniLM-L6-v2). The runtime has two flavours that
cannot coexist in one build output — Microsoft #2184 tracks the root cause.
We pick one at `dotnet build` time via an MSBuild property.

## TL;DR

```powershell
# One-shot GPU ingest
pwsh scripts/build-gpu.ps1 -Release -Clean
$env:ARISTA_MCP__Gpu = "true"
dotnet run --project src/AristaMcp.Cli --no-build -c Release -- ingest --force

# Back to normal (CPU serve)
Remove-Item Env:\ARISTA_MCP__Gpu
dotnet build -c Release
```

## Why not runtime-switchable

`Microsoft.ML.OnnxRuntime` and `Microsoft.ML.OnnxRuntime.Gpu` ship the same
managed assembly (`Microsoft.ML.OnnxRuntime.dll`) plus different native
dependencies. The CPU package delivers a CPU-only `onnxruntime.dll`; the GPU
package delivers a CUDA-linked build plus `onnxruntime_providers_cuda.dll`
+ CUDA/cuDNN runtime. Both emit their native bits into the app's output
directory under the same filename, so you can only have one in any build's
`bin/`. A `AristaMcp.Gpu=true` at runtime does not help — the managed side
is already bound to whichever native DLL the NuGet graph resolved.

There is a newer architecture called the **CUDA Plugin EP**
(`onnxruntime_BUILD_CUDA_EP_AS_PLUGIN`) where CUDA is a separate shared
library loaded via `RegisterExecutionProviderLibrary`. The Python and C++
APIs ship today; the C# surface for runtime registration is not yet
stable in 1.24.x. When it lands, we can collapse the two flavours into
one build.

## How the MSBuild conditional works

`src/AristaMcp.Embedding/AristaMcp.Embedding.csproj` has:

```xml
<UseGpuOnnx Condition="'$(UseGpuOnnx)' == ''">false</UseGpuOnnx>
...
<PackageReference Include="Microsoft.ML.OnnxRuntime"     Condition="'$(UseGpuOnnx)' != 'true'" />
<PackageReference Include="Microsoft.ML.OnnxRuntime.Gpu" Condition="'$(UseGpuOnnx)' == 'true'" />
```

`dotnet build` without a property → CPU package. `dotnet build
-p:UseGpuOnnx=true` → GPU package. The condition is on a normal
`ItemGroup`'s `PackageReference` — MSBuild evaluates this during project
evaluation (before any target runs), which is exactly when NuGet restore
resolves the graph.

Both pinned versions live in `Directory.Packages.props`:

```xml
<PackageVersion Include="Microsoft.ML.OnnxRuntime"     Version="1.24.4" />
<PackageVersion Include="Microsoft.ML.OnnxRuntime.Gpu" Version="1.24.4" />
```

Keep both at the same version; otherwise the managed ABI drifts between
build profiles and other projects may bind against the wrong surface.

## Gotchas

- **Always `-Clean` when switching flavours.** NuGet caches the asset
  graph under `obj/`. A leftover CPU graph in `obj/` can produce a build
  that *claims* to be GPU but still loads the CPU `onnxruntime.dll` at
  runtime. `scripts/build-gpu.ps1 -Clean` handles this.
- **`ARISTA_MCP__Gpu=true` AND a GPU build are both required.** The
  managed code calls `AppendExecutionProvider_CUDA()` only when the
  setting is set. On a CPU-package build, that call throws
  `"CUDA provider is not supported"`.
- **Don't commit a GPU build artifact.** `bin/` is gitignored anyway, but
  a publish output with CUDA DLLs (>300 MB) is way bigger than a CPU one
  — if you accidentally `git add -f` it, rollback.
- **CUDA version compatibility**. Package 1.24.x targets CUDA 12.x by
  default (we pin to that). Make sure `nvidia-smi` reports a compatible
  driver before running. ORT will log a clear "CUDA provider not
  available" error if not.

## Why serve stays CPU

A single query embedding on arctic-embed-m-v1.5 is ~100-200 ms on CPU.
A GPU pass is ~5 ms, but the serve host only embeds one query at a time
(QueryEmbeddingCache handles repeats), so the savings are ~100 ms per
cache miss. The VRAM cost (~1 GB steady state + transient CUDA context)
is real and crowds out whatever else the operator runs — Marker
reconversions, local training, anything. CPU serve is the conservative
default; flip to GPU only if latency matters more than VRAM sharing.

## Bench / ingest / curate-triples

All three commands respect `ARISTA_MCP__Gpu` via `EmbeddingOptions.Gpu`.
The CLI will pick up `AppendExecutionProvider_CUDA()` on an `.Gpu` build
and ignore the flag on a CPU build (logs a warning path in that case).
