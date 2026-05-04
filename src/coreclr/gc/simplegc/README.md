# simplegc

A from-scratch standalone GC plugin for CoreCLR. It is loaded as a DLL through
the standard `DOTNET_GCName` switch and implements just enough of the
`IGCHeap` / `IGCHandleManager` interfaces to run real .NET workloads — including
ASP.NET Core / Kestrel — while exposing a wide surface for GC experimentation.

simplegc is intentionally *not* a production garbage collector. It is a
**substrate for experiments**: a small, readable C++ codebase plus a managed
policy SDK (`SimpleGC.Policy`) that lets a workload (or an LLM-authored policy)
direct allocation routing, drive collection, and observe behaviour through
documented exports.

```
src/coreclr/gc/simplegc/
├── simplegc.cpp              # the GC itself (single translation unit)
├── CMakeLists.txt            # builds simplegc.dll
├── SimpleGC.Policy/          # managed policy SDK (IPolicy, hints, host, ABI)
├── hello/                    # minimal "load + Console.WriteLine" smoke test
├── marksweep-test/           # exercises STW mark-sweep correctness
├── policy-demo/              # cross-GC perf-over-time comparison harness
├── policy-host/              # standalone host that loads a managed policy
├── adaptive-policy/          # demo of the count-based AdaptivePolicy
├── kestrel-bench/            # Fortunes-style HTTP workload (real Kestrel)
└── webapi-bench/             # ASP.NET Core Web API workload
```

## Loading simplegc

Build the runtime as usual; `simplegc.dll` ends up in the shared-framework
output directory next to the default GC. Then:

```powershell
$env:DOTNET_GCName = "simplegc.dll"
dotnet run --project hello
```

`SIMPLEGC_LOG=1` (lifecycle) or `SIMPLEGC_LOG=2` (every IGCHeap call) prints
diagnostics to stderr.

## What simplegc supports

### Multi-region heap

A single `VirtualReserve` is partitioned into five named regions, each with
its own bump pointer and commit watermark. Routing decisions select among
them on a per-allocation basis.

| Region            | Size   | Collected? | Use case                                                |
| ----------------- | ------ | ---------- | ------------------------------------------------------- |
| **perm**          | 1 GB   | No         | Process-lifetime objects (config, JIT state, caches)    |
| **request**       | 256 MB | No (rewound) | Per-request scratch; rewound on `simplegc_request_end` |
| **marksweep**     | 256 MB | Yes (STW) | Free-list-backed region for collectible churn          |
| **norefsPerm**    | 1 GB   | No         | Permanent objects with **no managed refs**; skipped by cross-region scans |
| **gen0**          | 64 MB  | No (overflows) | Experimental nursery (overflows into mark-sweep)   |

The single contiguous reservation lets a single card table cover all regions
without biasing tricks, and lets the conservative cross-region pointer scan
walk every object in linear order.

### STW mark-sweep with freelist

The mark-sweep region implements:

* Per-MethodTable mark-bit side bitmap (1 bit per 8 bytes of heap).
* Intrusive freelist using `g_gc_pFreeObjectMethodTable` so dead slots
  remain valid heap objects for any walker.
* Page-granular **decommit-after-sweep** that returns physical memory to the
  OS for freelist slots ≥ 4 KB (tracked in a per-page bitmap so the
  allocator never touches a decommitted slot).
* Per-thread "sub-arena" (TLAB-of-chunks) allocator that amortises the
  mark-sweep lock across many chunk-takes and improves cache locality.
* Linearly-walkable arenas: every abandoned alloc-context tail is encoded
  as a `FreeObject` filler so the heap is parseable from any point.

### Per-thread / per-MethodTable routing

Three orthogonal mechanisms decide which region an allocation lands in:

1. **Per-thread bracket** — `simplegc_set_thread_route(route)` /
   `simplegc_request_begin/end()` for short scopes.
2. **Per-MethodTable table** — `simplegc_set_route(mt_token, route)` writes
   into a lock-free hash table consulted on every chunk refill.
3. **Global default route** — `simplegc_set_default_route(route)` (or
   `SIMPLEGC_DEFAULT_ROUTE`).

The route enum (`Route` in `SimpleGC.Policy.Route`):

| Route        | Region          |
| ------------ | --------------- |
| `Default`    | per arena selection (perm by default) |
| `ForcePerm`  | perm            |
| `ForceReq`   | request arena   |
| `MarkSweep`  | mark-sweep      |
| `NoRefsPerm` | norefsPerm (with `GC_ALLOC_CONTAINS_REF` fallback to perm) |

### Card table + write barriers

simplegc publishes `WriteBarrierParameters` early in `Initialize` and biases
the card-table base pointer so the runtime's standard barrier indexes into
the substrate's table. The conservative perm/request walk can be opted into
"card-aware" mode (`SIMPLEGC_USE_CARDS=1`) where it scans only dirty cards,
implementing a remembered set for cross-region references without
generational copying.

### Managed policy SDK (`SimpleGC.Policy`)

A small managed library that turns simplegc's exports into a programmable
policy surface:

* **`IPolicy` / `PolicyHost`** — register a callback invoked by the native
  dispatcher after each mark-sweep collection. The host marshals a snapshot
  of per-MT counters (`RoutingEntry`) and lets the policy call `SetRoute`.
* **`BasicPolicy`** — promotes any MT that survives ≥ N collections and
  retains ≥ B bytes to mark-sweep.
* **`AdaptivePolicy`** — pay-for-play count-based promotion with quiescence
  detection, optional age/survivor gates, and self-stop after a configurable
  duration.
* **`SimpleGCHintAttribute` / `HintScanner`** — declarative routing.
  Annotate a type with `[SimpleGCHint(Lifetime.Permanent, NoReferences = true)]`
  and the scanner resolves it through reflection at startup (and on
  `AssemblyLoad`) to a concrete route.

### Other features

* **Per-MT allocation tracking** with bounded lock-free hash table; results
  exposed through `simplegc_get_mt_stats`.
* **Strategy callback** — `simplegc_register_strategy` lets managed code
  decide *whether* to collect every N KB allocated.
* **Auto-collect threshold** keyed off `DOTNET_GCHeapHardLimit` so plain
  simplegc users get sensible defaults under tight container caps without
  tuning.
* **Telemetry exports** for arena bytes, mark-sweep stats, memory pressure,
  last-collect duration, and freelist-decommit totals.

## Tuning

All knobs are read once during `GC_Initialize`. User-set values always win
over the cap-aware auto-tune defaults.

### Behavioural

| Variable                            | Default             | Meaning |
| ----------------------------------- | ------------------- | ------- |
| `SIMPLEGC_DEFAULT_ROUTE`            | *(perm)*            | `marksweep` routes default allocs to the collectible region; `gen0` routes to the nursery (overflows to MS until the copy collector lands). |
| `SIMPLEGC_AUTO_COLLECT_MB`          | `128` (auto-tuned)  | When `(perm.committed + ms.committed)` exceeds N MB, force a mark-sweep from the alloc slow path. Throttled to one collect per 16 MB. |
| `SIMPLEGC_AUTO_FREELIST_DECOMMIT`   | `1`                 | After every collect, walk the freelist and `VirtualDecommit` page-aligned interiors of dead slots. Set to `0` to leave the decision to a managed policy. |
| `SIMPLEGC_PROMOTE_AFTER_N_ALLOC`    | `0` (off)           | When an MT's cumulative alloc count crosses N, atomically flip its route from default to `ForcePerm`. Implicitly enables MT tracking and auto-routing. |
| `SIMPLEGC_PERM_IMMUTABLE`           | `0` (off)           | **Unsafe in general.** Trust that perm content is never mutated to point at MS objects, and walk only `[walked_high, perm.bump)` per collect. Safe for benchmark-style workloads only. |
| `SIMPLEGC_USE_CARDS`                | `0` (off)           | Walk perm/request only on dirty cards (sticky-card semantics). Safe for workloads that mutate perm fields. |
| `SIMPLEGC_NO_REWIND`                | `0` (off)           | Disable request-arena rewind on `simplegc_request_end` (debugging aid). |
| `SIMPLEGC_SHADOW_GEN0_SCAN_RUNS`    | `0` (off)           | For the next N MS collects, run a shadow `GcScanRoots(0,0)` alongside the real `(2,2)` scan and log timings. Diagnostic for evaluating gen0 viability. |

### Performance / topology

| Variable                  | Default | Meaning |
| ------------------------- | ------- | ------- |
| `SIMPLEGC_CHUNK_KB`       | `8`     | MS chunk-take size (1–1024 KB). Bigger reduces lock contention; smaller reduces per-thread slack waste. |
| `SIMPLEGC_SUB_ARENA_KB`   | `256`   | Per-thread MS sub-arena ("TLAB of chunks"). `0` disables sub-arenas (every chunk-take takes `g_marksweep.lock`). Cap 16 MB. |

### Observability

| Variable          | Default | Meaning |
| ----------------- | ------- | ------- |
| `SIMPLEGC_LOG`    | `1`     | `0` silent · `1` lifecycle + summaries · `2` every IGCHeap method call |

### Cap-aware auto-tune

When `DOTNET_GCHeapHardLimit` is set (typical for containers), simplegc
applies sensible defaults *only for variables the user has not explicitly
set*:

* **Tight cap (≤ 384 MB):** default route → `marksweep`, auto-collect
  threshold = `cap / 8` (min 8 MB), sub-arenas disabled.
* **Loose cap (> 384 MB):** default route → `marksweep`, auto-collect
  threshold = `cap / 8`, sub-arenas kept.
* **No cap, MS default route still active:** auto-collect threshold falls
  back to 128 MB so long-running workloads do not leak forever.

## Demo workloads

| Project              | What it shows |
| -------------------- | ------------- |
| `hello`              | Smallest program that loads simplegc and exercises the strategy callback. |
| `marksweep-test`     | Correctness checks for the STW mark-sweep region. |
| `policy-demo`        | Compares `simplegc-policy`, `simplegc-nopolicy`, and the default GC on the same workload (CSV per cycle, unified summary). |
| `policy-host`        | Hosts a managed `IPolicy` against a configurable workload. |
| `adaptive-policy`    | Stand-alone demo of `AdaptivePolicy` with quiescence + self-stop. |
| `kestrel-bench`      | Real Kestrel HTTP server with per-request bracketing and a Fortunes-style endpoint. |
| `webapi-bench`       | Minimal ASP.NET Core Web API for end-to-end measurement. |

## Reported wins vs default Workstation GC

Two results from the commit history are worth calling out as credible
head-to-head wins against the default Workstation GC. Both are reproducible
through the demo workloads in this directory.

### Cache workload — clean win on every axis

`policy-demo --workload=cache --gc-mode={default|simplegc-policy}`, 100k
permanent cache entries (~12 MB) + 1000 cycles of (20k transient + 2k cache
reads), 3-run averages (commit `27092574fd0`, M1f):

| metric           | default WKS | simplegc-policy | delta   |
| ---------------- | ----------: | --------------: | ------: |
| `wall_clock_ms`  | 397         | **270**         | −32%    |
| `peak_wss_mb`    | 157         | **56**          | −64%    |
| `pause_total_ms` | 33          | **0**           | −100%   |

The cache fill is bracketed in `SetThreadRoute(ForcePerm)` (so the static
cache lives in the perm arena and is never traced) and each cycle's
transients are bracketed in `RequestBegin` / `RequestEnd` (rewound in O(1)).
No mark-sweep collect runs at all. This is the cleanest demonstration of the
substrate thesis: when the workload's lifetime structure matches the
substrate primitives, simplegc beats default WKS on **every** metric
including memory.

### Fortunes (Kestrel) — managed AdaptivePolicy matches a native promotion path

`kestrel-bench` Fortunes endpoint, `c=16 n=20000 mem-mb=1024
AutoCollectMb=32`, 3-run averages (commit `04900e7e46d`, M1q.1):

| config                          | rps        | p99 µs   | collects |
| ------------------------------- | ---------: | -------: | -------: |
| Default WKS                     | 37,514     | 1,294    | 8        |
| simplegc seed + AdaptivePolicy  | **38,483** | **832**  | 1        |

vs default: **rps +2.6%, p99 −36%**, driven entirely from managed C#
(`SimpleGC.Policy.AdaptivePolicy` plus a small static seed list). The
adaptive layer discovers ~20 runtime-internal types within the first ~1 s
of warmup, then self-stops on quiescence.

**Honest tradeoff.** Both wins come at higher peak working set than default
WKS (cache is the exception — it wins WSS too). On Fortunes, peakWS grows
to ~410 MB vs default's ~180 MB because promoted MTs are pinned to the
uncollectible perm region; the customer chooses the trade. Default WKS
has no equivalent knob, but it also never makes the trade.

## Caveats

* simplegc **never compacts** and the perm / norefsPerm / request regions
  never reclaim memory at the object level. The only collected region is
  mark-sweep; everything else relies on the workload (or a policy) directing
  long-lived churn there.
* Several optimisations (`SIMPLEGC_PERM_IMMUTABLE`, in particular) trade
  correctness for throughput and are safe only for specific workload
  classes — see the inline comments in `simplegc.cpp`.
* The substrate has a single global routing-policy callback slot: only one
  `PolicyHost` / `AdaptivePolicy` can be active per process.
