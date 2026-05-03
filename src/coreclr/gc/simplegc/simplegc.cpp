// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// simplegc.cpp — minimal "Hello World" standalone GC.
//
// Goals:
//   * Be the smallest possible IGCHeap/IGCHandleManager implementation that the
//     CoreCLR runtime can load and use to run a `Console.WriteLine("hi")` style app.
//   * Allocate from a single growable bump-pointer arena (VirtualAlloc-backed).
//   * Never actually collect — we just keep allocating until we hit the limit.
//   * Most IGCHeap methods are no-op stubs that log + return safe defaults so we
//     can iterate by observing what the runtime calls.
//
// This is intentionally NOT a working garbage collector. It is a vehicle for
// proving the load/init handshake and for letting us swap pieces (allocator,
// barrier, mark/sweep) one at a time.

#include "common.h"
#include "gcenv.h"
#include "gc.h"          // pulls in standalone forwarders for GCToEEInterface
#include "gcdesc.h"
#include "objecthandle.h"

#include <atomic>
#include <vector>
#include <mutex>
#include <algorithm>
#include <chrono>

#ifdef _MSC_VER
#define DLLEXPORT __declspec(dllexport)
#else
#define DLLEXPORT __attribute__((visibility("default")))
#endif

#define GC_EXPORT extern "C" DLLEXPORT

// ---------------------------------------------------------------------------
// Trace helpers
// ---------------------------------------------------------------------------
//
// SIMPLEGC_TRACE controls verbosity. Override on the command line / cmake.
// Levels:
//   0 = silent
//   1 = lifecycle (init, shutdown, gc) and allocations summaries
//   2 = every IGCHeap/IGCHandleManager method that gets called
#ifndef SIMPLEGC_TRACE
#define SIMPLEGC_TRACE 1
#endif

// Runtime override for trace level. Read once on the first log call from the
// SIMPLEGC_LOG env var so the bench can dial logging up or down without a
// rebuild. -1 means "uninitialized; check env var on next call".
static int g_simplegcTraceLevel = -1;

static void simplegc_log(int level, const char* fmt, ...)
{
    if (g_simplegcTraceLevel < 0)
    {
        const char* v = std::getenv("SIMPLEGC_LOG");
        g_simplegcTraceLevel = v ? std::atoi(v) : SIMPLEGC_TRACE;
    }
    if (level > g_simplegcTraceLevel) return;
    va_list args;
    va_start(args, fmt);
    fprintf(stderr, "[simplegc] ");
    vfprintf(stderr, fmt, args);
    fprintf(stderr, "\n");
    fflush(stderr);
    va_end(args);
}

#define LOG1(...) simplegc_log(1, __VA_ARGS__)
#define LOG2(...) simplegc_log(2, __VA_ARGS__)

// Tag every method with a one-line marker so we can see what the runtime calls.
#define TRACE_METHOD() LOG2("%s", __FUNCTION__)

// Forward declarations of file-scope helpers needed by code in the anonymous
// namespace below. The bodies are defined further down (line ~840) once the
// runtime has wired up g_gc_pFreeObjectMethodTable.
static inline size_t ms_free_object_min_size();
static void          ms_set_free_obj(uint8_t* p, size_t size);

// ---------------------------------------------------------------------------
// Globals required by the standalone GC contract
// ---------------------------------------------------------------------------

namespace
{
    // Heap parameters. We carve a single 256 MB VirtualReserve into two
    // arenas:
    //   * perm       (1 GB)   - long-lived state, never reset
    //   * request    (256 MB) - reset by simplegc_request_end()
    //   * marksweep  (256 MB) - real STW mark-sweep collected (M1b)
    //
    // Keeping all three within a single reserve lets us continue publishing
    // one contiguous heap range to the runtime (so write barriers and the
    // card table cover all arenas without any biasing changes).
    constexpr size_t kPermSize       = 1024 * 1024 * 1024;  // 1 GB
    constexpr size_t kRequestSize    = 256  * 1024 * 1024;  // 256 MB
    constexpr size_t kMarkSweepSize  = 256  * 1024 * 1024;  // 256 MB
    constexpr size_t kNoRefsPermSize = 1024 * 1024 * 1024;  // 1 GB (M1g)
    constexpr size_t kHeapSize       = kPermSize + kRequestSize + kMarkSweepSize + kNoRefsPermSize;
    // M1h: smaller commit grain so simplegc can fit under tight Job
    // Object memory caps. Was 16 MB — reducing to 1 MB lets the substrate
    // demonstrate graceful operation under ~64-96 MB process memory caps
    // where the default GC's working set already exceeds the cap.
    constexpr size_t kCommitGrain    = 1 * 1024 * 1024;
    constexpr size_t kAllocCtxQuantDefault = 8 * 1024;
    // M1l measure: chunk size affects MS-allocation slow-path frequency.
    // Bigger chunks = fewer chunk-takes = less g_marksweep.lock contention,
    // at the cost of higher per-chunk slack waste. Tune via env at startup.
    size_t            kAllocCtxQuant       = kAllocCtxQuantDefault;

    // M1m: per-thread MS sub-arena ("TLAB of chunks"). Each thread takes a
    // fresh sub-arena under g_marksweep.lock, then bumps lock-free within it
    // for chunk-takes. Drastically reduces lock contention under high-c
    // multi-threaded workloads (Fortunes c=16 was burning ~12s wall on lock
    // acquires across all threads — sub-arenas amortize that to one
    // acquire per N chunk-takes where N = subArenaSize/avgChunkSize).
    //
    // Cache locality: each thread's allocations stay within a contiguous
    // sub-arena region, so the thread's working set keeps reusing the same
    // cache lines instead of scattering across the whole MS region.
    //
    // Default 256 KB (32 chunks at 8 KB; mostly fits in modern L2 caches).
    // SIMPLEGC_SUB_ARENA_KB=0 disables sub-arenas — every chunk-take goes
    // straight through g_marksweep.lock as before M1m. Useful for A/B test.
    constexpr size_t kSubArenaSizeDefault = 256 * 1024;
    size_t           kSubArenaSize        = kSubArenaSizeDefault;

    // M1m.2: opt-in incremental perm walk. When SIMPLEGC_PERM_IMMUTABLE=1,
    // the GC trusts that perm-arena object contents are never mutated to
    // store new cross-region references after the previous walk completed.
    // Then on each collect we only scan [g_permWalkedHigh, perm.bump),
    // which is typically 0 bytes after warmup for steady-state workloads
    // (when SIMPLEGC_DEFAULT_ROUTE=marksweep, perm receives only startup
    // allocations and a tiny trickle of runtime-internal objects).
    //
    // SAFETY CONTRACT (caller responsibility): with NO WRITE BARRIERS, the
    // GC cannot detect a perm field write that mutates an existing perm
    // object to point at a freshly allocated MS object. If the workload
    // mutates perm fields after a walk, those MS objects can be missed
    // and incorrectly swept, leading to torn references and AVs.
    //
    // Validated SAFE on:
    //   - policy-demo grow (single-thread, no runtime infrastructure churn:
    //     perm content is JIT startup state, immutable thereafter).
    //
    // Validated UNSAFE on:
    //   - Fortunes / Kestrel (HTTP machinery, HttpClient, header caches,
    //     dispatch tables get mutated after warmup → AV). General .NET
    //     runtime workloads cannot satisfy this contract without barriers.
    //
    // Default-off; user must explicitly opt in. This is a building block
    // for future safer designs (card tables for perm; per-MT immutability
    // tags); it is NOT a generally enabled optimization.
    bool   g_permImmutableOptIn = false;
    uint8_t* g_permWalkedHigh   = nullptr;   // highest-scanned perm.bump
    uint8_t* g_requestWalkedHigh= nullptr;   // highest-scanned request.bump

    // Each arena owns its own bump pointer, committed-watermark, and lock.
    struct Arena
    {
        std::mutex lock;
        uint8_t*   start      = nullptr;
        uint8_t*   end        = nullptr;
        uint8_t*   committed  = nullptr; // first uncommitted byte
        uint8_t*   bump       = nullptr; // first free byte
        uint8_t*   checkpoint = nullptr; // saved bump for current request-scope reset
    };

    Arena g_perm;
    Arena g_request;
    // M1g: dedicated no-refs perm sub-arena. Same bump-allocator semantics as
    // g_perm but the substrate REJECTS allocations with GC_ALLOC_CONTAINS_REF
    // set (silently rerouting them to g_perm). By construction this arena
    // cannot hold pointers into the mark-sweep region, so the M1e
    // conservative cross-region scan is skipped over it. This is the
    // "segregated no-refs perm" optimization: ref-free types promoted by
    // the policy land here and pay zero scan cost per collection.
    Arena g_norefsPerm;

    // ---- M1b: mark-sweep region ---------------------------------------------
    //
    // A *third* region in the same VirtualReserve, but with real STW mark-sweep
    // collection. Per-object allocation only (no chunk-style fast-path bumping)
    // so the region is fully linearly walkable for the sweep phase.
    //
    // M1b constraints (intentional simplifications):
    //   - References from perm/request to mark-sweep are NOT supported; mark-sweep
    //     refs must live only on stacks, in mark-sweep itself, or in our handle
    //     store. Enforced by managed-side test code.
    //   - Finalizable, LOH/POH allocations always force perm regardless of
    //     routing.
    //   - Interior root scan does a linear walk to find the containing object.
    //   - Single mutex around the whole region (no concurrent allocation).
    //
    // Layout: free list is intrusive — each free slot is a g_gc_pFreeObjectMethodTable
    // array whose first 16 bytes hold {next pointer; size}. We keep the free MT in
    // place so the slot remains a valid heap object for any walker.
    struct MarkSweepRegion
    {
        std::mutex lock;
        uint8_t*   start_obj   = nullptr; // first object pointer (after start padding)
        uint8_t*   end         = nullptr; // reserved end
        uint8_t*   committed   = nullptr; // first uncommitted byte
        uint8_t*   bump        = nullptr; // first byte never yet handed out

        // Side bitmap: 1 bit per kMarkBitGranularity bytes of [start_obj, end).
        uint8_t*   mark_bits   = nullptr;
        size_t     mark_bits_size = 0;

        // Free list: singly linked, intrusive. The slot pointer IS the free
        // object pointer; ms_freelist_next/size read from inside the slot.
        uint8_t*   free_head   = nullptr;

        std::atomic<uint64_t> bytes_allocated{0};
        std::atomic<uint64_t> bytes_freelist{0};
        std::atomic<uint64_t> bytes_live_after_collect{0};
        std::atomic<uint64_t> bytes_collected_total{0};
        std::atomic<uint64_t> bytes_bump_rewound{0};
        std::atomic<uint64_t> n_collections{0};
        std::atomic<uint64_t> n_objects_allocated{0};
        std::atomic<uint64_t> n_objects_swept{0};
    };

    constexpr size_t kMarkBitGranularity = 8; // 1 bit per 8 bytes of heap

    MarkSweepRegion g_marksweep;

    // Per-thread routing flag. When true, Alloc routes the next allocation
    // into the mark-sweep region instead of t_activeArena/perm. Set via the
    // simplegc_route_to_marksweep export. (Legacy single-bit; superseded by
    // t_forceRoute below for new code, kept working for back-compat.)
    thread_local bool t_useMarkSweep = false;

    // Per-thread "auto-route" flag. When set, the slow-path Alloc consults
    // the dominant MT in the just-flushed chunk (post-hoc walk) and adjusts
    // routing for the NEXT chunk based on that MT's recommended route.
    // Driven by simplegc_enable_auto_routing(int).
    thread_local bool t_autoRoute = false;

    // Per-thread forced route (M1c). Higher priority than t_useMarkSweep.
    // Value is one of kRouteDefault / kRouteForcePerm / kRouteForceReq /
    // kRouteMarkSweep. kRouteDefault means "no override; fall through to
    // t_useMarkSweep / g_defaultRoute / arena selection". Set via the
    // simplegc_set_thread_route export — used by policy-aware workloads
    // to bracket specific allocation sites.
    thread_local uint8_t t_forceRoute = 0; // kRouteDefault

    // Global default route (M1c). When non-zero, Alloc consults this AFTER
    // the per-thread overrides and the request bracket and uses it to drive
    // routing for any allocation that hasn't been forced elsewhere. This
    // lets a workload say "everything goes to mark-sweep by default" without
    // a per-frame thread bracket. Set via simplegc_set_default_route.
    std::atomic<uint8_t> g_defaultRoute{0}; // kRouteDefault

    // Overall VirtualReserve range (perm.start .. request.end). Used by
    // IsHeapPointer / RegisterFrozenSegment etc. that want to know "is this
    // anywhere in our managed heap?"
    uint8_t*  g_heapStart = nullptr;
    uint8_t*  g_heapEnd   = nullptr;

    // Per-thread "active arena". null = use g_perm. Set by simplegc_request_begin.
    thread_local Arena* t_activeArena = nullptr;

    // Per-thread last-observed alloc context. Cached so simplegc_request_end
    // can flush it (force the next allocation to refill from perm) without
    // walking all alloc contexts.
    thread_local gc_alloc_context* t_lastAllocCtx = nullptr;

    // Forward declaration: defined further down in this namespace once the
    // M1a per-MT counter machinery is set up. Used by
    // simplegc_flush_alloc_context.
    static inline void simplegc_attribute_pending(gc_alloc_context* acontext);

    // Flushes the alloc-context cache so the runtime's fast path falls back
    // into our IGCHeap::Alloc on the next allocation.
    //
    // CRITICAL: the runtime's fast path (e.g. RhpNewArrayFast) does NOT use
    // gc_alloc_context::alloc_limit. It uses ee_alloc_context::combined_limit,
    // which is the field located *immediately before* gc_alloc_context in the
    // owning ee_alloc_context (offset -sizeof(uint8_t*) from acontext). The
    // fast path computes "available = combined_limit - alloc_ptr" and only
    // bails to slow path if (size > available). If we zero alloc_ptr but
    // leave combined_limit at a non-zero value, available is computed as a
    // huge unsigned number and the fast path "succeeds", writing the new
    // object's MethodTable to address 0 -> AV.
    //
    // Layout (per src/coreclr/vm/gcheaputilities.h ee_alloc_context):
    //   offset  0: uint8_t* combined_limit
    //   offset +8: gc_alloc_context  (i.e. what we get as 'acontext')
    //              +0: alloc_ptr
    //              +8: alloc_limit
    //              ...
    // ---- M1e: linearly-walkable arenas --------------------------------------
    //
    // To make the perm and request arenas walkable from outside the GC code
    // (so cross-region references can be discovered during mark), every chunk
    // we hand out reserves a small `slack` tail past acontext->alloc_limit.
    // When the alloc context is abandoned (refill, request begin/end, route
    // change, FixAllocContext for STW), the abandoned region
    //   [alloc_ptr, alloc_limit + slack)
    // is overwritten with a g_gc_pFreeObjectMethodTable "free object" header
    // covering the full tail. The slack guarantees this region is always
    // >= ms_free_object_min_size() bytes — large enough to encode as a free
    // object — even when the runtime fast-path leaves a < min-object-size gap
    // before bailing to slow-path Alloc.
    static inline size_t arena_fill_slack()
    {
        // ms_free_object_min_size() is typically 24 bytes on x64. Round up to
        // 8-byte alignment with a 32-byte floor so chunk handout math stays
        // simple even before g_gc_pFreeObjectMethodTable is wired up.
        size_t s = ms_free_object_min_size();
        if (s < 32) s = 32;
        return (s + 7) & ~size_t(7);
    }

    // Is `p` inside a bump-arena chunk (perm / request / norefsPerm / MS)?
    // Used to decide whether arena_fill_chunk_tail should write a filler.
    //
    // M1k.1: MS chunks now follow the same slack convention as perm/request —
    // every MS chunk reserves `slack` bytes past acontext->alloc_limit so the
    // tail can always be encoded as a free-object filler at fix-alloc-context
    // time. This keeps the MS region linearly walkable for sweep and for the
    // ms_build_object_start_index walker, which is required because MS uses
    // chunk-based allocation (multiple back-to-back JIT-fast-path objects per
    // slow-path call) instead of single-object slots.
    static inline bool bumpable_arena_range(uint8_t* p)
    {
        return (p >= g_perm.start       && p < g_perm.end)
            || (p >= g_request.start    && p < g_request.end)
            || (p >= g_norefsPerm.start && p < g_norefsPerm.end)
            || (p >= g_marksweep.start_obj && p < g_marksweep.end);
    }

    // Write a g_gc_pFreeObjectMethodTable filler covering the abandoned tail
    // [alloc_ptr, alloc_limit + slack) of `acontext`'s chunk, IFF that chunk
    // came from the perm or request bump arena. Caller must invoke BEFORE
    // zeroing alloc_ptr / alloc_limit.
    static inline void arena_fill_chunk_tail(gc_alloc_context* acontext)
    {
        if (acontext == nullptr) return;
        uint8_t* alloc_ptr   = acontext->alloc_ptr;
        uint8_t* alloc_limit = acontext->alloc_limit;
        if (alloc_ptr == nullptr || alloc_limit == nullptr) return;
        if (alloc_ptr > alloc_limit) return; // defensive: should never happen

        // Only fill if this chunk is in a bump arena (perm / request /
        // norefsPerm / MS). Each of these arenas reserves `slack` bytes past
        // alloc_limit when handing out a chunk, so writing the filler stays
        // inside the chunk we own.
        if (!bumpable_arena_range(alloc_ptr))
        {
            return;
        }

        size_t slack    = arena_fill_slack();
        uint8_t* tail_end = alloc_limit + slack;
        size_t tail_size  = (size_t)(tail_end - alloc_ptr);
        // Invariant: every perm/request chunk reserves >= slack bytes past
        // alloc_limit, so tail_size >= slack >= ms_free_object_min_size().
        if (tail_size < ms_free_object_min_size()) return;
        ms_set_free_obj(alloc_ptr, tail_size);
    }

    static inline void simplegc_flush_alloc_context(gc_alloc_context* acontext)
    {
        // M1a: capture per-MT stats from the chunk we're about to abandon.
        simplegc_attribute_pending(acontext);
        if (acontext == nullptr) return;
        // M1e: encode the abandoned chunk tail as a free object so the arena
        // remains linearly walkable for cross-region mark.
        arena_fill_chunk_tail(acontext);
        // Zero combined_limit (the field 8 bytes before acontext).
        uint8_t** combinedLimit = reinterpret_cast<uint8_t**>(
            reinterpret_cast<uint8_t*>(acontext) - sizeof(uint8_t*));
        *combinedLimit = nullptr;
        acontext->alloc_ptr   = nullptr;
        acontext->alloc_limit = nullptr;
    }

    std::atomic<uint64_t> g_totalAllocated{0};
    std::atomic<uint64_t> g_requestedBytes{0};
    std::atomic<uint64_t> g_objectCount{0};
    std::atomic<uint64_t> g_gcCount{0};

    // ---- Phase 3: managed strategy bridge -----------------------------------
    //
    // The app registers a function pointer to a [UnmanagedCallersOnly] managed
    // method via simplegc_register_strategy(). simplegc::Alloc consults this
    // method when allocation crosses kConsultThreshold bytes since the last
    // consult. The method must NEVER allocate (it runs from inside Alloc).

    constexpr uint32_t kStrategyAbiVersion = 1;
    constexpr uint64_t kConsultThreshold   = 256 * 1024; // 256 KB

    // ---- M1j.3: cap-aware auto-collect (env-driven) ------------------------
    //
    // When SIMPLEGC_AUTO_COLLECT_MB=N is set, simplegc::Alloc's slow path
    // checks (perm.committed - perm.start) + (marksweep.committed -
    // marksweep.start) and triggers simplegc_force_collect() when this
    // exceeds N MB. Throttled by g_autoCollectThrottleBytes so we don't
    // collect on every slow-path call once we're over the threshold.
    //
    // 0 = disabled (default; preserves the previous "never auto-collect"
    // behavior). Set at init from the env var; never changes thereafter.
    std::atomic<uint64_t> g_autoCollectThresholdBytes{0};
    std::atomic<uint64_t> g_bytesAtLastAutoCollect{0};
    std::atomic<uint64_t> g_autoCollectCount{0};
    constexpr uint64_t kAutoCollectThrottleBytes = 16 * 1024 * 1024; // 16 MB

    // M1l measure: per-phase timing accumulators (microseconds). Updated by
    // simplegc_force_collect so we can quantify where collect time is spent
    // (perm scan vs sweep vs root scan) before deciding which substrate to
    // optimize first. Dumped to stderr after each collect at LOG1.
    struct PhaseTimes
    {
        std::atomic<uint64_t> fix_alloc_us{0};
        std::atomic<uint64_t> scan_roots_us{0};
        std::atomic<uint64_t> walk_arenas_us{0};
        std::atomic<uint64_t> drain_gray_us{0};
        std::atomic<uint64_t> sweep_us{0};
        std::atomic<uint64_t> total_us{0};
        std::atomic<uint64_t> collects{0};
        // Last-collect snapshot of perm/request walked sizes (bytes), to
        // correlate walk_arenas_us with the scanned region size.
        std::atomic<uint64_t> last_perm_walk_bytes{0};
        std::atomic<uint64_t> last_request_walk_bytes{0};
    };
    PhaseTimes g_phaseTimes;

    // M1l measure: MS slow-path entry counters. ms_slow_calls counts every
    // chunk-take attempt, ms_slow_lock_us aggregates time spent inside
    // g_marksweep.lock across all chunk-takes. Together with bytes_allocated
    // they let us compute average chunk-take latency under c=16 to see
    // whether mutex contention is the throughput bottleneck.
    std::atomic<uint64_t> g_msSlowCalls{0};
    std::atomic<uint64_t> g_msSlowLockUs{0};

    // M1m: per-thread MS sub-arena state. The lock-free fast path bumps
    // within [t_msSubBump..t_msSubEnd). g_msSubGen is incremented INSIDE
    // STW (post-sweep, pre-RestartEE) by every collection; threads compare
    // their cached t_msSubGen against the global gen on every chunk-take
    // and refill on mismatch (sub-arena memory may have been swept into a
    // freelist slot, rewound by bump-reset, or both, while STW was active).
    //
    // g_msSubArenaTakes counts lock-free fast-path successes; useful as a
    // ratio against g_msSlowCalls (= lock-acquired path) to measure how
    // often the fast path wins.
    thread_local uint8_t* t_msSubBump = nullptr;
    thread_local uint8_t* t_msSubEnd  = nullptr;
    thread_local uint64_t t_msSubGen  = 0;
    std::atomic<uint64_t> g_msSubGen{1};            // start at 1; threads init to 0 (mismatch)
    std::atomic<uint64_t> g_msSubArenaTakes{0};      // lock-free chunk-takes
    std::atomic<uint64_t> g_msSubArenaRefills{0};    // fresh sub-arenas allocated under lock

    // Single-collection-at-a-time gate. Replaces the `g_marksweep.lock`
    // bracket previously used at the top of simplegc_force_collect.
    //
    // We can NOT hold a native mutex across SuspendEE: other threads that
    // are inside the Alloc slow path may already be blocked on
    // g_marksweep.lock while running native code (no safe-point), so
    // SuspendEE would deadlock waiting for them. An atomic flag gates
    // entry without blocking. Threads that lose the race simply return —
    // either someone else is collecting now, or they retry on the next
    // slow-path crossing of the threshold.
    std::atomic<bool> g_collectInProgress{false};

    // ---- M1a: per-MethodTable allocation counters --------------------------
    //
    // Open-addressed lock-free hash table keyed by MethodTable*. Every chunk
    // refill (slow-path Alloc) walks the previously-filled chunk and bumps
    // per-MT (count, bytes, min/max size) for every object it finds.
    //
    // Why post-hoc: IGCHeap::Alloc does not receive the MethodTable* — the
    // runtime writes MT at object[0] AFTER Alloc returns. So we attribute
    // the previous chunk's contents on the *next* call (or on flush).
    //
    // Capacity sized for "all distinct MTs an app touches" — 8192 is
    // generous for typical .NET apps. Overflow is silently dropped (it's a
    // stats degradation, never a correctness issue).
    constexpr size_t kMtTableCapacity = 8192; // must be power of 2

    struct SimpleGCMTEntry
    {
        std::atomic<MethodTable*> mt;        // null = empty slot
        std::atomic<uint64_t>     count;
        std::atomic<uint64_t>     bytes;
        std::atomic<uint32_t>     min_size;
        std::atomic<uint32_t>     max_size;
        // ---- Adaptive routing (post-M1b) -------------------------------
        // Survival counters: populated during mark-phase walk. Reset at the
        // start of each mark-sweep so they reflect THIS collection's
        // survival, not a running total.
        std::atomic<uint64_t>     bytes_survived;
        std::atomic<uint64_t>     count_survived;
        // Number of mark-sweep collections this MT had any surviving objects.
        // Bumped after each collection where count_survived > 0.
        std::atomic<uint32_t>     age_collections;
        // Routing decision written by the managed policy callback (or by
        // simplegc_set_route directly). Read by Alloc on the slow path.
        //   0 = Default (perm/request fast path)
        //   1 = ForcePerm
        //   2 = ForceRequest (only honored if a request bracket is open)
        //   3 = MarkSweep
        std::atomic<uint8_t>      route;
    };
    static_assert(sizeof(std::atomic<uint8_t>) == 1, "atomic<uint8_t> must be 1 byte");

    // Route values exposed through the public ABI; mirrored in C# as enum.
    constexpr uint8_t kRouteDefault     = 0;
    constexpr uint8_t kRouteForcePerm   = 1;
    constexpr uint8_t kRouteForceReq    = 2;
    constexpr uint8_t kRouteMarkSweep   = 3;
    constexpr uint8_t kRouteNoRefsPerm  = 4;  // M1g: ref-free perm arena; not scanned

    SimpleGCMTEntry        g_mtTable[kMtTableCapacity];
    std::atomic<uint64_t>  g_mtAttributedObjects{0};
    std::atomic<uint64_t>  g_mtAttributedBytes{0};
    std::atomic<uint32_t>  g_mtTableUsed{0};
    std::atomic<uint64_t>  g_mtOverflowObjects{0}; // table-full drops

    // Default OFF. Managed policy host turns this on via
    // simplegc_enable_mt_tracking() after the runtime has finished its early
    // initialization (NativeRuntimeEventSource cctor / reflection setup).
    // The walk dereferences MethodTable* pointers we read out of the heap,
    // so it must only run when the runtime is past its bring-up phase.
    std::atomic<int>       g_mtTrackingEnabled{0};

    // Per-thread tracking of the chunk we last refilled. On next slow-path
    // entry (or flush) we walk [t_chunkStart, acontext->alloc_ptr) and
    // attribute objects to their MTs.
    thread_local uint8_t* t_chunkStart = nullptr;
    thread_local uint8_t* t_chunkEnd   = nullptr;

    // Per-route byte tally accumulated during a single mt_walk_chunk pass.
    // Reset at the start of each walk; consulted at the end if t_autoRoute
    // is enabled to decide whether to flip the thread's routing flags.
    thread_local uint64_t t_routeTally[5] = {0, 0, 0, 0, 0};
}

// Public ABI struct exposed via simplegc_get_mt_stats. ABI version + size are
// asserted statically so managed-side mirrors stay in sync. Append-only.
struct SimpleGCMtStat
{
    uint64_t mt_token;     // MethodTable* as opaque uint64
    uint64_t count;
    uint64_t bytes;
    uint32_t min_size;
    uint32_t max_size;
};
static_assert(sizeof(SimpleGCMtStat) == 32, "SimpleGCMtStat ABI mismatch");

namespace
{
    // Cheap hash for MT pointer.
    static inline size_t mt_hash(MethodTable* mt)
    {
        uintptr_t h = reinterpret_cast<uintptr_t>(mt);
        h ^= h >> 16;
        h *= 0x85ebca6bULL;
        h ^= h >> 13;
        return static_cast<size_t>(h) & (kMtTableCapacity - 1);
    }

    // Lock-free find-or-insert + update.
    // Also accumulates `size` into the per-thread route tally bucket
    // corresponding to this MT's current routing decision; the tally is
    // consulted by simplegc_attribute_pending for auto-routing.
    static void mt_record(MethodTable* mt, uint32_t size)
    {
        size_t h = mt_hash(mt);
        for (size_t i = 0; i < kMtTableCapacity; ++i)
        {
            size_t idx = (h + i) & (kMtTableCapacity - 1);
            SimpleGCMTEntry& e = g_mtTable[idx];
            MethodTable* cur = e.mt.load(std::memory_order_acquire);
            if (cur == mt)
            {
                e.count.fetch_add(1, std::memory_order_relaxed);
                e.bytes.fetch_add(size, std::memory_order_relaxed);
                uint32_t mn = e.min_size.load(std::memory_order_relaxed);
                while (size < mn &&
                       !e.min_size.compare_exchange_weak(mn, size, std::memory_order_relaxed)) {}
                uint32_t mx = e.max_size.load(std::memory_order_relaxed);
                while (size > mx &&
                       !e.max_size.compare_exchange_weak(mx, size, std::memory_order_relaxed)) {}
                uint8_t r = e.route.load(std::memory_order_relaxed);
                if (r < 5) t_routeTally[r] += size;
                return;
            }
            if (cur == nullptr)
            {
                MethodTable* expected = nullptr;
                if (e.mt.compare_exchange_strong(expected, mt,
                        std::memory_order_acq_rel, std::memory_order_acquire))
                {
                    e.count.store(1, std::memory_order_relaxed);
                    e.bytes.store(size, std::memory_order_relaxed);
                    e.min_size.store(size, std::memory_order_relaxed);
                    e.max_size.store(size, std::memory_order_relaxed);
                    g_mtTableUsed.fetch_add(1, std::memory_order_relaxed);
                    // New entry → route is default; tally into bucket 0.
                    t_routeTally[kRouteDefault] += size;
                    return;
                }
                if (expected == mt)
                {
                    e.count.fetch_add(1, std::memory_order_relaxed);
                    e.bytes.fetch_add(size, std::memory_order_relaxed);
                    uint8_t r = e.route.load(std::memory_order_relaxed);
                    if (r < 5) t_routeTally[r] += size;
                    return;
                }
            }
        }
        g_mtOverflowObjects.fetch_add(1, std::memory_order_relaxed);
    }

    // Compute object size from MT, matching the formula used elsewhere in the
    // GC. Result is rounded up to 8-byte alignment (matches simplegc_raw_alloc).
    static inline uint32_t simplegc_obj_size(MethodTable* mt, uint8_t* obj)
    {
        uint32_t size = mt->GetBaseSize();
        if (mt->HasComponentSize())
        {
            // Number of components is a uint32_t at object + sizeof(void*).
            uint32_t numComp = *reinterpret_cast<uint32_t*>(obj + sizeof(void*));
            size += numComp * mt->RawGetComponentSize();
        }
        return (size + 7u) & ~7u;
    }

    // Sanity-check an MT pointer before dereferencing. We cannot fully
    // validate without a runtime API, but we can catch obviously-bad values
    // (low addresses, mis-aligned). Combined with SEH around the deref.
    static inline bool mt_pointer_plausible(MethodTable* mt)
    {
        uintptr_t v = reinterpret_cast<uintptr_t>(mt);
        if (v < 0x10000) return false;                  // null page / very low
        if ((v & 0x3) != 0) return false;               // unaligned
#if defined(_WIN64)
        // User-mode upper bound on Windows x64 (well above any normal heap).
        if (v >= 0x800000000000ull) return false;
#endif
        return true;
    }

    // Inner walker: no C++ objects on the stack so we can wrap with SEH.
    // Returns the number of objects walked; *outBytes accumulates bytes.
    static uint32_t mt_walk_chunk_inner(uint8_t* p, uint8_t* end, uint64_t* outBytes)
    {
        const uint32_t kMaxObjsPerWalk = 65536;
        uint32_t walkedObjs = 0;
        uint64_t walkedBytes = 0;
        while (p < end && walkedObjs < kMaxObjsPerWalk)
        {
            MethodTable* mt = *reinterpret_cast<MethodTable**>(p);
            if (mt == nullptr)
            {
                // Object not yet initialized at slow-path entry. Stop here.
                break;
            }
            if (!mt_pointer_plausible(mt))
            {
                break;
            }
            uint32_t size = simplegc_obj_size(mt, p);
            if (size < sizeof(void*) || size > 256u * 1024u * 1024u ||
                p + size > end)
            {
                break;
            }
            mt_record(mt, size);
            ++walkedObjs;
            walkedBytes += size;
            p += size;
        }
        *outBytes = walkedBytes;
        return walkedObjs;
    }

    // Walk objects in [chunk_start, end) and attribute each.
    // Caller guarantees end > chunk_start and end is within the chunk we tracked.
    static void mt_walk_chunk(uint8_t* chunk_start, uint8_t* end)
    {
        uint64_t walkedBytes = 0;
        uint32_t walkedObjs  = 0;
#ifdef _WIN32
        __try
        {
            walkedObjs = mt_walk_chunk_inner(chunk_start, end, &walkedBytes);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // AV during walk — abandon this chunk's attribution. Counters
            // bumped before the AV stay; we just bail out cleanly.
            walkedObjs = 0;
        }
#else
        walkedObjs = mt_walk_chunk_inner(chunk_start, end, &walkedBytes);
#endif
        if (walkedObjs > 0)
        {
            g_mtAttributedObjects.fetch_add(walkedObjs, std::memory_order_relaxed);
            g_mtAttributedBytes.fetch_add(walkedBytes, std::memory_order_relaxed);
        }
    }

    // Drain the per-thread pending chunk into the MT counters. Called on
    // slow-path entry (before refill) and on flush_alloc_context.
    static inline void simplegc_attribute_pending(gc_alloc_context* acontext)
    {
        if (g_mtTrackingEnabled.load(std::memory_order_acquire) == 0)
        {
            t_chunkStart = nullptr;
            t_chunkEnd   = nullptr;
            return;
        }

        if (t_chunkStart == nullptr)
        {
            return;
        }
        if (acontext != nullptr)
        {
            uint8_t* alloc_ptr = acontext->alloc_ptr;
            // Walk only if alloc_ptr is within the chunk we tracked. After
            // simplegc_flush_alloc_context zeroed alloc_ptr we'll skip.
            if (alloc_ptr > t_chunkStart && alloc_ptr <= t_chunkEnd)
            {
                // Reset the per-route byte tally; mt_record will populate it
                // as we walk.
                t_routeTally[0] = t_routeTally[1] = t_routeTally[2] = t_routeTally[3] = t_routeTally[4] = 0;
                mt_walk_chunk(t_chunkStart, alloc_ptr);

                // Auto-routing: if the dominant route in the just-walked
                // chunk is non-default and accounts for >= 60% of attributed
                // bytes, flip the thread's routing flags for the next chunk.
                if (t_autoRoute)
                {
                    uint64_t total = t_routeTally[0] + t_routeTally[1] +
                                     t_routeTally[2] + t_routeTally[3] +
                                     t_routeTally[4];
                    if (total >= 1024)
                    {
                        // Find dominant non-default bucket.
                        uint8_t  best  = kRouteDefault;
                        uint64_t bestB = t_routeTally[kRouteDefault];
                        for (uint8_t r = 1; r < 5; ++r)
                        {
                            if (t_routeTally[r] > bestB)
                            {
                                bestB = t_routeTally[r];
                                best  = r;
                            }
                        }
                        // Threshold: 60% of total bytes.
                        if (bestB * 5 >= total * 3)
                        {
                            switch (best)
                            {
                            case kRouteMarkSweep:
                                t_useMarkSweep = true;
                                break;
                            case kRouteForcePerm:
                                t_useMarkSweep = false;
                                t_activeArena  = nullptr; // perm
                                break;
                            case kRouteForceReq:
                                t_useMarkSweep = false;
                                // t_activeArena left as-is; honored only if
                                // a request bracket has set it to &g_request.
                                break;
                            default:
                                t_useMarkSweep = false;
                                break;
                            }
                        }
                    }
                }
            }
        }
        t_chunkStart = nullptr;
        t_chunkEnd   = nullptr;
    }
}

// File-scope (external linkage) so the extern "C" exports below can use these
// types in their parameter lists without warnings. The struct + typedef are not
// part of any public header — strategy authors mirror them in managed code.

// Stats blob passed by-pointer to the managed callback. New fields must be
// appended; ABI version + size are checked at registration time so old
// strategies on a newer GC keep working.
struct SimpleGCStats
{
    uint64_t totalAllocatedBytes;     // raw alloc-context refill bytes
    uint64_t requestedBytes;          // bytes the runtime actually requested (object sizes)
    uint64_t objectCount;
    uint64_t gcCount;
    uint64_t bytesSinceLastConsult;
};
static_assert(sizeof(SimpleGCStats) == 40, "SimpleGCStats ABI size mismatch");

using ShouldCollectFn = int (LOCALGC_CALLCONV *)(const SimpleGCStats* stats);

namespace
{
    std::atomic<ShouldCollectFn> g_shouldCollect{nullptr};
    std::atomic<uint64_t>        g_consultCount{0};
    std::atomic<uint64_t>        g_strategyApprovedGCs{0};
    std::atomic<uint64_t>        g_bytesAtLastConsult{0};

    // A "card table" placeholder. Real GCs use this for write barriers; we don't
    // collect, but the runtime expects a non-null card table when we publish our
    // heap range via StompWriteBarrier. We allocate the smallest valid table that
    // covers our heap range.
    uint32_t*          g_cardTable   = nullptr;
#ifdef FEATURE_MANUALLY_MANAGED_CARD_BUNDLES
    uint32_t*          g_cardBundleTable = nullptr;
#endif

    // Free-object MethodTable supplied by the runtime; the GC normally uses this
    // to "fill" gaps. We store it but don't use it.
    MethodTable*       g_freeObjMT   = nullptr;

    // Frozen segments registered by the runtime (e.g. for pre-baked string
    // literals embedded in CoreLib / R2R images). Their address ranges live
    // OUTSIDE our managed heap, but IsHeapPointer must return true for them.
    struct FrozenSegment { uint8_t* lo; uint8_t* hi; };
    std::mutex                  g_frozenLock;
    std::vector<FrozenSegment>  g_frozenSegments;
}

// ---------------------------------------------------------------------------
// Globals declared in gccommon.h that gcenv.ee.standalone.inl forwarders need
// ---------------------------------------------------------------------------
//
// The gc.h chain includes gcenv.ee.standalone.inl which references these
// extern globals. We DO NOT pull in gccommon.cpp (which is part of the regular
// GC), so we must define them ourselves.

IGCHeapInternal*   g_theGCHeap          = nullptr;
IGCHandleManager*  g_theGCHandleManager = nullptr;
IGCToCLR*          g_theGCToCLR         = nullptr;
VersionInfo        g_runtimeSupportedVersion;
bool               g_oldMethodTableFlags = false;

extern "C" uint32_t*    g_gc_card_table        = nullptr;
#ifdef FEATURE_MANUALLY_MANAGED_CARD_BUNDLES
extern "C" uint32_t*    g_gc_card_bundle_table = nullptr;
#endif
extern "C" uint8_t*     g_gc_lowest_address    = nullptr;
extern "C" uint8_t*     g_gc_highest_address   = nullptr;
extern "C" GCHeapType   g_gc_heap_type         = GC_HEAP_WKS;
extern "C" uint32_t     g_max_generation       = 2; // we pretend gen2 = max
extern "C" MethodTable* g_gc_pFreeObjectMethodTable = nullptr;
extern "C" uint32_t     g_num_processors       = 0;

VOLATILE(int32_t)  g_fSuspensionPending  = 0;

// ---------------------------------------------------------------------------
// Bump-pointer allocator
// ---------------------------------------------------------------------------

static bool simplegc_init_heap()
{
    // Reserve a single big region. Commit lazily.
    g_heapStart = static_cast<uint8_t*>(GCToOSInterface::VirtualReserve(kHeapSize, 0, 0));
    if (g_heapStart == nullptr)
    {
        LOG1("VirtualReserve(%zu) failed", kHeapSize);
        return false;
    }

    g_heapEnd = g_heapStart + kHeapSize;

    // Carve into perm + request arenas.
    g_perm.start      = g_heapStart;
    g_perm.end        = g_heapStart + kPermSize;
    g_perm.committed  = g_perm.start;
    g_perm.bump       = g_perm.start;

    g_request.start     = g_perm.end;
    g_request.end       = g_request.start + kRequestSize;
    g_request.committed = g_request.start;
    g_request.bump      = g_request.start;

    // The runtime reads SIZEOF_OBJHEADER (8 bytes on x64) of zero memory immediately
    // *before* every Object pointer to inspect the SyncBlock index. Reserve a small
    // padding area at the start of EACH arena so the first object pointer we hand
    // out has a valid (committed, zero-initialized) sync-block prefix.
    constexpr size_t kStartPadding = 64;
    g_perm.bump = g_perm.start + kStartPadding;
    if (!GCToOSInterface::VirtualCommit(g_perm.start, kStartPadding))
    {
        LOG1("VirtualCommit(perm start padding) failed");
        return false;
    }
    g_perm.committed = g_perm.start + ((kStartPadding + 4095) & ~static_cast<size_t>(4095));

    g_request.bump = g_request.start + kStartPadding;
    if (!GCToOSInterface::VirtualCommit(g_request.start, kStartPadding))
    {
        LOG1("VirtualCommit(request start padding) failed");
        return false;
    }
    g_request.committed = g_request.start + ((kStartPadding + 4095) & ~static_cast<size_t>(4095));

    // Mark-sweep region: third sub-range of the same VirtualReserve, immediately
    // after the request arena.
    uint8_t* msBase = g_request.end;
    g_marksweep.start_obj = msBase + kStartPadding;
    g_marksweep.end       = msBase + kMarkSweepSize;
    g_marksweep.committed = msBase;
    g_marksweep.bump      = g_marksweep.start_obj;
    if (!GCToOSInterface::VirtualCommit(msBase, kStartPadding))
    {
        LOG1("VirtualCommit(marksweep start padding) failed");
        return false;
    }
    g_marksweep.committed = msBase + ((kStartPadding + 4095) & ~static_cast<size_t>(4095));

    // M1g: no-refs perm region — fourth sub-range of the same VirtualReserve,
    // immediately after the mark-sweep region. Bump-allocator semantics
    // (same as g_perm) but the substrate enforces "no GC pointers" at
    // Alloc time via the GC_ALLOC_CONTAINS_REF flag.
    uint8_t* nrpBase = msBase + kMarkSweepSize;
    g_norefsPerm.start     = nrpBase;
    g_norefsPerm.end       = nrpBase + kNoRefsPermSize;
    g_norefsPerm.committed = g_norefsPerm.start;
    g_norefsPerm.bump      = g_norefsPerm.start + kStartPadding;
    if (!GCToOSInterface::VirtualCommit(g_norefsPerm.start, kStartPadding))
    {
        LOG1("VirtualCommit(norefsPerm start padding) failed");
        return false;
    }
    g_norefsPerm.committed = g_norefsPerm.start + ((kStartPadding + 4095) & ~static_cast<size_t>(4095));

    // Side bitmap: 1 bit per kMarkBitGranularity bytes of the data area. We
    // size it for the FULL reserve so we never need to grow it.
    g_marksweep.mark_bits_size =
        (kMarkSweepSize + kMarkBitGranularity * 8 - 1) / (kMarkBitGranularity * 8);
    g_marksweep.mark_bits = static_cast<uint8_t*>(::malloc(g_marksweep.mark_bits_size));
    if (g_marksweep.mark_bits == nullptr)
    {
        LOG1("malloc(mark_bits=%zu) failed", g_marksweep.mark_bits_size);
        return false;
    }
    memset(g_marksweep.mark_bits, 0, g_marksweep.mark_bits_size);

    g_gc_lowest_address  = g_heapStart;
    g_gc_highest_address = g_heapEnd;

    // Build a tiny card table. Entry per ~2KB region (card_byte_shift=11 on 64-bit).
    // The runtime indexes the card table using ABSOLUTE addresses:
    //     g_gc_card_table[ ((size_t)dst) >> card_byte_shift ]
    // To make that valid for any dst in our heap, we BIAS the published pointer
    // back by (lowest_address >> shift) so that the indexing lands inside our
    // allocated buffer.
    constexpr int kCardByteShift = 11;
    size_t cardCount = (kHeapSize >> kCardByteShift) + 1;
    g_cardTable = static_cast<uint32_t*>(::malloc(cardCount * sizeof(uint32_t)));
    memset(g_cardTable, 0, cardCount * sizeof(uint32_t));
    {
        uint8_t* biased = reinterpret_cast<uint8_t*>(g_cardTable)
                        - ((reinterpret_cast<size_t>(g_heapStart)) >> kCardByteShift);
        g_gc_card_table = reinterpret_cast<uint32_t*>(biased);
    }

#ifdef FEATURE_MANUALLY_MANAGED_CARD_BUNDLES
    // Bundle table: card_bundle_byte_shift = 21 on x64. Each bundle entry covers
    // ~2MB. Same biasing scheme as above.
    constexpr int kCardBundleByteShift = 21;
    size_t bundleCount = (kHeapSize >> kCardBundleByteShift) + 1;
    g_cardBundleTable = static_cast<uint32_t*>(::malloc(bundleCount * sizeof(uint32_t)));
    memset(g_cardBundleTable, 0, bundleCount * sizeof(uint32_t));
    {
        uint8_t* biased = reinterpret_cast<uint8_t*>(g_cardBundleTable)
                        - ((reinterpret_cast<size_t>(g_heapStart)) >> kCardBundleByteShift);
        g_gc_card_bundle_table = reinterpret_cast<uint32_t*>(biased);
    }
#endif

    LOG1("heap reserved at %p .. %p (perm=%zu MB, request=%zu MB, marksweep=%zu MB, norefsperm=%zu MB)",
         g_heapStart, g_heapEnd, kPermSize >> 20, kRequestSize >> 20, kMarkSweepSize >> 20, kNoRefsPermSize >> 20);

    // ---- M1j.3: env-driven cap-aware policy ----
    // SIMPLEGC_AUTO_COLLECT_MB=N
    //   When perm+ms committed bytes exceed N MB, force_collect is
    //   triggered from the Alloc slow path. 0 disables. Recommended:
    //   set to 60-70% of the process memory cap for headroom.
    // SIMPLEGC_DEFAULT_ROUTE=marksweep
    //   Set the global default route to MS. Allocations that would
    //   otherwise default to perm get routed to the collectible MS
    //   region instead. Without this, auto-collect has no work to do
    //   because everything sits in (uncollectible) perm.
    bool autoCollectUserSet = false;
    bool defaultRouteUserSet = false;
    bool subArenaUserSet = false;
    if (const char* v = std::getenv("SIMPLEGC_AUTO_COLLECT_MB"))
    {
        if (*v != '\0')
        {
            uint64_t mb = std::strtoull(v, nullptr, 10);
            g_autoCollectThresholdBytes.store(mb * 1024ULL * 1024ULL,
                                              std::memory_order_release);
            autoCollectUserSet = true;
            LOG1("SIMPLEGC_AUTO_COLLECT_MB=%llu", (unsigned long long)mb);
        }
    }
    if (const char* v = std::getenv("SIMPLEGC_DEFAULT_ROUTE"))
    {
        if (std::strcmp(v, "marksweep") == 0 || std::strcmp(v, "ms") == 0)
        {
            g_defaultRoute.store(kRouteMarkSweep, std::memory_order_release);
            defaultRouteUserSet = true;
            LOG1("SIMPLEGC_DEFAULT_ROUTE=marksweep");
        }
    }
    // M1l measure: SIMPLEGC_CHUNK_KB tunes the MS chunk-take size.
    // Default 8 KB (kAllocCtxQuantDefault). Bigger reduces lock contention
    // on g_marksweep.lock but increases per-chunk slack waste under tight caps.
    if (const char* v = std::getenv("SIMPLEGC_CHUNK_KB"))
    {
        if (*v != '\0')
        {
            uint64_t kb = std::strtoull(v, nullptr, 10);
            if (kb >= 1 && kb <= 1024)
            {
                kAllocCtxQuant = (size_t)(kb * 1024);
                LOG1("SIMPLEGC_CHUNK_KB=%llu (kAllocCtxQuant=%zu bytes)",
                     (unsigned long long)kb, kAllocCtxQuant);
            }
        }
    }
    // M1m: SIMPLEGC_SUB_ARENA_KB tunes the per-thread MS sub-arena size.
    // Default 256 KB. Set to 0 to disable sub-arenas (legacy behavior:
    // every chunk-take goes through g_marksweep.lock).
    if (const char* v = std::getenv("SIMPLEGC_SUB_ARENA_KB"))
    {
        if (*v != '\0')
        {
            uint64_t kb = std::strtoull(v, nullptr, 10);
            if (kb <= 16384)  // cap at 16 MB
            {
                kSubArenaSize = (size_t)(kb * 1024);
                subArenaUserSet = true;
                LOG1("SIMPLEGC_SUB_ARENA_KB=%llu (kSubArenaSize=%zu bytes)",
                     (unsigned long long)kb, kSubArenaSize);
            }
        }
    }
    // M1m.2: SIMPLEGC_PERM_IMMUTABLE=1 opts into the incremental
    // perm/request walk (only scan the high-water portion since the
    // last walk). See declaration of g_permImmutableOptIn for the
    // correctness contract.
    if (const char* v = std::getenv("SIMPLEGC_PERM_IMMUTABLE"))
    {
        if (*v != '\0' && *v != '0')
        {
            g_permImmutableOptIn = true;
            LOG1("SIMPLEGC_PERM_IMMUTABLE=1 (incremental perm walk enabled)");
        }
    }
    // ---- M1m.1: cap-aware auto-tune from DOTNET_GCHeapHardLimit ----
    // When the runtime is configured with a hard heap-size limit (typical
    // for containers), auto-engage the policy-mode defaults so plain
    // simplegc users get sensible behavior under tight caps without
    // setting SIMPLEGC_* env vars manually.
    //
    //  cap > 384 MB: keep sub-arenas on (256 KB), default route stays
    //                perm (uncollectible bump arena handles loose caps
    //                with no collection at all when total allocs fit).
    //  cap <= 384 MB: switch to MS default route + auto-collect threshold
    //                = cap/8 (e.g. 256 MB cap -> 32 MB threshold,
    //                140 MB cap -> 17 MB). Disable sub-arenas (which
    //                over-commit MS at tight caps - see M1m regression).
    //
    // User SIMPLEGC_* settings always win; this only fills in defaults.
    uint64_t hardLimitBytes = 0;
    if (const char* v = std::getenv("DOTNET_GCHeapHardLimit"))
    {
        if (*v != '\0')
        {
            // Hex (0x prefix) per CLR convention.
            hardLimitBytes = std::strtoull(v, nullptr, 0);
        }
    }
    if (hardLimitBytes > 0)
    {
        size_t hardLimitMb = (size_t)(hardLimitBytes >> 20);
        LOG1("DOTNET_GCHeapHardLimit=%zu MB detected; auto-tuning defaults", hardLimitMb);
        bool tightCap = (hardLimitMb <= 384);
        if (tightCap)
        {
            if (!defaultRouteUserSet)
            {
                g_defaultRoute.store(kRouteMarkSweep, std::memory_order_release);
                LOG1("  auto-tune: default_route=marksweep (tight cap)");
            }
            if (!autoCollectUserSet)
            {
                size_t thresholdMb = hardLimitMb / 8;
                if (thresholdMb < 8) thresholdMb = 8;
                g_autoCollectThresholdBytes.store(
                    (uint64_t)thresholdMb * 1024ULL * 1024ULL,
                    std::memory_order_release);
                LOG1("  auto-tune: auto_collect_mb=%zu (cap/8)", thresholdMb);
            }
            if (!subArenaUserSet)
            {
                kSubArenaSize = 0;
                LOG1("  auto-tune: sub_arena_kb=0 (tight cap)");
            }
        }
        else
        {
            // Loose cap: still engage MS+auto-collect so plain simplegc
            // doesn't OOM on continuous workloads, but keep sub-arenas
            // for throughput.
            if (!defaultRouteUserSet)
            {
                g_defaultRoute.store(kRouteMarkSweep, std::memory_order_release);
                LOG1("  auto-tune: default_route=marksweep (loose cap)");
            }
            if (!autoCollectUserSet)
            {
                size_t thresholdMb = hardLimitMb / 8;
                g_autoCollectThresholdBytes.store(
                    (uint64_t)thresholdMb * 1024ULL * 1024ULL,
                    std::memory_order_release);
                LOG1("  auto-tune: auto_collect_mb=%zu (cap/8)", thresholdMb);
            }
        }
    }
    return true;
}

// Commit more pages in the given arena to satisfy bytesNeeded. Caller holds arena.lock.
static bool simplegc_commit(Arena& arena, size_t bytesNeeded)
{
    if (arena.bump + bytesNeeded > arena.committed)
    {
        size_t want = (size_t)(arena.bump + bytesNeeded - arena.committed);
        size_t grain = (want + kCommitGrain - 1) & ~(kCommitGrain - 1);
        if (arena.committed + grain > arena.end)
        {
            LOG1("OOM in arena [%p..%p): need %zu (used=%zu)",
                 arena.start, arena.end, grain, (size_t)(arena.bump - arena.start));
            return false;
        }
        if (!GCToOSInterface::VirtualCommit(arena.committed, grain))
        {
            LOG1("VirtualCommit(%zu) failed", grain);
            return false;
        }
        arena.committed += grain;
    }
    return true;
}

// Bump-pointer raw allocate from a specific arena.
static uint8_t* simplegc_raw_alloc(Arena& arena, size_t size)
{
    std::lock_guard<std::mutex> guard(arena.lock);
    // Align to 8 (small object alignment).
    size = (size + 7) & ~static_cast<size_t>(7);
    if (!simplegc_commit(arena, size))
        return nullptr;
    uint8_t* p = arena.bump;
    arena.bump += size;
    g_totalAllocated.fetch_add(size, std::memory_order_relaxed);
    g_objectCount.fetch_add(1, std::memory_order_relaxed);
    return p;
}

// ---------------------------------------------------------------------------
// M1b: mark-sweep allocation, freelist, and free-object helpers.
// ---------------------------------------------------------------------------
//
// Free-object encoding: a span of N bytes (N >= ms_free_object_min_size())
// becomes a g_gc_pFreeObjectMethodTable instance whose ArrayBase::m_dwLength
// stores N - free_object_base_size. The slot remains a valid heap object that
// any linear walker can step over.
//
// Freelist link: stored at offset 16 inside the slot (after MT@0 and length@8).
// This is past the ArrayBase header so it doesn't disturb the runtime's view
// of the free object. We only stash it for live free slots (not for free
// objects used as filler for abandoned alloc-context tails).

static inline uint32_t ms_free_object_base_size()
{
    if (g_gc_pFreeObjectMethodTable == nullptr) return 24; // sane fallback
    return g_gc_pFreeObjectMethodTable->GetBaseSize();
}

// Minimum sweepable slot. Below this, we cannot create a parseable free object.
static inline size_t ms_free_object_min_size()
{
    return ms_free_object_base_size();
}

// Write a g_gc_pFreeObjectMethodTable header at p describing a slot of `size`
// bytes total (including the header). Caller must guarantee size >=
// ms_free_object_min_size(). Does NOT touch the freelist.
static void ms_set_free_obj(uint8_t* p, size_t size)
{
    assert(g_gc_pFreeObjectMethodTable != nullptr);
    assert(size >= ms_free_object_min_size());
    *reinterpret_cast<MethodTable**>(p) = g_gc_pFreeObjectMethodTable;
    // m_dwLength is a uint32_t at offset sizeof(void*) (= 8 on x64). The
    // runtime's SetFree writes a size_t there; we follow suit so high bits
    // are clean if the runtime ever reads it.
    *reinterpret_cast<size_t*>(p + sizeof(void*)) = size - ms_free_object_base_size();
}

// Read the size of a slot whose header is a free object MT.
static size_t ms_read_free_obj_size(uint8_t* p)
{
    size_t numComponents = *reinterpret_cast<size_t*>(p + sizeof(void*));
    return ms_free_object_base_size() + numComponents;
}

// Freelist link helpers. Link is stored at p + 16 (just past ArrayBase header).
// Free slot must be at least 24 bytes (handled by min size constraint).
static inline uint8_t*& ms_freelist_next(uint8_t* p)
{
    return *reinterpret_cast<uint8_t**>(p + 16);
}

// Push a free slot onto the head of the region's freelist. Caller holds
// g_marksweep.lock.
static void ms_freelist_push(uint8_t* p, size_t size)
{
    ms_set_free_obj(p, size);
    if (size >= 24)
    {
        ms_freelist_next(p) = g_marksweep.free_head;
        g_marksweep.free_head = p;
    }
    g_marksweep.bytes_freelist.fetch_add(size, std::memory_order_relaxed);
}

// Pop a slot from the freelist that's at least `size` bytes. First-fit search.
// Returns nullptr if none. Caller holds g_marksweep.lock. On success, the
// returned slot is removed from the freelist and zeroed. If the slot is
// larger than needed and the remainder is >= ms_free_object_min_size(), the
// remainder is re-linked as a smaller free object.
//
// **Soundness rule**: we MUST NOT hand back a slot whose remainder is
// non-zero but too small to hold a free filler header. The linear walker
// (ms_build_object_start_index, ms_sweep_locked) advances by the alloc'd
// object's size, lands in the unfilled "slop", reads MT=0, and `break`s —
// silently truncating the rest of the arena from the index. We avoid that
// by skipping any slot where `remainder` would be in (0, min_size).
static uint8_t* ms_freelist_take(size_t size)
{
    const size_t minFreeSize = ms_free_object_min_size();
    uint8_t** slot = &g_marksweep.free_head;
    while (*slot != nullptr)
    {
        uint8_t* p = *slot;
        size_t s = ms_read_free_obj_size(p);
        // Accept slots that fit exactly OR leave a fillable remainder.
        // Reject slots that would create unfillable slop.
        bool exactFit  = (s == size);
        bool clean_split = (s >= size + minFreeSize);
        if (exactFit || clean_split)
        {
            // Unlink.
            *slot = ms_freelist_next(p);
            g_marksweep.bytes_freelist.fetch_sub(s, std::memory_order_relaxed);

            if (clean_split)
            {
                size_t remainder = s - size;
                uint8_t* tail = p + size;
                ms_set_free_obj(tail, remainder);
                if (remainder >= 24)
                {
                    ms_freelist_next(tail) = g_marksweep.free_head;
                    g_marksweep.free_head = tail;
                }
                g_marksweep.bytes_freelist.fetch_add(remainder, std::memory_order_relaxed);
                s = size;
            }
            // Always zero the slot before handing it back. The runtime may
            // skip its own zeroing when GC_ALLOC_ZEROING_OPTIONAL is set.
            memset(p, 0, s);
            return p;
        }
        slot = &ms_freelist_next(p);
    }
    return nullptr;
}

// Commit more pages in the mark-sweep region to satisfy bytesNeeded.
// Caller holds g_marksweep.lock.
static bool ms_commit(size_t bytesNeeded)
{
    if (g_marksweep.bump + bytesNeeded > g_marksweep.committed)
    {
        size_t want = (size_t)(g_marksweep.bump + bytesNeeded - g_marksweep.committed);
        size_t grain = (want + kCommitGrain - 1) & ~(kCommitGrain - 1);
        if (g_marksweep.committed + grain > g_marksweep.end)
        {
            return false;
        }
        if (!GCToOSInterface::VirtualCommit(g_marksweep.committed, grain))
        {
            LOG1("VirtualCommit(marksweep %zu) failed", grain);
            return false;
        }
        g_marksweep.committed += grain;
    }
    return true;
}

// Bump-allocate `size` bytes from the high water mark. Caller holds g_marksweep.lock.
static uint8_t* ms_raw_bump(size_t size)
{
    if (!ms_commit(size)) return nullptr;
    uint8_t* p = g_marksweep.bump;
    g_marksweep.bump += size;
    return p;
}

// ---------------------------------------------------------------------------
// M1b: STW mark-sweep collection
// ---------------------------------------------------------------------------
//
// Collection sequence:
//   1. SuspendEE
//   2. Clear mark bits, prepare gray queue
//   3. GcScanRoots(callback, max_gen, max_gen, sc) — walks managed stacks,
//      statics, and runtime-internal roots.
//   4. Walk our own SimpleHandleStore — every slot is a strong root (we don't
//      currently distinguish handle types).
//   5. Drain the gray queue: for each gray object, walk its reference fields
//      via GCDesc and mark+enqueue any reference that lands in our region.
//   6. Sweep: linear walk of [start_obj, bump). Any unmarked object becomes
//      a free slot — fill with g_gc_pFreeObjectMethodTable, push to freelist.
//      Free objects placed by previous sweeps are passed through as already-free.
//   7. RestartEE
//
// Collection happens under g_marksweep.lock to serialize collectors.
// Concurrent allocation is not supported (allocator also holds the lock).

namespace
{
    // Gray queue: per-collection scratch. Lives in heap-allocated storage so
    // we don't blow the stack on big graphs.
    std::vector<uint8_t*> g_grayQueue;

    static inline bool ms_in_region(uint8_t* p)
    {
        return p >= g_marksweep.start_obj && p < g_marksweep.bump;
    }

    static inline size_t ms_bit_index(uint8_t* p)
    {
        return (size_t)(p - g_marksweep.start_obj) / kMarkBitGranularity;
    }

    // Returns true if this is the first time the bit was set (i.e. the caller
    // should enqueue this object as gray). Returns false if already marked.
    static bool ms_test_and_set_mark(uint8_t* p)
    {
        size_t bit  = ms_bit_index(p);
        size_t byte = bit >> 3;
        uint8_t mask = (uint8_t)(1u << (bit & 7));
        if (g_marksweep.mark_bits[byte] & mask) return false;
        g_marksweep.mark_bits[byte] |= mask;
        return true;
    }

    // Promote callback for GcScanRoots. Marks the referenced object if it
    // lies in our mark-sweep region.
    //
    // For interior pointers (GC_CALL_INTERIOR), we conservatively walk the
    // region linearly to find the containing object. This is O(N) per
    // interior root; acceptable given how few such roots exist.
    static uint8_t* ms_find_containing_object(uint8_t* interior);

    static void LOCALGC_CALLCONV ms_promote_callback(PTR_PTR_Object ppObj,
                                                     ScanContext* /*sc*/,
                                                     uint32_t flags)
    {
        if (ppObj == nullptr) return;
        uint8_t* obj = reinterpret_cast<uint8_t*>(*ppObj);
        if (obj == nullptr || !ms_in_region(obj)) return;

        if (flags & GC_CALL_INTERIOR)
        {
            obj = ms_find_containing_object(obj);
            if (obj == nullptr) return;
        }

        if (ms_test_and_set_mark(obj))
        {
            g_grayQueue.push_back(obj);
        }
    }

    // Linear scan to find the object whose [start, start+size) covers `interior`.
    // Stops if it walks past the high water mark or hits a corrupt header.
    // Caller must hold g_marksweep.lock (we read free objects' lengths).
    static uint8_t* ms_find_containing_object(uint8_t* interior)
    {
        uint8_t* p   = g_marksweep.start_obj;
        uint8_t* end = g_marksweep.bump;
        while (p < end)
        {
            MethodTable* mt = *reinterpret_cast<MethodTable**>(p);
            if (mt == nullptr) return nullptr;
            uint32_t size;
            if (mt == g_gc_pFreeObjectMethodTable)
            {
                size = (uint32_t)ms_read_free_obj_size(p);
            }
            else
            {
                size = simplegc_obj_size(mt, p);
            }
            if (size < sizeof(void*) || p + size > end) return nullptr;
            if (interior >= p && interior < p + size) return p;
            p += size;
        }
        return nullptr;
    }

    // Walk the GC reference fields of `obj` (whose MT is `mt` and total size
    // is `size`). For each reference into our region, mark+enqueue.
    //
    // Ports the go_through_object_cl logic from gc.cpp:7395, restricted to
    // ContainsGCPointers types and to refs that fall inside our region.
    static void ms_walk_object_refs(MethodTable* mt, uint8_t* obj, size_t size)
    {
        if (!mt->ContainsGCPointers()) return;

        CGCDesc* map = CGCDesc::GetCGCDescFromMT(mt);
        CGCDescSeries* cur = map->GetHighestSeries();
        ptrdiff_t cnt = (ptrdiff_t)map->GetNumSeries();

        if (cnt >= 0)
        {
            // Plain object (non-array of valuetypes). One series per
            // contiguous run of reference-typed fields.
            CGCDescSeries* last = map->GetLowestSeries();
            do
            {
                uint8_t** parm   = reinterpret_cast<uint8_t**>(obj + cur->GetSeriesOffset());
                uint8_t** ppstop = reinterpret_cast<uint8_t**>(
                    reinterpret_cast<uint8_t*>(parm) + cur->GetSeriesSize() + size);
                while (parm < ppstop)
                {
                    uint8_t* ref = *parm;
                    if (ref != nullptr && ms_in_region(ref))
                    {
                        if (ms_test_and_set_mark(ref))
                        {
                            g_grayQueue.push_back(ref);
                        }
                    }
                    parm++;
                }
                cur--;
            } while (cur >= last);
        }
        else
        {
            // Repeating series — array of valuetypes that contain refs.
            // Layout: cnt is negative; cur->val_serie[__i] for __i in [cnt..0)
            // describes a stride. Walk components from startoffset to obj+size.
            uint8_t** parm = reinterpret_cast<uint8_t**>(obj + cur->startoffset);
            ptrdiff_t cs = mt->RawGetComponentSize();
            uint8_t* obj_end = obj + size;
            (void)cs; // cs unused — we step by sum of skip+nptrs in the val_serie
            while (reinterpret_cast<uint8_t*>(parm) < obj_end)
            {
                for (ptrdiff_t i = 0; i > cnt; --i)
                {
                    HALF_SIZE_T skip  = cur->val_serie[i].skip;
                    HALF_SIZE_T nptrs = cur->val_serie[i].nptrs;
                    uint8_t** ppstop = parm + nptrs;
                    while (parm < ppstop)
                    {
                        uint8_t* ref = *parm;
                        if (ref != nullptr && ms_in_region(ref))
                        {
                            if (ms_test_and_set_mark(ref))
                            {
                                g_grayQueue.push_back(ref);
                            }
                        }
                        parm++;
                    }
                    parm = reinterpret_cast<uint8_t**>(reinterpret_cast<uint8_t*>(parm) + skip);
                }
            }
        }
    }

    // Walk our SimpleHandleStore as additional roots. Defined later (after
    // the simplegc_handles namespace is introduced) but declared here.
    void ms_scan_handle_store();

    // Bump survival counters on the MT entry for `mt` by `size` bytes / 1 obj.
    // Adds the entry if missing (we want survival data even for MTs we didn't
    // observe at allocation time, e.g. types allocated before tracking was
    // enabled).
    static void mt_record_survived(MethodTable* mt, uint32_t size)
    {
        size_t h = mt_hash(mt);
        for (size_t i = 0; i < kMtTableCapacity; ++i)
        {
            size_t idx = (h + i) & (kMtTableCapacity - 1);
            SimpleGCMTEntry& e = g_mtTable[idx];
            MethodTable* cur = e.mt.load(std::memory_order_acquire);
            if (cur == mt)
            {
                e.count_survived.fetch_add(1, std::memory_order_relaxed);
                e.bytes_survived.fetch_add(size, std::memory_order_relaxed);
                return;
            }
            if (cur == nullptr)
            {
                MethodTable* expected = nullptr;
                if (e.mt.compare_exchange_strong(expected, mt,
                        std::memory_order_acq_rel, std::memory_order_acquire))
                {
                    e.count_survived.store(1, std::memory_order_relaxed);
                    e.bytes_survived.store(size, std::memory_order_relaxed);
                    g_mtTableUsed.fetch_add(1, std::memory_order_relaxed);
                    return;
                }
                if (expected == mt)
                {
                    e.count_survived.fetch_add(1, std::memory_order_relaxed);
                    e.bytes_survived.fetch_add(size, std::memory_order_relaxed);
                    return;
                }
            }
        }
    }

    // Drain the gray queue: for each marked-but-unscanned object, walk its
    // reference fields and mark+enqueue every ref that lies in our region.
    // Also bumps per-MT survival counters for the policy engine.
    static void ms_drain_gray_queue()
    {
        const uint64_t kMaxDrain = 100000000;
        uint64_t drained = 0;
        while (!g_grayQueue.empty())
        {
            if (++drained > kMaxDrain)
            {
                LOG1("ms_drain_gray_queue: cap reached drained=%llu queue=%zu - ABORTING",
                     (unsigned long long)drained, g_grayQueue.size());
                return;
            }
            uint8_t* obj = g_grayQueue.back();
            g_grayQueue.pop_back();
            MethodTable* mt = *reinterpret_cast<MethodTable**>(obj);
            if (mt == nullptr || mt == g_gc_pFreeObjectMethodTable) continue;
            size_t size = simplegc_obj_size(mt, obj);
            mt_record_survived(mt, (uint32_t)size);
            ms_walk_object_refs(mt, obj, size);
        }
    }

    // M1e: build a sorted vector of MS-region object starts for fast
    // containment lookup during the conservative pointer scan. Used only
    // inside force_collect under STW; MS layout is stable while we hold
    // g_marksweep.lock.
    //
    // We only need EXACT-START matches for the conservative scan (perm/
    // request stack/heap fields holding pointers to MS objects). Interior
    // pointers from heap fields are vanishingly rare in C# and would only
    // arise from explicit unsafe code; rooted byrefs on the stack are
    // already handled via GcScanRoots(GC_CALL_INTERIOR).
    //
    // Stored as object starts (not size pairs) — a candidate is "valid" iff
    // it appears in the vector. A `std::lower_bound` is O(log n). For the
    // expected MS sizes (thousands of objects), this is comfortably under
    // a microsecond per probe.
    static std::vector<uint8_t*> g_msStartsCache;

    static void ms_build_object_start_index()
    {
        g_msStartsCache.clear();
        uint8_t* p   = g_marksweep.start_obj;
        uint8_t* end = g_marksweep.bump;
        size_t walked = 0;
        size_t freeFillers = 0;
        const char* stopReason = "reached_end";
        uint8_t* lastP = p;
        while (p < end)
        {
            MethodTable* mt = *reinterpret_cast<MethodTable**>(p);
            if (mt == nullptr) { stopReason = "mt_null"; lastP = p; break; }
            uint32_t size;
            if (mt == g_gc_pFreeObjectMethodTable)
            {
                size = (uint32_t)ms_read_free_obj_size(p);
                ++freeFillers;
            }
            else
            {
                size = simplegc_obj_size(mt, p);
                // Real MS objects are recorded; free fillers are skipped
                // (no point marking a free object).
                g_msStartsCache.push_back(p);
            }
            if (size < sizeof(void*) || p + size > end) { stopReason = "bad_size"; lastP = p; break; }
            p += size;
            ++walked;
        }
        // Already sorted by construction (linear scan from low to high).
        LOG1("ms_build_object_start_index: %zu MS objects indexed (walked=%zu fillers=%zu stop=%s lastP=%p arena=[%p..%p))",
             g_msStartsCache.size(), walked, freeFillers, stopReason, lastP,
             g_marksweep.start_obj, g_marksweep.bump);
    }

    static inline bool ms_is_object_start(uint8_t* candidate)
    {
        if (g_msStartsCache.empty()) return false;
        auto it = std::lower_bound(g_msStartsCache.begin(),
                                   g_msStartsCache.end(),
                                   candidate);
        return it != g_msStartsCache.end() && *it == candidate;
    }

    // M1e (revised): conservative pointer scan over a bump arena. For every
    // 8-byte-aligned word in [arena.start + 64, arena.bump), interpret the
    // word as a pointer and, if it points exactly at an MS object start,
    // mark+enqueue that object. This closes the cross-region soundness
    // gap: an MS object whose only root is a perm-arena field
    // (e.g. a static cache) gets marked before sweep.
    //
    // Why conservative scan rather than CGCDesc-based ref walk?
    //   * The perm arena contains arbitrary runtime-internal types whose
    //     CGCDesc layouts our naive walker mishandles (the previous
    //     header-parsing walker crashed mid-arena on a real object).
    //   * Conservative scan trades a small over-approximation (a non-
    //     pointer value coincidentally matching an MS object address keeps
    //     that object alive an extra cycle) for full robustness — no
    //     header parsing required.
    //   * Cost is O(arena_size / 8); ~2.5M reads + log-N probes for a
    //     20 MB arena, well under tens of ms per collection. Card-table
    //     write barriers can later reduce this to O(dirty_cards).
    //
    // Pre-conditions:
    //   * Caller holds g_marksweep.lock.
    //   * EE is suspended.
    //   * ms_build_object_start_index() has been invoked for this collection.
    //   * MT-parsing of perm objects is intentionally NOT used here.
    static void ms_walk_arena_for_external_refs(Arena& arena,
                                                uint8_t* startOverride = nullptr)
    {
        constexpr size_t kStartPadding = 64; // matches GC_Initialize layout
        if (arena.start == nullptr) return;
        uint8_t* p = (startOverride != nullptr)
                         ? startOverride
                         : (arena.start + kStartPadding);
        uint8_t* end = arena.bump;
        // Align p down to 8 bytes (it should already be 8-aligned because
        // kStartPadding is 64; defensive in case GC_Initialize ever changes).
        p = reinterpret_cast<uint8_t*>(
                reinterpret_cast<uintptr_t>(p) & ~uintptr_t(7));
        if (p >= end) return;

        size_t scanned = 0;
        size_t marked  = 0;
        // Quick reject: if no MS objects exist, the scan can't mark anything.
        if (g_msStartsCache.empty())
        {
            LOG1("ms_walk_arena[conservative]: start=%p bump=%p "
                 "MS index empty - skipping",
                 arena.start, arena.bump);
            return;
        }
        uint8_t* msLo = g_msStartsCache.front();
        uint8_t* msHi = g_msStartsCache.back();

        while (p + sizeof(uint8_t*) <= end)
        {
            uint8_t* candidate = *reinterpret_cast<uint8_t**>(p);
            // Cheap range filter avoids most lower_bound calls.
            if (candidate >= msLo && candidate <= msHi
                && ms_is_object_start(candidate))
            {
                if (ms_test_and_set_mark(candidate))
                {
                    g_grayQueue.push_back(candidate);
                    ++marked;
                }
            }
            ++scanned;
            p += sizeof(uint8_t*);
        }
        LOG1("ms_walk_arena[conservative]: start=%p bump=%p "
             "scanned=%zu marked=%zu",
             arena.start, arena.bump, scanned, marked);
    }

    // Callback for IGCToCLR::GcEnumAllocContexts. Encodes any abandoned chunk
    // tail as a free object filler and zeros combined_limit / alloc_ptr /
    // alloc_limit so the runtime's fast path will fall back into our slow-path
    // Alloc on the next allocation.
    //
    // Used during STW collection (simplegc_force_collect) to ensure every
    // thread's cached chunk is rendered linearly walkable before we walk the
    // arenas for cross-region references.
    static void LOCALGC_CALLCONV simplegc_gc_fix_alloc_context_cb(
        gc_alloc_context* acontext, void*)
    {
        if (acontext == nullptr) return;
        arena_fill_chunk_tail(acontext);
        uint8_t** combinedLimit = reinterpret_cast<uint8_t**>(
            reinterpret_cast<uint8_t*>(acontext) - sizeof(uint8_t*));
        *combinedLimit = nullptr;
        acontext->alloc_ptr   = nullptr;
        acontext->alloc_limit = nullptr;
    }

    // Sweep: linear walk of [start_obj, bump). Unmarked objects become free
    // slots; we coalesce adjacent dead slots into one free object before
    // pushing onto the freelist.
    //
    // Returns the live byte total (sum of sizes of marked objects, including
    // the size of pre-existing free objects we passed through unchanged).
    static uint64_t ms_sweep_locked()
    {
        // Reset freelist; we rebuild it from the swept image. Bytes moved into
        // it are accounted via ms_freelist_push.
        g_marksweep.free_head = nullptr;
        g_marksweep.bytes_freelist.store(0, std::memory_order_relaxed);

        uint8_t* p   = g_marksweep.start_obj;
        uint8_t* end = g_marksweep.bump;
        uint64_t live_bytes  = 0;
        uint64_t dead_bytes  = 0;
        uint64_t swept_count = 0;

        while (p < end)
        {
            MethodTable* mt = *reinterpret_cast<MethodTable**>(p);
            if (mt == nullptr)
            {
                // Unallocated tail (shouldn't happen pre-bump). Stop.
                break;
            }

            size_t size;
            bool is_free_filler = (mt == g_gc_pFreeObjectMethodTable);
            if (is_free_filler)
            {
                size = ms_read_free_obj_size(p);
            }
            else
            {
                size = simplegc_obj_size(mt, p);
            }

            if (size < sizeof(void*) || p + size > end)
            {
                LOG1("sweep: corrupt header at %p (mt=%p size=%zu) — abort", p, mt, size);
                break;
            }

            // Coalesce a run of dead/free spans starting at p so the freelist
            // gets one big slot instead of many small ones.
            bool start_dead = is_free_filler ||
                              (g_marksweep.mark_bits[ms_bit_index(p) >> 3] &
                               (1u << (ms_bit_index(p) & 7))) == 0;
            if (start_dead)
            {
                size_t run = size;
                if (!is_free_filler)
                {
                    swept_count++;
                    dead_bytes += size;
                }
                uint8_t* q = p + size;
                while (q < end)
                {
                    MethodTable* nmt = *reinterpret_cast<MethodTable**>(q);
                    if (nmt == nullptr) break;
                    bool n_is_free = (nmt == g_gc_pFreeObjectMethodTable);
                    size_t nsize = n_is_free ? ms_read_free_obj_size(q)
                                             : simplegc_obj_size(nmt, q);
                    if (nsize < sizeof(void*) || q + nsize > end) break;
                    bool n_marked = !n_is_free &&
                                    (g_marksweep.mark_bits[ms_bit_index(q) >> 3] &
                                     (1u << (ms_bit_index(q) & 7))) != 0;
                    if (n_marked) break;
                    if (!n_is_free)
                    {
                        swept_count++;
                        dead_bytes += nsize;
                    }
                    run += nsize;
                    q += nsize;
                }
                // M1l: bump-reset.
                //
                // If this dead run extends all the way to the bump pointer,
                // every byte from `p` onward is reclaimable. Rewind bump
                // instead of pushing onto the freelist.
                //
                // Cache-locality benefit: chunks taken after this collect
                // bump-allocate from `p` (warm, recently-touched memory)
                // instead of pushing further into cold pages near the
                // committed high-water mark. On Fortunes (Kestrel), our
                // walk-arena cost scales with `bump - start_obj`; rewind
                // also makes future walks cheaper.
                //
                // Memory hygiene: zero the rewound tail. ms_raw_bump does
                // NOT zero its return (it relies on VirtualCommit returning
                // zero pages on first commit), so without this memset, a
                // subsequent ms_raw_bump in the rewound region would hand
                // out stale post-sweep memory to the runtime.
                //
                // Safe under STW: this runs in force_collect after
                // SuspendEE. All alloc contexts have been "fixed"
                // (alloc_ptr == alloc_limit) so no thread holds a chunk
                // overlapping the rewound region.
                if (q == end)
                {
                    memset(p, 0, run);
                    g_marksweep.bump = p;
                    g_marksweep.bytes_bump_rewound.fetch_add(run, std::memory_order_relaxed);
                    break;
                }
                if (run >= ms_free_object_min_size())
                {
                    ms_freelist_push(p, run);
                }
                p += run;
            }
            else
            {
                // Live object — leave in place.
                live_bytes += size;
                p += size;
            }
        }

        g_marksweep.bytes_live_after_collect.store(live_bytes, std::memory_order_relaxed);
        g_marksweep.bytes_collected_total.fetch_add(dead_bytes, std::memory_order_relaxed);
        g_marksweep.n_objects_swept.fetch_add(swept_count, std::memory_order_relaxed);
        g_marksweep.n_collections.fetch_add(1, std::memory_order_relaxed);
        return live_bytes;
    }
} // namespace


// ---------------------------------------------------------------------------
// SimpleHandleStore / SimpleHandleManager
// ---------------------------------------------------------------------------
//
// We back handles with a vector<Slot> where each slot is a pair
// (primary, secondary). The OBJECTHANDLE is the address of the primary; the
// secondary lives at handle + sizeof(Object*) and is only used by dependent
// handles (ConditionalWeakTable, Monitor's per-object Conditions, etc.).
// Non-dependent handles leave secondary == nullptr.
//
// This pair-per-slot layout costs 8 bytes per non-dependent handle but is
// the simplest design that lets us implement Get/SetDependentHandleSecondary
// correctly without a parallel side table.

namespace simplegc_handles
{
    constexpr size_t kBucketSize = 4096;

    struct Slot
    {
        Object* primary;
        Object* secondary;
    };

    struct Bucket
    {
        Slot slots[kBucketSize];
        Bucket() { memset(slots, 0, sizeof(slots)); }
    };

    std::mutex               g_handleLock;
    std::vector<Bucket*>     g_buckets;
    size_t                   g_nextSlot = 0; // grows monotonically; we never reclaim

    static OBJECTHANDLE alloc_slot(Object* primary, Object* secondary = nullptr)
    {
        std::lock_guard<std::mutex> guard(g_handleLock);
        size_t bucketIdx = g_nextSlot / kBucketSize;
        size_t inIdx     = g_nextSlot % kBucketSize;
        if (bucketIdx >= g_buckets.size())
        {
            g_buckets.push_back(new Bucket());
        }
        Bucket* b = g_buckets[bucketIdx];
        b->slots[inIdx].primary   = primary;
        b->slots[inIdx].secondary = secondary;
        OBJECTHANDLE h = reinterpret_cast<OBJECTHANDLE>(&b->slots[inIdx].primary);
        ++g_nextSlot;
        return h;
    }

    // Given an OBJECTHANDLE pointing at the primary slot, return a pointer
    // to the matching secondary slot (immediately after the primary in the
    // Slot pair). Safe because each Slot is a contiguous (primary,secondary)
    // pair and the handle is the address of the primary.
    static Object** secondary_slot(OBJECTHANDLE h)
    {
        return reinterpret_cast<Object**>(reinterpret_cast<uint8_t*>(h) + sizeof(Object*));
    }
}

// Definition of ms_scan_handle_store (declared above in the unnamed namespace
// containing the other ms_* helpers). Both primary and secondary slots are
// treated as strong references into our region. For dependent handles this is
// a conservative over-approximation (the secondary should logically only be
// kept alive while the primary is reachable); we accept the small extra
// retention in exchange for simplicity and to guarantee CWT-stored values
// (e.g., Monitor's per-object Conditions) stay alive as long as anyone is
// using the table.
namespace
{
    void ms_scan_handle_store()
    {
        std::lock_guard<std::mutex> guard(simplegc_handles::g_handleLock);
        size_t total = simplegc_handles::g_nextSlot;
        for (size_t i = 0; i < total; ++i)
        {
            size_t bucketIdx = i / simplegc_handles::kBucketSize;
            size_t inIdx     = i % simplegc_handles::kBucketSize;
            simplegc_handles::Slot& slot =
                simplegc_handles::g_buckets[bucketIdx]->slots[inIdx];
            Object* refs[2] = { slot.primary, slot.secondary };
            for (Object* obj : refs)
            {
                if (obj == nullptr) continue;
                uint8_t* p = reinterpret_cast<uint8_t*>(obj);
                if (!ms_in_region(p)) continue;
                if (ms_test_and_set_mark(p))
                {
                    g_grayQueue.push_back(p);
                }
            }
        }
    }
}

class SimpleHandleStore : public IGCHandleStore
{
public:
    void Uproot() override { TRACE_METHOD(); }
    bool ContainsHandle(OBJECTHANDLE) override { TRACE_METHOD(); return true; }
    OBJECTHANDLE CreateHandleOfType(Object* obj, HandleType) override
    { TRACE_METHOD(); return simplegc_handles::alloc_slot(obj); }
    OBJECTHANDLE CreateHandleOfType(Object* obj, HandleType, int) override
    { TRACE_METHOD(); return simplegc_handles::alloc_slot(obj); }
    OBJECTHANDLE CreateHandleWithExtraInfo(Object* obj, HandleType, void*) override
    { TRACE_METHOD(); return simplegc_handles::alloc_slot(obj); }
    OBJECTHANDLE CreateDependentHandle(Object* primary, Object* secondary) override
    { TRACE_METHOD(); return simplegc_handles::alloc_slot(primary, secondary); }
};

static SimpleHandleStore* g_globalHandleStore = nullptr;

class SimpleHandleManager : public IGCHandleManager
{
public:
    bool Initialize() override { TRACE_METHOD(); g_globalHandleStore = new SimpleHandleStore(); return true; }
    void Shutdown() override { TRACE_METHOD(); }

    IGCHandleStore* GetGlobalHandleStore() override { TRACE_METHOD(); return g_globalHandleStore; }
    IGCHandleStore* CreateHandleStore() override { TRACE_METHOD(); return new SimpleHandleStore(); }
    void DestroyHandleStore(IGCHandleStore* store) override { TRACE_METHOD(); delete store; }

    OBJECTHANDLE CreateGlobalHandleOfType(Object* obj, HandleType) override
    { TRACE_METHOD(); return simplegc_handles::alloc_slot(obj); }

    OBJECTHANDLE CreateDuplicateHandle(OBJECTHANDLE handle) override
    {
        TRACE_METHOD();
        Object* obj = *reinterpret_cast<Object**>(handle);
        return simplegc_handles::alloc_slot(obj);
    }

    void DestroyHandleOfType(OBJECTHANDLE handle, HandleType) override
    {
        TRACE_METHOD();
        if (handle != nullptr)
        {
            *reinterpret_cast<Object**>(handle) = nullptr;
            *simplegc_handles::secondary_slot(handle) = nullptr;
        }
    }
    void DestroyHandleOfUnknownType(OBJECTHANDLE handle) override
    {
        TRACE_METHOD();
        if (handle != nullptr)
        {
            *reinterpret_cast<Object**>(handle) = nullptr;
            *simplegc_handles::secondary_slot(handle) = nullptr;
        }
    }

    void  SetExtraInfoForHandle(OBJECTHANDLE, HandleType, void*) override { TRACE_METHOD(); }
    void* GetExtraInfoFromHandle(OBJECTHANDLE) override { TRACE_METHOD(); return nullptr; }

    void StoreObjectInHandle(OBJECTHANDLE handle, Object* obj) override
    {
        TRACE_METHOD();
        *reinterpret_cast<Object**>(handle) = obj;
    }
    bool StoreObjectInHandleIfNull(OBJECTHANDLE handle, Object* obj) override
    {
        TRACE_METHOD();
        Object** slot = reinterpret_cast<Object**>(handle);
        if (*slot == nullptr) { *slot = obj; return true; }
        return false;
    }
    void SetDependentHandleSecondary(OBJECTHANDLE handle, Object* secondary) override
    { TRACE_METHOD(); *simplegc_handles::secondary_slot(handle) = secondary; }
    Object* GetDependentHandleSecondary(OBJECTHANDLE handle) override
    { TRACE_METHOD(); return *simplegc_handles::secondary_slot(handle); }

    Object* InterlockedCompareExchangeObjectInHandle(OBJECTHANDLE handle, Object* obj, Object* comparand) override
    {
        TRACE_METHOD();
        Object** slot = reinterpret_cast<Object**>(handle);
        // Single-threaded approximation; good enough for hello world bring-up.
        Object* cur = *slot;
        if (cur == comparand) *slot = obj;
        return cur;
    }
    HandleType HandleFetchType(OBJECTHANDLE) override { TRACE_METHOD(); return HNDTYPE_DEFAULT; }
    void TraceRefCountedHandles(HANDLESCANPROC, uintptr_t, uintptr_t) override { TRACE_METHOD(); }
};

// ---------------------------------------------------------------------------
// SimpleGCHeap — IGCHeap implementation
// ---------------------------------------------------------------------------
//
// IGCHeapInternal extends IGCHeap with a few internal-use methods. The runtime
// only interacts with us through IGCHeap, so we just inherit IGCHeap directly.
// `g_theGCHeap` is typed `IGCHeapInternal*`, but standalone GCs cast through
// it; we expose ourselves as IGCHeap and only set `g_theGCHeap` for internal
// forwarders that don't apply to this GC.

// Forward declaration so SimpleGCHeap::GarbageCollect can call it.
HRESULT simplegc_force_collect();

class SimpleGCHeap : public IGCHeapInternal
{
public:
    // ---- IGCHeapInternal extras (4 methods beyond IGCHeap) -------------
    int    GetNumberOfHeaps()                    override { return 1; }
    int    GetHomeHeapNumber()                   override { return 0; }
    size_t GetPromotedBytes(int /*heap_index*/)  override { return 0; }
    bool   IsPromoted2(Object*, bool)            override { return true; }

    // ---- Hosting APIs (override the concrete IGCHeapInternal versions) -
    bool   IsValidSegmentSize(size_t)                override { TRACE_METHOD(); return true; }
    bool   IsValidGen0MaxSize(size_t)                override { TRACE_METHOD(); return true; }
    size_t GetValidSegmentSize(bool /*large_seg*/)   override { TRACE_METHOD(); return 16 * 1024 * 1024; }
    void   SetReservedVMLimit(size_t)                override { TRACE_METHOD(); }

    // ---- Concurrent GC -------------------------------------------------
    void    WaitUntilConcurrentGCComplete()                       override { TRACE_METHOD(); }
    bool    IsConcurrentGCInProgress()                            override { TRACE_METHOD(); return false; }
    void    TemporaryEnableConcurrentGC()                         override { TRACE_METHOD(); }
    void    TemporaryDisableConcurrentGC()                        override { TRACE_METHOD(); }
    bool    IsConcurrentGCEnabled()                               override { TRACE_METHOD(); return false; }
    HRESULT WaitUntilConcurrentGCCompleteAsync(int)               override { TRACE_METHOD(); return S_OK; }

    // ---- Finalization --------------------------------------------------
    size_t   GetNumberOfFinalizable() override { TRACE_METHOD(); return 0; }
    Object*  GetNextFinalizable()     override { TRACE_METHOD(); return nullptr; }

    // ---- BCL routines --------------------------------------------------
    void GetMemoryInfo(uint64_t* highMemLoadThresholdBytes,
                       uint64_t* totalAvailableMemoryBytes,
                       uint64_t* lastRecordedMemLoadBytes,
                       uint64_t* lastRecordedHeapSizeBytes,
                       uint64_t* lastRecordedFragmentationBytes,
                       uint64_t* totalCommittedBytes,
                       uint64_t* promotedBytes,
                       uint64_t* pinnedObjectCount,
                       uint64_t* finalizationPendingCount,
                       uint64_t* index,
                       uint32_t* generation,
                       uint32_t* pauseTimePct,
                       bool*     isCompaction,
                       bool*     isConcurrent,
                       uint64_t* /*genInfoRaw*/,
                       uint64_t* /*pauseInfoRaw*/,
                       int /*kind*/) override
    {
        TRACE_METHOD();
        if (highMemLoadThresholdBytes)     *highMemLoadThresholdBytes = 90;
        if (totalAvailableMemoryBytes)     *totalAvailableMemoryBytes = kHeapSize;
        if (lastRecordedMemLoadBytes)      *lastRecordedMemLoadBytes = 0;
        if (lastRecordedHeapSizeBytes)     *lastRecordedHeapSizeBytes = g_totalAllocated.load();
        if (lastRecordedFragmentationBytes)*lastRecordedFragmentationBytes = 0;
        if (totalCommittedBytes)           *totalCommittedBytes = (size_t)((g_perm.committed - g_perm.start) + (g_request.committed - g_request.start) + (g_norefsPerm.committed - g_norefsPerm.start));
        if (promotedBytes)                 *promotedBytes = 0;
        if (pinnedObjectCount)             *pinnedObjectCount = 0;
        if (finalizationPendingCount)      *finalizationPendingCount = 0;
        if (index)                         *index = 0;
        if (generation)                    *generation = 0;
        if (pauseTimePct)                  *pauseTimePct = 0;
        if (isCompaction)                  *isCompaction = false;
        if (isConcurrent)                  *isConcurrent = false;
    }

    uint32_t GetMemoryLoad()                  override { TRACE_METHOD(); return 0; }
    int      GetGcLatencyMode()               override { TRACE_METHOD(); return 0; }
    int      SetGcLatencyMode(int)            override { TRACE_METHOD(); return 0; }
    int      GetLOHCompactionMode()           override { TRACE_METHOD(); return 0; }
    void     SetLOHCompactionMode(int)        override { TRACE_METHOD(); }
    bool     RegisterForFullGCNotification(uint32_t, uint32_t) override { TRACE_METHOD(); return false; }
    bool     CancelFullGCNotification()       override { TRACE_METHOD(); return false; }
    int      WaitForFullGCApproach(int)       override { TRACE_METHOD(); return 0; }
    int      WaitForFullGCComplete(int)       override { TRACE_METHOD(); return 0; }

    unsigned WhichGeneration(Object*)         override { return 0; }
    int      CollectionCount(int, int)        override { return (int)g_gcCount.load(); }
    int      StartNoGCRegion(uint64_t, bool, uint64_t, bool) override { TRACE_METHOD(); return 0; }
    int      EndNoGCRegion()                  override { TRACE_METHOD(); return 0; }
    size_t   GetTotalBytesInUse()             override { return g_totalAllocated.load(); }
    uint64_t GetTotalAllocatedBytes()         override { return g_totalAllocated.load(); }

    HRESULT GarbageCollect(int /*generation*/, bool /*low_memory_p*/, int /*mode*/) override
    {
        TRACE_METHOD();
        g_gcCount.fetch_add(1, std::memory_order_relaxed);
        // Only the mark-sweep region is collectible. Perm and request are not
        // touched here — request memory is reclaimed via simplegc_request_end.
        return simplegc_force_collect();
    }

    unsigned GetMaxGeneration()               override { return 2; }
    void     SetFinalizationRun(Object*)      override { TRACE_METHOD(); }
    bool     RegisterForFinalization(int, Object*) override { TRACE_METHOD(); return true; }
    int      GetLastGCPercentTimeInGC()       override { return 0; }
    size_t   GetLastGCGenerationSize(int)     override { return 0; }

    // ---- Initialization / state -----------------------------------------
    HRESULT Initialize() override
    {
        TRACE_METHOD();
        if (!simplegc_init_heap())
            return E_OUTOFMEMORY;

        // Tell the EE about the heap range so JIT'd write barriers know our bounds.
        WriteBarrierParameters args = {};
        args.operation              = WriteBarrierOp::Initialize;
        args.is_runtime_suspended   = true;
        args.requires_upper_bounds_check = false;
        args.card_table             = g_gc_card_table;
#ifdef FEATURE_MANUALLY_MANAGED_CARD_BUNDLES
        args.card_bundle_table      = g_gc_card_bundle_table;
#endif
        args.lowest_address         = g_gc_lowest_address;
        args.highest_address        = g_gc_highest_address;
        args.ephemeral_low          = g_gc_lowest_address;
        args.ephemeral_high         = g_gc_highest_address;
        args.write_watch_table      = nullptr;
        if (g_theGCToCLR != nullptr)
        {
            g_theGCToCLR->StompWriteBarrier(&args);
        }

        // Cache the runtime-supplied free-object MethodTable.
        if (g_theGCToCLR != nullptr)
        {
            g_freeObjMT = ::GCToEEInterface::GetFreeObjectMethodTable();
            g_gc_pFreeObjectMethodTable = g_freeObjMT;
        }

        LOG1("Initialize complete");
        return S_OK;
    }

    bool     IsPromoted(Object*) override { return true; } // pretend everything is "promoted"
    bool     IsHeapPointer(void* p, bool /*small_heap_only*/) override
    {
        if (p >= g_heapStart && p < g_heapEnd)
            return true;
        // Also report TRUE for any frozen segment registered with us.
        std::lock_guard<std::mutex> guard(g_frozenLock);
        for (const auto& seg : g_frozenSegments)
        {
            if (p >= seg.lo && p < seg.hi)
                return true;
        }
        return false;
    }
    unsigned GetCondemnedGeneration() override { return 0; }
    bool     IsGCInProgressHelper(bool) override { return false; }
    unsigned GetGcCount() override { return (unsigned)g_gcCount.load(); }
    bool     IsThreadUsingAllocationContextHeap(gc_alloc_context*, int) override { return true; }
    bool     IsEphemeral(Object*) override { return true; }
    uint32_t WaitUntilGCComplete(bool) override { TRACE_METHOD(); return S_OK; }
    void     FixAllocContext(gc_alloc_context* acontext, void*, void*) override
    {
        TRACE_METHOD();
        if (acontext == nullptr) return;
        // M1e: encode the abandoned chunk tail as a free object so the perm/
        // request arenas remain linearly walkable for the cross-region mark
        // walk. Must run BEFORE we zero the pointers below. We deliberately
        // do NOT call simplegc_attribute_pending here — t_chunkStart/t_chunkEnd
        // are thread-local to whoever last called Alloc(), which is not
        // necessarily the thread whose context we're being asked to fix.
        arena_fill_chunk_tail(acontext);
        // Reset the alloc context so subsequent allocations refill from the bump
        // pointer (or hit the route-aware slow path). Per the IGCToCLR contract
        // for GcEnumAllocContexts, the legal modification is setting alloc_ptr
        // and alloc_limit to zero; we additionally zero combined_limit (the
        // ee_alloc_context field 8 bytes before the gc_alloc_context) to keep
        // the runtime's fast path from short-circuiting on a stale value.
        uint8_t** combinedLimit = reinterpret_cast<uint8_t**>(
            reinterpret_cast<uint8_t*>(acontext) - sizeof(uint8_t*));
        *combinedLimit = nullptr;
        acontext->alloc_ptr   = nullptr;
        acontext->alloc_limit = nullptr;
    }
    size_t   GetCurrentObjSize() override { return g_totalAllocated.load(); }
    void     SetGCInProgress(bool) override { /* no-op */ }
    bool     RuntimeStructuresValid() override { return true; }
    void     SetSuspensionPending(bool fSuspensionPending) override
    {
        g_fSuspensionPending = fSuspensionPending ? 1 : 0;
    }
    void     SetYieldProcessorScalingFactor(float) override { /* no-op */ }
    void     Shutdown() override { TRACE_METHOD(); }

    // ---- Memory pressure / timing --------------------------------------
    size_t GetLastGCStartTime(int) override { return 0; }
    size_t GetLastGCDuration(int) override  { return 0; }
    size_t GetNow() override
    {
        return (size_t)GCToOSInterface::GetLowPrecisionTimeStamp();
    }

    // ---- Allocation ----------------------------------------------------
    Object* Alloc(gc_alloc_context* acontext, size_t size, uint32_t flags) override
    {
        // ---- M1i.2: align size to 8 BEFORE any other work ----
        // The runtime's gchelpers slow path passes the *raw* object size to
        // IGCHeap::Alloc (e.g. for byte[29], size = 24 + 29 = 53). The default
        // GC aligns it internally (gc_heap::allocate, allocation.cpp:4557).
        // We must do the same — otherwise the bump path's
        //   acontext->alloc_ptr = chunk + size
        // produces a non-8-aligned alloc_ptr, and every subsequent fast-path
        // allocation from that chunk is misaligned. That breaks any code that
        // assumes 8-aligned objects, including SSE2/AVX vmovdqa moves emitted
        // for things like Latin1Utility.WidenLatin1ToUtf16_Sse2.
        //
        // The mark-sweep path below already aligns separately; aligning here
        // is harmless for it (alignedSize = (size + 7) & ~7 idempotent on an
        // already-aligned size).
        size = (size + 7u) & ~static_cast<size_t>(7);

        // ---- M1a: attribute the chunk we're about to abandon ----
        // The previous chunk (if any) has been bump-filled by fast path; its
        // objects' MTs are now written. Walk and bin them before we refill.
        simplegc_attribute_pending(acontext);

        // M1e: encode the abandoned chunk tail as a free-object filler so
        // the perm/request arena remains linearly walkable. Must run AFTER
        // attribute_pending (which reads alloc_ptr) and BEFORE we hand out
        // a fresh chunk (which overwrites alloc_ptr/alloc_limit). Skipping
        // this leaves NULL MT bytes in the arena and breaks the cross-region
        // mark walk.
        arena_fill_chunk_tail(acontext);

        // ---- Phase 3 strategy hook: consult BEFORE we reserve any space ----
        //
        // We must do this *before* simplegc_raw_alloc because once we hand
        // back a chunk pointer the runtime treats it as a partially-constructed
        // object. Calling back into managed from there would be unsafe if the
        // managed code ever triggers a real GC.
        //
        // Re-entrancy: the callback may itself trigger Alloc (e.g. JIT prestub,
        // type init). We block recursive consults via a thread-local flag.
        static thread_local bool s_inConsult = false;
        if (!s_inConsult)
        {
            auto fn = g_shouldCollect.load(std::memory_order_acquire);
            if (fn != nullptr)
            {
                uint64_t total = g_totalAllocated.load(std::memory_order_relaxed);
                uint64_t last  = g_bytesAtLastConsult.load(std::memory_order_relaxed);
                if (total >= last + kConsultThreshold)
                {
                    if (g_bytesAtLastConsult.compare_exchange_strong(last, total))
                    {
                        s_inConsult = true;
                        SimpleGCStats stats =
                        {
                            total,
                            g_requestedBytes.load(std::memory_order_relaxed),
                            g_objectCount.load(std::memory_order_relaxed),
                            g_gcCount.load(std::memory_order_relaxed),
                            total - last
                        };

                        // Transition to preemptive mode while running managed
                        // code (per IGCToCLR contract for calling back into
                        // managed from within GC code).
                        bool toggled = GCToEEInterface::EnablePreemptiveGC();
                        int result = fn(&stats);
                        if (toggled) GCToEEInterface::DisablePreemptiveGC();

                        g_consultCount.fetch_add(1, std::memory_order_relaxed);
                        if (result != 0)
                        {
                            g_strategyApprovedGCs.fetch_add(1, std::memory_order_relaxed);
                            // We don't actually collect yet; bumping g_gcCount
                            // here would lie to CollectionCount. The strategy
                            // approval is reported separately via telemetry.
                        }
                        s_inConsult = false;
                    }
                }
            }
        }

        // ---- M1j.3: cap-aware auto-collect ----
        //
        // When SIMPLEGC_AUTO_COLLECT_MB=N is set at init, force_collect
        // is triggered from here once mark-sweep committed bytes cross
        // the threshold. Throttled by kAutoCollectThrottleBytes so we
        // don't hammer collect on every slow-path call once we're over.
        //
        // Mark-sweep is the only region that gets collected — perm and
        // norefsPerm grow forever; request rewinds via app brackets. So
        // we threshold on MS committed bytes specifically. Pair this
        // with SIMPLEGC_DEFAULT_ROUTE=marksweep so allocations actually
        // land in the collectible region.
        //
        // Re-uses s_inConsult as a re-entrancy guard so a callback into
        // managed code triggered by force_collect (e.g. via root scan)
        // doesn't recursively trigger another collect.
        //
        // Locks: NONE held at this point. Safe to run force_collect (it
        // takes g_marksweep.lock and SuspendEE-s internally).
        uint64_t threshold = g_autoCollectThresholdBytes.load(std::memory_order_relaxed);
        if (threshold != 0 && !s_inConsult && g_marksweep.start_obj != nullptr)
        {
            uint64_t msCommitted =
                static_cast<uint64_t>(g_marksweep.committed - g_marksweep.start_obj);
            if (msCommitted >= threshold)
            {
                // Throttle: don't re-fire until bytes_allocated has grown by
                // at least the user-configured threshold since the last auto
                // fire. Scales naturally with AutoCollectMb, so users who pick
                // a larger threshold also get less aggressive throttling. With
                // chunk-based MS allocation each slow path bumps bytes_allocated
                // by 8 KB, so anchoring throttle to a fixed 16 MB constant fires
                // far too often under continuous-churn workloads (e.g. Fortunes).
                uint64_t total    = g_marksweep.bytes_allocated.load(std::memory_order_relaxed);
                uint64_t lastAuto = g_bytesAtLastAutoCollect.load(std::memory_order_relaxed);
                uint64_t throttle = threshold > kAutoCollectThrottleBytes
                                        ? threshold
                                        : kAutoCollectThrottleBytes;
                if (total >= lastAuto + throttle &&
                    g_bytesAtLastAutoCollect.compare_exchange_strong(lastAuto, total))
                {
                    s_inConsult = true;
                    LOG1("auto-collect: msCommitted=%zu MB threshold=%zu MB",
                         (size_t)(msCommitted >> 20), (size_t)(threshold >> 20));
                    g_gcCount.fetch_add(1, std::memory_order_relaxed);
                    simplegc_force_collect();
                    g_autoCollectCount.fetch_add(1, std::memory_order_relaxed);
                    s_inConsult = false;
                }
            }
        }

        // Refill the alloc context with a fresh chunk and place this object inside.
        // The runtime fast-path will then bump-allocate from the context until it's
        // exhausted again.
        //
        // Phase 4: route refills through the thread's active arena (perm by
        // default; request when inside simplegc_request_begin/end).
        //
        // M1b: when t_useMarkSweep is set on this thread, route to the
        // mark-sweep region instead. Mark-sweep allocations are SINGLE-OBJECT
        // (alloc_ptr == alloc_limit) so the region stays linearly walkable
        // for sweep. Finalizable / LOH / POH always force perm.
        //
        // Important: certain allocation classes are conceptually long-lived or
        // require special handling that the request-arena rewind can't honor:
        //   * LOH/POH: large/pinned objects shouldn't bleed into a 64 MB arena
        //     and POH objects mustn't move - they shouldn't be rewound either.
        //   * FINALIZE: finalizable objects need F-reachable tracking; rewinding
        //     them would skip the finalizer.
        // Send all of these to perm even when a request bracket is active.
        const uint32_t kAlwaysPermFlags =
            GC_ALLOC_FINALIZE | GC_ALLOC_LARGE_OBJECT_HEAP | GC_ALLOC_PINNED_OBJECT_HEAP;
        bool forcePerm = (flags & kAlwaysPermFlags) != 0;

        // M1c: resolve the EFFECTIVE route for this allocation. Priority:
        //   1. forcePerm flags (LOH/POH/Final)            → kRouteForcePerm
        //   2. per-thread forced route (t_forceRoute)     → that route
        //   3. legacy per-thread t_useMarkSweep flag      → kRouteMarkSweep
        //   4. request-bracket arena (t_activeArena)      → kRouteForceReq
        //   5. global default route (g_defaultRoute)      → that route
        //   6. fallback                                   → kRouteForcePerm
        //
        // Putting the request bracket ahead of the global default preserves
        // the existing semantics for code that uses simplegc_request_begin/end:
        // a request bracket is a per-call-site contract that the caller has
        // already opted into and shouldn't be silently overridden by a
        // process-wide "default to mark-sweep".
        uint8_t effRoute;
        if (forcePerm)
        {
            effRoute = kRouteForcePerm;
        }
        else if (t_forceRoute != kRouteDefault)
        {
            effRoute = t_forceRoute;
        }
        else if (t_useMarkSweep)
        {
            effRoute = kRouteMarkSweep;
        }
        else if (t_activeArena != nullptr)
        {
            effRoute = kRouteForceReq;
        }
        else
        {
            uint8_t globalDef = g_defaultRoute.load(std::memory_order_relaxed);
            effRoute = (globalDef != kRouteDefault) ? globalDef : kRouteForcePerm;
        }

        if (effRoute == kRouteMarkSweep)
        {
            // M1k.1: chunk-based MS allocation (TLAB-style).
            //
            // Earlier (single-object) MS path acquired g_marksweep.lock on
            // every allocation and disabled the JIT fast path by setting
            // alloc_ptr == alloc_limit. Under c=16+ HTTP concurrency, all
            // threads serialized on the mutex on every allocation — capping
            // throughput at ~10k rps even on workloads where the default
            // GC ran at 37k+ rps.
            //
            // Now: take a chunk of max(size, kAllocCtxQuant) + slack bytes
            // and hand it to the alloc context. JIT fast path bumps within
            // the chunk lock-free until it exhausts. Slack tail at chunk
            // end gets encoded as a free filler at GC fix-alloc-context
            // time (arena_fill_chunk_tail), keeping the MS region linearly
            // walkable for ms_build_object_start_index and ms_sweep_locked.
            //
            // M1m: per-thread MS sub-arena fast path. The thread bumps its
            // own private kSubArenaSize-byte sub-region lock-free for chunks;
            // it only acquires g_marksweep.lock when refilling the sub-arena.
            // After every fast-path bump, we maintain a free-object filler at
            // [t_msSubBump..t_msSubEnd) so the linear walker can pass through
            // unbumped sub-arena bytes if STW catches us mid-sub-arena.
            //
            // Sub-arena invalidation: g_msSubGen is incremented under STW
            // (post-sweep, pre-RestartEE). Threads compare cached t_msSubGen
            // against g_msSubGen on every chunk-take and refill on mismatch.
            // This catches sub-arenas whose memory got swept into a freelist
            // slot or eaten by bump-reset rewinder during the just-finished
            // collect.
            //
            // Soundness: the lock-free fast path ONLY hands out a chunk if
            // the post-allocation sub-arena remainder is exactly 0 OR
            // >= ms_free_object_min_size() — same rule as ms_freelist_take.
            // Any other remainder would be unfillable slop and would break
            // the linear walker (which advances by alloc'd object size and
            // would land in zero-bytes thinking it's a real object).
            size_t alignedSize   = (size + 7u) & ~static_cast<size_t>(7);
            size_t baseChunkSize = (alignedSize > kAllocCtxQuant) ? alignedSize
                                                                  : kAllocCtxQuant;
            size_t slack         = arena_fill_slack();
            size_t chunkSize     = baseChunkSize + slack;
            const size_t minFreeSize = ms_free_object_min_size();

            uint8_t* chunk = nullptr;

            // M1m fast path: lock-free bump within per-thread sub-arena.
            // The if-condition checks BOTH:
            //   - sub-arenas are enabled (kSubArenaSize > 0)
            //   - thread's cached gen matches global (no GC since refill)
            //   - sub-arena has space for chunkSize bytes
            //   - post-bump remainder is fillable (0 or >= minFreeSize)
            if (kSubArenaSize > 0)
            {
                uint64_t curGen = g_msSubGen.load(std::memory_order_acquire);
                if (t_msSubGen == curGen && t_msSubBump != nullptr &&
                    t_msSubBump + chunkSize <= t_msSubEnd)
                {
                    size_t remainder = (size_t)((t_msSubEnd - t_msSubBump) - chunkSize);
                    if (remainder == 0 || remainder >= minFreeSize)
                    {
                        chunk = t_msSubBump;
                        t_msSubBump += chunkSize;
                        // Maintain free-object filler at the new sub-arena tail
                        // so the linear walker can pass through unbumped bytes.
                        if (remainder >= minFreeSize)
                        {
                            ms_set_free_obj(t_msSubBump, remainder);
                        }
                        // M1m: zero chunk[0..chunkSize) before handing to runtime.
                        // The chunk may currently hold a stale free-filler header
                        // from a prior `ms_set_free_obj(sub, ...)` call (initial
                        // sub-arena fill OR previous-chunk-take's tail filler that
                        // landed AT this chunk's start). Runtime allocations with
                        // GC_ALLOC_ZEROING_OPTIONAL skip their own zeroing and
                        // expect zero memory. Without this, fields not explicitly
                        // initialized by the constructor (e.g., reference fields
                        // defaulted to null) end up holding the filler size bytes
                        // and AV when dereferenced. ms_freelist_take has the same
                        // memset; the legacy raw_bump path was safe only because
                        // ms_raw_bump returned freshly committed (or M1l-rewound,
                        // explicitly zeroed) memory.
                        memset(chunk, 0, chunkSize);
                        g_msSubArenaTakes.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            }

            // M1k.1 / M1m slow path: under g_marksweep.lock. Either:
            //   (a) sub-arenas are disabled (kSubArenaSize == 0), or
            //   (b) the lock-free fast path missed (gen mismatch, sub-arena
            //       exhausted, or remainder unfillable).
            // Try to refill the sub-arena, then retry the fast path. If the
            // chunk is too big for a sub-arena (large allocation), or refill
            // fails, fall back to direct freelist/raw_bump.
            if (chunk == nullptr)
            {
                using ms_clock = std::chrono::steady_clock;
                auto tLockStart = ms_clock::now();
                std::lock_guard<std::mutex> guard(g_marksweep.lock);

                // Try to refill the sub-arena (if enabled and chunk fits).
                // Note: we may also reach here with a still-valid sub-arena
                // (e.g., remainder unfillable for THIS chunkSize but room for
                // smaller). In that case the refill check is false and we
                // fall through to the direct path.
                if (kSubArenaSize > 0 && chunkSize <= kSubArenaSize)
                {
                    uint64_t curGen = g_msSubGen.load(std::memory_order_relaxed);
                    bool needRefill = (t_msSubGen != curGen) ||
                                      (t_msSubBump == nullptr) ||
                                      (t_msSubBump + chunkSize > t_msSubEnd);
                    if (needRefill)
                    {
                        uint8_t* sub = ms_raw_bump(kSubArenaSize);
                        if (sub != nullptr)
                        {
                            t_msSubBump = sub;
                            t_msSubEnd  = sub + kSubArenaSize;
                            t_msSubGen  = curGen;
                            // Initialize the whole sub-arena as one big filler
                            // so the linear walker can pass through unbumped
                            // bytes if STW catches us before any chunk-take.
                            ms_set_free_obj(sub, kSubArenaSize);
                            g_msSubArenaRefills.fetch_add(1, std::memory_order_relaxed);
                        }
                    }

                    // Try sub-arena bump (now possibly fresh).
                    if (t_msSubGen == curGen && t_msSubBump != nullptr &&
                        t_msSubBump + chunkSize <= t_msSubEnd)
                    {
                        size_t remainder = (size_t)((t_msSubEnd - t_msSubBump) - chunkSize);
                        if (remainder == 0 || remainder >= minFreeSize)
                        {
                            chunk = t_msSubBump;
                            t_msSubBump += chunkSize;
                            if (remainder >= minFreeSize)
                            {
                                ms_set_free_obj(t_msSubBump, remainder);
                            }
                            // M1m: see fast-path comment above.
                            memset(chunk, 0, chunkSize);
                        }
                    }
                }

                // Direct fallback (chunk too big for sub-arena, refill failed,
                // or sub-arenas disabled): freelist + raw_bump.
                if (chunk == nullptr)
                {
                    chunk = ms_freelist_take(chunkSize);
                    if (chunk == nullptr)
                    {
                        chunk = ms_raw_bump(chunkSize);
                    }
                    // Fallback: if a chunk-sized slot/bump isn't available, drop
                    // back to per-object size so we can still allocate against the
                    // tail of bump or a small free-list slot. This mirrors the
                    // pre-M1k.1 single-object behavior under pressure.
                    if (chunk == nullptr && alignedSize < kAllocCtxQuant)
                    {
                        size_t smallChunkSize = alignedSize + slack;
                        chunk = ms_freelist_take(smallChunkSize);
                        if (chunk == nullptr)
                        {
                            chunk = ms_raw_bump(smallChunkSize);
                        }
                        if (chunk != nullptr)
                        {
                            baseChunkSize = alignedSize;
                            chunkSize     = smallChunkSize;
                        }
                    }
                }
                auto tLockEnd = ms_clock::now();
                uint64_t lockUs = (uint64_t)std::chrono::duration_cast<std::chrono::microseconds>(tLockEnd - tLockStart).count();
                g_msSlowLockUs.fetch_add(lockUs, std::memory_order_relaxed);
                g_msSlowCalls.fetch_add(1, std::memory_order_relaxed);
            }
            if (chunk == nullptr)
            {
                LOG1("Alloc[marksweep](size=%zu, flags=0x%x) FAILED", size, flags);
                return nullptr;
            }

            // bytes_allocated drives the auto-collect throttle (16 MB delta
            // between collects) and the policy-demo smart-trigger. Track
            // baseChunkSize per chunk-take regardless of source (bump or
            // freelist) — this represents the in-use portion of the chunk
            // that the runtime will fill with user data via JIT fast path.
            // Both bump and freelist sources represent real "work to do"
            // pressure that the throttle should observe. Excluding `slack`
            // is correct: slack is the free-filler tail.
            //
            // Without this attribution being chunk-sized, the throttle would
            // either:
            //   - undercount (alignedSize) → policy-demo's smart-trigger
            //     would barely fire; auto-promotion never kicks in.
            //   - count only fromBump → after bump fills (tight caps), the
            //     throttle stops, freelist drains without compaction, OOM.
            g_marksweep.bytes_allocated.fetch_add(baseChunkSize, std::memory_order_relaxed);
            g_marksweep.n_objects_allocated.fetch_add(1, std::memory_order_relaxed);
            g_requestedBytes.fetch_add(size, std::memory_order_relaxed);
            g_objectCount.fetch_add(1, std::memory_order_relaxed);

            if (acontext != nullptr)
            {
                t_lastAllocCtx = acontext;
                acontext->alloc_ptr   = chunk + size;
                // Slack-aware: alloc_limit is positioned `slack` bytes BEFORE
                // chunk_end so arena_fill_chunk_tail can always encode the
                // abandoned tail as a g_gc_pFreeObjectMethodTable filler.
                acontext->alloc_limit = chunk + baseChunkSize;
                acontext->alloc_bytes += (int64_t)alignedSize;

                // Track this chunk for post-hoc per-MT attribution. The next
                // slow-path call (or flush) walks [t_chunkStart, alloc_ptr)
                // and increments per-MT alloc counters via mt_walk_chunk.
                // With JIT fast-path bumping the chunk, this captures every
                // object the runtime allocated since chunk-take.
                t_chunkStart = chunk;
                t_chunkEnd   = chunk + baseChunkSize;
            }

            return reinterpret_cast<Object*>(chunk);
        }

        // Bump-pointer (perm / norefsPerm / request) path. Force-route
        // caller-overrides win over the request bracket: kRouteForcePerm
        // always lands in perm even if a request bracket is active.
        //
        // M1g soundness rule: kRouteNoRefsPerm is honored ONLY when the
        // runtime tells us the allocation has no GC pointers
        // (GC_ALLOC_CONTAINS_REF is clear). Otherwise we silently re-route
        // to g_perm. This keeps g_norefsPerm provably free of MS-pointing
        // refs at all times, which lets the M1e cross-region scan skip it
        // entirely.
        if (effRoute == kRouteNoRefsPerm && (flags & GC_ALLOC_CONTAINS_REF))
        {
            effRoute = kRouteForcePerm;
        }
        Arena* targetArenaPtr;
        if (effRoute == kRouteForceReq && t_activeArena != nullptr)
        {
            targetArenaPtr = t_activeArena;
        }
        else if (effRoute == kRouteNoRefsPerm)
        {
            targetArenaPtr = &g_norefsPerm;
        }
        else
        {
            targetArenaPtr = &g_perm;
        }
        Arena& targetArena = *targetArenaPtr;

        size_t baseChunkSize = (size > kAllocCtxQuant) ? size : kAllocCtxQuant;
        // M1e: reserve `slack` bytes at the end of every chunk we hand out.
        // alloc_limit is positioned `slack` bytes before chunk_end, so when
        // the alloc context is later abandoned (refill, flush, fix), the
        // region [alloc_ptr, alloc_limit + slack) is always >= slack bytes
        // and therefore writable as a single g_gc_pFreeObjectMethodTable
        // free-object filler. Without this, gaps of < min_free_obj_size bytes
        // (e.g. 8 or 16) cannot be encoded and the arena loses linear
        // walkability.
        size_t slack = arena_fill_slack();
        size_t chunkSize = baseChunkSize + slack;
        uint8_t* chunk = simplegc_raw_alloc(targetArena, chunkSize);
        if (chunk == nullptr)
        {
            LOG1("Alloc(size=%zu, flags=0x%x) FAILED", size, flags);
            return nullptr;
        }

        // The request arena is rewound between requests; that means a new
        // request reuses memory previously written by an earlier request. The
        // runtime expects allocation contexts to be zero-initialized memory
        // when GC_ALLOC_ZEROING_OPTIONAL is set on the requesting allocation
        // (the runtime will skip its own zeroing in that case). For the perm
        // arena this is automatic (VirtualAlloc gives zeros and we never
        // re-allocate), but for the request arena we must zero stale bytes —
        // including the slack tail, which we may overwrite with a free-object
        // filler later.
        if (&targetArena == &g_request)
        {
            memset(chunk, 0, chunkSize);
        }

        g_requestedBytes.fetch_add(size, std::memory_order_relaxed);

        // Cache the alloc context pointer so request_end can flush it without
        // walking all alloc contexts. Single-threaded benchmark assumption.
        if (acontext != nullptr)
        {
            t_lastAllocCtx = acontext;
        }

        static int s_traceCount = 0;
        if (s_traceCount++ < 20)
        {
            LOG1("Alloc[%d] size=%zu flags=0x%x arena=%s -> %p (acontext=%p)",
                 s_traceCount, size, flags,
                 (&targetArena == &g_request)     ? "request" :
                 (&targetArena == &g_norefsPerm)  ? "norefsperm" :
                                                    "perm",
                 chunk, acontext);
        }

        // Object goes at the beginning of the chunk.
        Object* obj = reinterpret_cast<Object*>(chunk);

        if (acontext != nullptr)
        {
            acontext->alloc_ptr   = chunk + size;
            // alloc_limit is positioned `slack` bytes BEFORE chunk_end so
            // arena_fill_chunk_tail can always encode the abandoned tail.
            acontext->alloc_limit = chunk + baseChunkSize;
            acontext->alloc_bytes += (int64_t)size;
            // Note: ee_alloc_context::combined_limit (offset -8 from acontext)
            // is updated by the runtime's slow path after we return, based on
            // alloc_limit. We don't need to touch it here.

            // M1a: remember this chunk so the next slow-path call (or flush)
            // can walk and attribute it. Includes the new object (which the
            // runtime will fill MT for after we return).
            t_chunkStart = chunk;
            t_chunkEnd   = chunk + baseChunkSize;
        }
        return obj;
    }

    void PublishObject(uint8_t*) override { /* no UOH list to maintain */ }
    void SetWaitForGCEvent() override   { TRACE_METHOD(); }
    void ResetWaitForGCEvent() override { TRACE_METHOD(); }

    // ---- Heap verification --------------------------------------------
    bool   IsLargeObject(Object*) override { return false; }
    void   ValidateObjectMember(Object*) override {}
    Object* NextObj(Object*) override { return nullptr; }
    Object* GetContainingObject(void*, bool) override { return nullptr; }

    // ---- Diagnostic walks (all no-op) ---------------------------------
    void DiagWalkObject(Object*, walk_fn, void*) override {}
    void DiagWalkObject2(Object*, walk_fn2, void*) override {}
    void DiagWalkHeap(walk_fn, void*, int, bool) override {}
    void DiagWalkSurvivorsWithType(void*, record_surv_fn, void*, walk_surv_type, int) override {}
    void DiagWalkFinalizeQueue(void*, fq_walk_fn) override {}
    void DiagScanFinalizeQueue(fq_scan_fn, ScanContext*) override {}
    void DiagScanHandles(handle_scan_fn, int, ScanContext*) override {}
    void DiagScanDependentHandles(handle_scan_fn, int, ScanContext*) override {}
    void DiagDescrGenerations(gen_walk_fn, void*) override {}
    void DiagTraceGCSegments() override {}
    void DiagGetGCSettings(EtwGCSettingsInfo*) override {}

    // ---- Stress -------------------------------------------------------
    bool StressHeap(gc_alloc_context*) override { return false; }

    // ---- Frozen segments (basicfreeze) --------------------------------
    segment_handle RegisterFrozenSegment(segment_info* pseginfo) override
    {
        TRACE_METHOD();
        // The runtime treats nullptr as OOM. Track the segment range so
        // IsHeapPointer correctly reports its addresses as "in heap".
        if (pseginfo == nullptr || pseginfo->pvMem == nullptr)
            return nullptr;
        FrozenSegment seg;
        seg.lo = static_cast<uint8_t*>(pseginfo->pvMem) + pseginfo->ibFirstObject;
        seg.hi = static_cast<uint8_t*>(pseginfo->pvMem) + pseginfo->ibReserved;
        {
            std::lock_guard<std::mutex> guard(g_frozenLock);
            g_frozenSegments.push_back(seg);
        }
        LOG1("RegisterFrozenSegment [%p..%p)", seg.lo, seg.hi);
        return reinterpret_cast<segment_handle>(pseginfo);
    }
    void           UnregisterFrozenSegment(segment_handle) override { TRACE_METHOD(); }
    bool           IsInFrozenSegment(Object* obj) override
    {
        std::lock_guard<std::mutex> guard(g_frozenLock);
        for (const auto& seg : g_frozenSegments)
        {
            if (reinterpret_cast<uint8_t*>(obj) >= seg.lo &&
                reinterpret_cast<uint8_t*>(obj) <  seg.hi)
                return true;
        }
        return false;
    }

    // ---- Events -------------------------------------------------------
    void ControlEvents(GCEventKeyword, GCEventLevel) override {}
    void ControlPrivateEvents(GCEventKeyword, GCEventLevel) override {}
    unsigned int GetGenerationWithRange(Object*, uint8_t** ppStart, uint8_t** ppAllocated, uint8_t** ppReserved) override
    {
        if (ppStart)     *ppStart     = g_heapStart;
        // Allocated high-water across our bump arenas. NOTE: g_norefsPerm
        // is intentionally EXCLUDED here — its base address sits past
        // perm+request+marksweep (~1.5 GB into the reserve) so even an
        // empty norefsPerm would inflate ppAllocated by an order of
        // magnitude. The runtime uses this range for write-barrier card
        // table coverage, and an inflated value perturbs allocator
        // behavior on unrelated paths (observed: it dramatically reduced
        // perm→MS reference visibility under M1e's conservative scan).
        // norefsPerm contains no GC pointers by construction, so leaving
        // it out of the high-water is sound.
        if (ppAllocated) *ppAllocated = (g_request.bump > g_perm.bump) ? g_request.bump : g_perm.bump;
        if (ppReserved)  *ppReserved  = g_heapEnd;
        return 0;
    }

    // ---- Newer additions ----------------------------------------------
    int64_t GetTotalPauseDuration() override { return 0; }
    void    EnumerateConfigurationValues(void*, ConfigurationValueFunc) override {}
    void    UpdateFrozenSegment(segment_handle, uint8_t*, uint8_t*) override {}
    int     RefreshMemoryLimit() override { return 0; }
    enable_no_gc_region_callback_status EnableNoGCRegionCallback(NoGCRegionCallbackFinalizerWorkItem*, uint64_t) override
    {
        return enable_no_gc_region_callback_status::not_started;
    }
    FinalizerWorkItem* GetExtraWorkForFinalization() override { return nullptr; }
    uint64_t GetGenerationBudget(int) override { return 0; }
    size_t   GetLOHThreshold() override { return LARGE_OBJECT_SIZE; }
    void     DiagWalkHeapWithACHandling(walk_fn, void*, int, bool) override {}
    void     NullBridgeObjectsWeakRefs(size_t, void*) override {}
};

static SimpleGCHeap*       g_simpleHeap    = nullptr;
static SimpleHandleManager* g_simpleHandleMgr = nullptr;

// ---------------------------------------------------------------------------
// M1b: STW collection orchestrator (file-scope so simplegc_collect_marksweep
// can call it).
// ---------------------------------------------------------------------------

// Forward declaration: routing-policy callback dispatcher (Step C).
static void simplegc_invoke_routing_policy_post_collect();

HRESULT simplegc_force_collect()
{
    if (g_simpleHeap == nullptr) return S_OK;
    if (g_marksweep.start_obj == nullptr) return S_OK;

    // Single-collect gate. We can NOT use std::mutex here because we
    // need to call SuspendEE without holding any native lock — see
    // g_collectInProgress declaration for rationale.
    bool expected = false;
    if (!g_collectInProgress.compare_exchange_strong(expected, true,
                                                     std::memory_order_acquire))
    {
        // Another thread is already collecting; skip this round. The
        // next allocation that crosses the throttle will retry.
        return S_OK;
    }

    {
        // Suspend the EE so we can scan stacks safely. The runtime guarantees
        // every thread is at a safe point on return. Once suspended, no
        // managed code is running — so g_marksweep allocator state is
        // implicitly serialized for the duration of the collect; no mutex
        // needed.
        GCToEEInterface::SuspendEE(SUSPEND_FOR_GC);

        // M1l measure: per-phase timing instrumentation. Captures wall-clock
        // microseconds for each force_collect phase so we can quantify the
        // bottleneck before picking parallel-mark vs card-table.
        using ms_clock = std::chrono::steady_clock;
        auto t0 = ms_clock::now();

        // M1e: encode every cached chunk's abandoned tail as a free-object
        // filler and zero the alloc-context pointers. After this loop, every
        // byte from start + 64 to bump in g_perm and g_request is either a
        // real object or a parseable free-object filler — i.e. the arenas
        // are linearly walkable for the cross-region mark walk below.
        //
        // GcEnumAllocContexts iterates ALL threads' alloc contexts, so this
        // also fixes the multi-threaded case (threads other than the one
        // that last called Alloc()).
        LOG1("force_collect: phase=fix-alloc-contexts");
        GCToEEInterface::GcEnumAllocContexts(&simplegc_gc_fix_alloc_context_cb,
                                             nullptr);
        auto t1 = ms_clock::now();
        LOG1("force_collect: phase=scan-roots");

        // Reset per-collection survival counters on every populated MT entry. The
        // mark phase will re-populate them. Cumulative counters (count, bytes)
        // are NOT reset here.
        for (size_t i = 0; i < kMtTableCapacity; ++i)
        {
            if (g_mtTable[i].mt.load(std::memory_order_relaxed) != nullptr)
            {
                g_mtTable[i].bytes_survived.store(0, std::memory_order_relaxed);
                g_mtTable[i].count_survived.store(0, std::memory_order_relaxed);
            }
        }

        // Clear mark bits and gray queue.
        memset(g_marksweep.mark_bits, 0, g_marksweep.mark_bits_size);
        g_grayQueue.clear();
        auto t1a = ms_clock::now();   // after mt_table reset + mark-bits memset

        // Collect roots from managed stacks, statics, and runtime-internal sources.
        ScanContext sc{};
        sc.promotion = TRUE;
        sc.concurrent = FALSE;
        GCToEEInterface::GcScanRoots(&ms_promote_callback, /*condemned*/ 2, /*max_gen*/ 2, &sc);
        auto t1b = ms_clock::now();   // after GcScanRoots (runtime-side)

        // Add our own handle store as roots.
        ms_scan_handle_store();
        auto t2 = ms_clock::now();    // after ms_scan_handle_store
        LOG1("force_collect: phase=walk-arenas");

        // M1e: walk the perm and request arenas to discover cross-region
        // references. Any field of a perm/request object that points into
        // the mark-sweep region is a root for sweep purposes — without this
        // walk, MS objects reachable only via long-lived perm state would
        // be incorrectly swept.
        //
        // Implementation: conservative pointer scan. We build a sorted
        // index of MS object starts (one-shot per collection), then read
        // every 8-aligned word in the perm/request arenas and treat any
        // word matching an MS start as a reference. This trades a small
        // over-approximation (false-positive marks keep dead MS objects
        // alive an extra cycle) for full robustness — we never parse the
        // CGCDesc layout of arbitrary perm-arena objects (some of which
        // are runtime-internal types our parser mishandles).
        ms_build_object_start_index();
        uint64_t permWalkBytes    = (uint64_t)(g_perm.bump    - g_perm.start);
        uint64_t requestWalkBytes = (uint64_t)(g_request.bump - g_request.start);
        g_phaseTimes.last_perm_walk_bytes.store(permWalkBytes, std::memory_order_relaxed);
        g_phaseTimes.last_request_walk_bytes.store(requestWalkBytes, std::memory_order_relaxed);
        // M1m.2: when the user opts into perm-immutable, walk only the
        // high-water portion since the last collect. This brings the
        // common case (steady-state perm with default route=marksweep)
        // from 60-100ms/walk to ~0ms/walk.
        //
        // Defensive: if a bump pointer regressed below the watermark (e.g.
        // request rewind via simplegc_request_end), drop the watermark to
        // arena.start so we re-scan everything currently live in the arena.
        // Missing this would skip new allocations under the old high.
        if (g_permImmutableOptIn)
        {
            if (g_permWalkedHigh    > g_perm.bump)    g_permWalkedHigh    = nullptr;
            if (g_requestWalkedHigh > g_request.bump) g_requestWalkedHigh = nullptr;
        }
        uint8_t* permWalkStart    = g_permImmutableOptIn ? g_permWalkedHigh    : nullptr;
        uint8_t* requestWalkStart = g_permImmutableOptIn ? g_requestWalkedHigh : nullptr;
        ms_walk_arena_for_external_refs(g_perm,    permWalkStart);
        ms_walk_arena_for_external_refs(g_request, requestWalkStart);
        if (g_permImmutableOptIn)
        {
            // Record high-watermark for next collect.
            g_permWalkedHigh    = g_perm.bump;
            g_requestWalkedHigh = g_request.bump;
        }
        // M1g: g_norefsPerm is INTENTIONALLY skipped. The substrate refuses
        // any allocation with GC_ALLOC_CONTAINS_REF set into this arena
        // (silently re-routing to g_perm), so by construction norefsPerm
        // contains no fields that could point at MS objects. Skipping the
        // conservative scan here is the optimization that lets the
        // policy's perm-promotion strategy pay off when the cache grows.
        auto t3 = ms_clock::now();
        LOG1("force_collect: phase=drain-gray (norefsPerm skipped: %zu bytes)",
             (size_t)(g_norefsPerm.bump - g_norefsPerm.start));

        // Trace the live closure (also bumps per-MT survival).
        ms_drain_gray_queue();
        auto t4 = ms_clock::now();
        LOG1("force_collect: phase=sweep");

        // Sweep dead objects into the freelist.
        uint64_t live = ms_sweep_locked();
        (void)live;

        // Bump age_collections for every MT that had any survivors this cycle.
        for (size_t i = 0; i < kMtTableCapacity; ++i)
        {
            if (g_mtTable[i].mt.load(std::memory_order_relaxed) != nullptr &&
                g_mtTable[i].count_survived.load(std::memory_order_relaxed) > 0)
            {
                g_mtTable[i].age_collections.fetch_add(1, std::memory_order_relaxed);
            }
        }

        auto t5 = ms_clock::now();

        // Accumulate per-phase microseconds. All deltas are non-negative
        // because we capture timestamps in monotonic order.
        auto us = [](ms_clock::time_point a, ms_clock::time_point b) -> uint64_t {
            return (uint64_t)std::chrono::duration_cast<std::chrono::microseconds>(b - a).count();
        };
        uint64_t fixUs       = us(t0, t1);
        uint64_t scanResetUs = us(t1, t1a);
        uint64_t scanRootsUs = us(t1a, t1b);
        uint64_t scanHandlesUs = us(t1b, t2);
        uint64_t scanUs      = us(t1, t2);
        uint64_t walkUs      = us(t2, t3);
        uint64_t drainUs     = us(t3, t4);
        uint64_t sweepUs     = us(t4, t5);
        uint64_t totalUs     = us(t0, t5);
        g_phaseTimes.fix_alloc_us  .fetch_add(fixUs,   std::memory_order_relaxed);
        g_phaseTimes.scan_roots_us .fetch_add(scanUs,  std::memory_order_relaxed);
        g_phaseTimes.walk_arenas_us.fetch_add(walkUs,  std::memory_order_relaxed);
        g_phaseTimes.drain_gray_us .fetch_add(drainUs, std::memory_order_relaxed);
        g_phaseTimes.sweep_us      .fetch_add(sweepUs, std::memory_order_relaxed);
        g_phaseTimes.total_us      .fetch_add(totalUs, std::memory_order_relaxed);
        g_phaseTimes.collects      .fetch_add(1,       std::memory_order_relaxed);

        // Always log the per-collect breakdown (LOG1) so harnesses can grep
        // it. Includes scanned arena sizes so we can correlate walk time
        // with region size and compute scan throughput. scan-roots is split
        // into reset (mt-table + mark-bits memset) / runtime-side GcScanRoots
        // / our own ms_scan_handle_store.
        // Always log the per-collect breakdown (LOG1) so harnesses can grep
        // it. Includes scanned arena sizes so we can correlate walk time
        // with region size and compute scan throughput. scan-roots is split
        // into reset (mt-table + mark-bits memset) / runtime-side GcScanRoots
        // / our own ms_scan_handle_store.
        uint64_t cumSlowCalls  = g_msSlowCalls .load(std::memory_order_relaxed);
        uint64_t cumSlowLockUs = g_msSlowLockUs.load(std::memory_order_relaxed);
        uint64_t cumRewound    = g_marksweep.bytes_bump_rewound.load(std::memory_order_relaxed);
        uint64_t bumpAfter     = (uint64_t)(g_marksweep.bump - g_marksweep.start_obj);
        uint64_t cumSubTakes   = g_msSubArenaTakes  .load(std::memory_order_relaxed);
        uint64_t cumSubRefills = g_msSubArenaRefills.load(std::memory_order_relaxed);
        LOG1("force_collect: phase_us fix=%llu scan=%llu (reset=%llu rtRoots=%llu handles=%llu) walk=%llu drain=%llu sweep=%llu total=%llu  permBytes=%llu requestBytes=%llu  cumSlowCalls=%llu cumSlowLockUs=%llu (avg=%llu ns)  bumpAfter=%llu cumRewound=%llu  subTakes=%llu subRefills=%llu",
             (unsigned long long)fixUs,
             (unsigned long long)scanUs,
             (unsigned long long)scanResetUs,
             (unsigned long long)scanRootsUs,
             (unsigned long long)scanHandlesUs,
             (unsigned long long)walkUs,  (unsigned long long)drainUs,
             (unsigned long long)sweepUs, (unsigned long long)totalUs,
             (unsigned long long)permWalkBytes,
             (unsigned long long)requestWalkBytes,
             (unsigned long long)cumSlowCalls,
             (unsigned long long)cumSlowLockUs,
             (unsigned long long)(cumSlowCalls ? (cumSlowLockUs * 1000ULL / cumSlowCalls) : 0),
             (unsigned long long)bumpAfter,
             (unsigned long long)cumRewound,
             (unsigned long long)cumSubTakes,
             (unsigned long long)cumSubRefills);

        // M1m: increment the sub-arena generation counter BEFORE RestartEE
        // so threads see the new gen on resume. Sub-arena memory may have
        // been swept into a freelist slot or eaten by bump-reset rewinder
        // during this collect — invalidating cached sub-arena pointers
        // forces threads to refill on their next chunk-take.
        g_msSubGen.fetch_add(1, std::memory_order_release);

        GCToEEInterface::RestartEE(/*bFinishedGC*/ true);
    }   // <-- scope-end (was: release g_marksweep.lock here).

    // Release the single-collect gate now that EE has restarted.
    g_collectInProgress.store(false, std::memory_order_release);

    // Invoke the registered routing-policy callback (if any). Runs OUTSIDE
    // both the suspended-EE bracket AND the marksweep lock — so the
    // callback (and its reverse-P/Invoke transition) may freely allocate
    // (which itself acquires g_marksweep.lock) without recursing on a
    // non-recursive mutex.
    simplegc_invoke_routing_policy_post_collect();

    return S_OK;
}

// ---------------------------------------------------------------------------
// GC <-> EE entry points
// ---------------------------------------------------------------------------

GC_EXPORT
void LOCALGC_CALLCONV
GC_VersionInfo(VersionInfo* info)
{
    // The EE writes the supported version into *info on entry.
    g_runtimeSupportedVersion = *info;
    g_oldMethodTableFlags = (g_runtimeSupportedVersion.MajorVersion < 2);

    info->MajorVersion = GC_INTERFACE_MAJOR_VERSION;
    info->MinorVersion = GC_INTERFACE_MINOR_VERSION;
    info->BuildVersion = 0;
    info->Name         = "SimpleGC (LLM-pluggable)";

    LOG1("GC_VersionInfo: runtime supports %u.%u; we are %u.%u",
         g_runtimeSupportedVersion.MajorVersion,
         g_runtimeSupportedVersion.MinorVersion,
         info->MajorVersion, info->MinorVersion);
}

GC_EXPORT
HRESULT LOCALGC_CALLCONV
GC_Initialize(IGCToCLR* clrToGC,
              IGCHeap** gcHeap,
              IGCHandleManager** gcHandleManager,
              GcDacVars* gcDacVars)
{
    LOG1("GC_Initialize entered");

    if (clrToGC == nullptr || gcHeap == nullptr || gcHandleManager == nullptr || gcDacVars == nullptr)
        return E_POINTER;

    g_theGCToCLR = clrToGC;

    // Initialize OS interface (for VirtualReserve/Commit and timestamps).
    if (!GCToOSInterface::Initialize())
    {
        LOG1("GCToOSInterface::Initialize failed");
        return E_FAIL;
    }

    g_simpleHandleMgr = new (std::nothrow) SimpleHandleManager();
    g_simpleHeap      = new (std::nothrow) SimpleGCHeap();
    if (g_simpleHandleMgr == nullptr || g_simpleHeap == nullptr)
        return E_OUTOFMEMORY;

    *gcHandleManager = g_simpleHandleMgr;
    *gcHeap          = g_simpleHeap;

    // We do not populate any DAC variables — the inspector won't be useful
    // against this GC, but the runtime tolerates a zeroed GcDacVars.
    memset(gcDacVars, 0, sizeof(*gcDacVars));

    // Internal global so that any inline forwarders that consult it can find us.
    g_theGCHeap          = g_simpleHeap;
    g_theGCHandleManager = g_simpleHandleMgr;

    LOG1("GC_Initialize complete");
    return S_OK;
}

// ---------------------------------------------------------------------------
// Phase 3: managed strategy bridge — public C ABI
// ---------------------------------------------------------------------------
//
// The application registers a function pointer for ShouldCollect via the
// simplegc_register_strategy export. The pointer is then called from the
// allocation slow path (BEFORE the next chunk refill) on the managed thread
// after a transition to preemptive GC mode.
//
// Constraints communicated to strategy authors:
//   1. The callback must be a [UnmanagedCallersOnly] static method.
//   2. It must NOT allocate (no new objects, no boxing, no string formatting).
//   3. It must be JIT'd and its containing type's .cctor must have run BEFORE
//      registration (the app should call RuntimeHelpers.PrepareMethod and
//      RuntimeHelpers.RunClassConstructor first).
//   4. It must not throw.
//   5. Returning non-zero approves a collection; we currently log it but
//      don't yet collect (Phase 4).

GC_EXPORT
uint32_t LOCALGC_CALLCONV
simplegc_register_strategy(uint32_t abiVersion,
                           uint32_t structSize,
                           ShouldCollectFn shouldCollect)
{
    if (abiVersion != kStrategyAbiVersion)
    {
        LOG1("simplegc_register_strategy: ABI version mismatch (got %u, expected %u)",
             abiVersion, kStrategyAbiVersion);
        return 0;
    }
    if (structSize != sizeof(SimpleGCStats))
    {
        LOG1("simplegc_register_strategy: struct size mismatch (got %u, expected %zu)",
             structSize, sizeof(SimpleGCStats));
        return 0;
    }

    g_shouldCollect.store(shouldCollect, std::memory_order_release);
    LOG1("simplegc_register_strategy: shouldCollect=%p", shouldCollect);
    return kStrategyAbiVersion;
}

GC_EXPORT
void LOCALGC_CALLCONV
simplegc_get_telemetry(uint64_t* outConsultCount,
                       uint64_t* outApprovedCount,
                       uint64_t* outGcCount,
                       uint64_t* outTotalAllocatedBytes,
                       uint64_t* outRequestedBytes,
                       uint64_t* outObjectCount)
{
    if (outConsultCount       != nullptr) *outConsultCount       = g_consultCount.load(std::memory_order_relaxed);
    if (outApprovedCount      != nullptr) *outApprovedCount      = g_strategyApprovedGCs.load(std::memory_order_relaxed);
    if (outGcCount            != nullptr) *outGcCount            = g_gcCount.load(std::memory_order_relaxed);
    if (outTotalAllocatedBytes!= nullptr) *outTotalAllocatedBytes= g_totalAllocated.load(std::memory_order_relaxed);
    if (outRequestedBytes     != nullptr) *outRequestedBytes     = g_requestedBytes.load(std::memory_order_relaxed);
    if (outObjectCount        != nullptr) *outObjectCount        = g_objectCount.load(std::memory_order_relaxed);
}

// ---------------------------------------------------------------------------
// Phase 4: per-request arena - public C ABI
// ---------------------------------------------------------------------------
//
// The application brackets a unit of work (e.g. a webapi request) with calls
// to simplegc_request_begin() and simplegc_request_end(). All allocations made
// while inside the bracket are routed to the request arena, and request_end
// rewinds the arena's bump pointer in O(1) — every object allocated in the
// bracket is reclaimed simultaneously.
//
// Hard contract for the caller:
//   * No managed reference into request-arena objects must outlive request_end.
//     This means: no static fields, no fields of perm-arena objects, nothing
//     captured by closures that live longer than the request.
//   * For the prototype the caller is responsible for nulling out their own
//     references before request_end. A real integration would enforce this at
//     compile time (the LLM-strategy story) or with runtime escape analysis.
//
// Currently single-threaded: t_activeArena and t_lastAllocCtx are thread-local;
// only the calling thread is affected. Multi-threaded bracket support requires
// per-thread request arenas plus EE suspension on flush — out of scope here.

GC_EXPORT
uint64_t LOCALGC_CALLCONV
simplegc_request_begin()
{
    if (t_activeArena == &g_request)
    {
        // Already inside a request. Nested begins are not supported.
        LOG1("simplegc_request_begin: nested call ignored");
        return 0;
    }

    // Snapshot the current bump position so request_end can rewind to here.
    {
        std::lock_guard<std::mutex> guard(g_request.lock);
        g_request.checkpoint = g_request.bump;
    }
    t_activeArena = &g_request;

    // Force the next allocation to refill from the request arena by zeroing
    // out the cached alloc context's pointers. Without this, the runtime's
    // fast path would keep bumping in whichever chunk the perm arena gave us
    // last.
    // Flush the alloc-context cache (combined_limit + alloc_ptr + alloc_limit)
    // so the very next allocation falls into our slow-path Alloc and refills
    // a fresh chunk from the request arena.
    simplegc_flush_alloc_context(t_lastAllocCtx);

    return (uint64_t)(uintptr_t)g_request.checkpoint;
}

GC_EXPORT
uint64_t LOCALGC_CALLCONV
simplegc_request_end()
{
    if (t_activeArena != &g_request)
    {
        LOG1("simplegc_request_end: no active request");
        return 0;
    }

    uint64_t freedBytes = 0;
    {
        std::lock_guard<std::mutex> guard(g_request.lock);
        freedBytes = (uint64_t)(g_request.bump - g_request.checkpoint);
        // O(1) "collection": rewind bump pointer. All objects above are gone.
        // DEBUG: optionally disable rewind to bisect arena-vs-rewind bugs.
        static bool s_disableRewind = []() {
            const char* v = std::getenv("SIMPLEGC_NO_REWIND");
            return v != nullptr && v[0] == '1';
        }();
        if (!s_disableRewind)
        {
            g_request.bump = g_request.checkpoint;
        }
    }
    t_activeArena = nullptr;

    // Flush the alloc context cache (combined_limit + alloc_ptr + alloc_limit)
    // so the very next allocation falls into our slow-path Alloc. Without
    // this, the runtime's fast path would keep bumping in the freshly-rewound
    // region of the request arena and "allocate" into reclaimed memory.
    simplegc_flush_alloc_context(t_lastAllocCtx);

    g_gcCount.fetch_add(1, std::memory_order_relaxed);
    return freedBytes;
}

GC_EXPORT
void LOCALGC_CALLCONV
simplegc_get_arena_stats(uint64_t* outPermBytesUsed,
                         uint64_t* outPermBytesCommitted,
                         uint64_t* outRequestBytesUsed,
                         uint64_t* outRequestBytesCommitted)
{
    if (outPermBytesUsed)         *outPermBytesUsed         = (uint64_t)(g_perm.bump - g_perm.start);
    if (outPermBytesCommitted)    *outPermBytesCommitted    = (uint64_t)(g_perm.committed - g_perm.start);
    if (outRequestBytesUsed)      *outRequestBytesUsed      = (uint64_t)(g_request.bump - g_request.start);
    if (outRequestBytesCommitted) *outRequestBytesCommitted = (uint64_t)(g_request.committed - g_request.start);
}

// M1g: stats for the no-refs perm sub-arena. Returns the number of bytes
// allocated into and committed by g_norefsPerm — used by the demo to report
// how much of the long-lived heap landed in the optimized arena.
GC_EXPORT
void LOCALGC_CALLCONV
simplegc_get_norefsperm_stats(uint64_t* outBytesUsed,
                              uint64_t* outBytesCommitted)
{
    if (outBytesUsed)      *outBytesUsed      = (uint64_t)(g_norefsPerm.bump - g_norefsPerm.start);
    if (outBytesCommitted) *outBytesCommitted = (uint64_t)(g_norefsPerm.committed - g_norefsPerm.start);
}

// ---------------------------------------------------------------------------
// M1a: per-MethodTable stats public C ABI
// ---------------------------------------------------------------------------
//
// Managed code calls these via P/Invoke to read the per-MT counter table.
// MT pointers are returned as opaque uint64 tokens; managed side resolves
// them to type names via a RuntimeTypeHandle->MT map built at startup.

// Fill `buffer` with up to `capacity` populated entries from the MT table.
// Returns the number of entries written. The order is implementation-defined
// (currently table walk order). Empty/zero-count slots are skipped.
GC_EXPORT
uint32_t LOCALGC_CALLCONV
simplegc_get_mt_stats(SimpleGCMtStat* buffer, uint32_t capacity)
{
    if (buffer == nullptr || capacity == 0) return 0;
    uint32_t out = 0;
    for (size_t i = 0; i < kMtTableCapacity && out < capacity; ++i)
    {
        MethodTable* mt = g_mtTable[i].mt.load(std::memory_order_acquire);
        if (mt == nullptr) continue;
        uint64_t count = g_mtTable[i].count.load(std::memory_order_relaxed);
        if (count == 0) continue;
        buffer[out].mt_token = reinterpret_cast<uint64_t>(mt);
        buffer[out].count    = count;
        buffer[out].bytes    = g_mtTable[i].bytes.load(std::memory_order_relaxed);
        buffer[out].min_size = g_mtTable[i].min_size.load(std::memory_order_relaxed);
        buffer[out].max_size = g_mtTable[i].max_size.load(std::memory_order_relaxed);
        ++out;
    }
    return out;
}

// Reset all MT counters to zero. Useful between phases of a benchmark.
GC_EXPORT
void LOCALGC_CALLCONV
simplegc_reset_mt_stats()
{
    for (size_t i = 0; i < kMtTableCapacity; ++i)
    {
        g_mtTable[i].mt.store(nullptr, std::memory_order_release);
        g_mtTable[i].count.store(0, std::memory_order_relaxed);
        g_mtTable[i].bytes.store(0, std::memory_order_relaxed);
        g_mtTable[i].min_size.store(0, std::memory_order_relaxed);
        g_mtTable[i].max_size.store(0, std::memory_order_relaxed);
    }
    g_mtTableUsed.store(0, std::memory_order_relaxed);
    g_mtAttributedObjects.store(0, std::memory_order_relaxed);
    g_mtAttributedBytes.store(0, std::memory_order_relaxed);
    g_mtOverflowObjects.store(0, std::memory_order_relaxed);
}

// Aggregate counters: total objects/bytes attributed and overflow drops.
// Useful to verify the table is keeping up.
GC_EXPORT
void LOCALGC_CALLCONV
simplegc_get_mt_summary(uint64_t* outAttributedObjects,
                        uint64_t* outAttributedBytes,
                        uint32_t* outDistinctMts,
                        uint64_t* outOverflowObjects)
{
    if (outAttributedObjects) *outAttributedObjects = g_mtAttributedObjects.load(std::memory_order_relaxed);
    if (outAttributedBytes)   *outAttributedBytes   = g_mtAttributedBytes.load(std::memory_order_relaxed);
    if (outDistinctMts)       *outDistinctMts       = g_mtTableUsed.load(std::memory_order_relaxed);
    if (outOverflowObjects)   *outOverflowObjects   = g_mtOverflowObjects.load(std::memory_order_relaxed);
}

// Enable or disable per-MT tracking. Default is OFF — managed code must
// turn it on after CLR startup is complete (the post-hoc walk dereferences
// MethodTable* pointers we read out of allocation chunks, which is unsafe
// during very early reflection / EventSource initialization).
//
// Calling with `enable != 0` arms the walker. Calling with 0 disables and
// drops any pending per-thread chunk state on the next slow-path entry.
GC_EXPORT
void LOCALGC_CALLCONV
simplegc_enable_mt_tracking(int32_t enable)
{
    g_mtTrackingEnabled.store(enable != 0 ? 1 : 0, std::memory_order_release);
}

GC_EXPORT
int32_t LOCALGC_CALLCONV
simplegc_is_mt_tracking_enabled()
{
    return g_mtTrackingEnabled.load(std::memory_order_acquire);
}

// ---------------------------------------------------------------------------
// M1b: mark-sweep region routing and stats
// ---------------------------------------------------------------------------

// Public ABI struct for mark-sweep region stats. Append-only; static_assert
// keeps managed-side mirror in sync.
struct SimpleGCMarkSweepStats
{
    uint64_t bytes_allocated;
    uint64_t bytes_freelist;
    uint64_t bytes_live_after_collect;
    uint64_t bytes_collected_total;
    uint64_t n_collections;
    uint64_t n_objects_allocated;
    uint64_t n_objects_swept;
    uint64_t bytes_committed;
    uint64_t bytes_bumped;       // bump - start_obj
    uint64_t bytes_reserved;
};
static_assert(sizeof(SimpleGCMarkSweepStats) == 80, "SimpleGCMarkSweepStats ABI");

// Route the calling thread's allocations to the mark-sweep region.
// Must be paired with simplegc_route_to_marksweep(0) before the thread
// allocates objects you do NOT want collected (e.g. long-lived state).
//
// This flushes any cached alloc context first so the next allocation will
// fall back into our IGCHeap::Alloc and pick up the new routing.
GC_EXPORT
void LOCALGC_CALLCONV
simplegc_route_to_marksweep(int32_t enable)
{
    simplegc_flush_alloc_context(t_lastAllocCtx);
    t_useMarkSweep = (enable != 0);
}

GC_EXPORT
int32_t LOCALGC_CALLCONV
simplegc_is_routed_to_marksweep()
{
    return t_useMarkSweep ? 1 : 0;
}

GC_EXPORT
void LOCALGC_CALLCONV
simplegc_get_marksweep_stats(SimpleGCMarkSweepStats* out)
{
    if (out == nullptr) return;
    out->bytes_allocated          = g_marksweep.bytes_allocated.load(std::memory_order_relaxed);
    out->bytes_freelist           = g_marksweep.bytes_freelist.load(std::memory_order_relaxed);
    out->bytes_live_after_collect = g_marksweep.bytes_live_after_collect.load(std::memory_order_relaxed);
    out->bytes_collected_total    = g_marksweep.bytes_collected_total.load(std::memory_order_relaxed);
    out->n_collections            = g_marksweep.n_collections.load(std::memory_order_relaxed);
    out->n_objects_allocated      = g_marksweep.n_objects_allocated.load(std::memory_order_relaxed);
    out->n_objects_swept          = g_marksweep.n_objects_swept.load(std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> guard(g_marksweep.lock);
        out->bytes_committed = (uint64_t)(g_marksweep.committed - g_marksweep.start_obj + 64);
        out->bytes_bumped    = (uint64_t)(g_marksweep.bump - g_marksweep.start_obj);
    }
    out->bytes_reserved = (uint64_t)kMarkSweepSize;
}

// Forward declaration; implementation in the collection section below.
HRESULT simplegc_force_collect();

GC_EXPORT
HRESULT LOCALGC_CALLCONV
simplegc_collect_marksweep()
{
    return simplegc_force_collect();
}

// ---------------------------------------------------------------------------
// Adaptive routing: per-MT route field + managed policy callback ABI
// ---------------------------------------------------------------------------
//
// The managed policy reads a snapshot of per-MT data (alloc bytes/count,
// last-collection survivors, age) and writes back routing decisions via
// simplegc_set_route. The callback runs at the END of simplegc_force_collect,
// AFTER RestartEE — so it may allocate, log, and use rich logic. A bad policy
// can produce bad routing (= bad performance) but never breaks correctness:
// region implementations are independent of policy choices.
//
// Snapshot ABI is append-only with a version constant.

constexpr uint32_t kRoutingPolicyAbiVersion = 1;

struct SimpleGCRoutingEntry
{
    uint64_t mt_token;          // MethodTable* as opaque uint64
    uint64_t alloc_count;       // cumulative # of objects ever allocated
    uint64_t alloc_bytes;       // cumulative bytes ever allocated
    uint64_t survived_count;    // objects surviving the most recent collection
    uint64_t survived_bytes;    // bytes surviving the most recent collection
    uint32_t age_collections;   // # of collections this MT had any survivors
    uint32_t min_size;
    uint32_t max_size;
    uint8_t  current_route;     // current routing decision
    uint8_t  _pad0;
    uint16_t _pad1;
};
static_assert(sizeof(SimpleGCRoutingEntry) == 56, "SimpleGCRoutingEntry ABI");

// RoutingPolicyFn is invoked post-collection. The callback walks the snapshot
// it pulls via simplegc_get_routing_snapshot (allocating its own buffer) and
// writes back via simplegc_set_route. The callback is itself void-returning
// because a bad policy cannot fail meaningfully — failures degrade to "no
// routing change this cycle".
typedef void (LOCALGC_CALLCONV *RoutingPolicyFn)();

static std::atomic<RoutingPolicyFn> g_routingPolicy{nullptr};

GC_EXPORT
uint32_t LOCALGC_CALLCONV
simplegc_register_routing_policy(uint32_t abiVersion, RoutingPolicyFn cb)
{
    if (abiVersion != kRoutingPolicyAbiVersion)
    {
        LOG1("simplegc_register_routing_policy: ABI version mismatch (got %u, expected %u)",
             abiVersion, kRoutingPolicyAbiVersion);
        return 0;
    }
    g_routingPolicy.store(cb, std::memory_order_release);
    LOG1("simplegc_register_routing_policy: cb=%p", cb);
    return kRoutingPolicyAbiVersion;
}

// Fill `buffer` with up to `capacity` populated entries from the MT table.
// Returns the total number of populated entries (which may exceed capacity;
// caller can re-call with a larger buffer).
GC_EXPORT
uint32_t LOCALGC_CALLCONV
simplegc_get_routing_snapshot(SimpleGCRoutingEntry* buffer, uint32_t capacity)
{
    uint32_t total = 0;
    uint32_t emitted = 0;
    for (size_t i = 0; i < kMtTableCapacity; ++i)
    {
        SimpleGCMTEntry& e = g_mtTable[i];
        MethodTable* mt = e.mt.load(std::memory_order_relaxed);
        if (mt == nullptr) continue;
        ++total;
        if (buffer != nullptr && emitted < capacity)
        {
            SimpleGCRoutingEntry& out = buffer[emitted++];
            out.mt_token        = (uint64_t)mt;
            out.alloc_count     = e.count.load(std::memory_order_relaxed);
            out.alloc_bytes     = e.bytes.load(std::memory_order_relaxed);
            out.survived_count  = e.count_survived.load(std::memory_order_relaxed);
            out.survived_bytes  = e.bytes_survived.load(std::memory_order_relaxed);
            out.age_collections = e.age_collections.load(std::memory_order_relaxed);
            out.min_size        = e.min_size.load(std::memory_order_relaxed);
            out.max_size        = e.max_size.load(std::memory_order_relaxed);
            out.current_route   = e.route.load(std::memory_order_relaxed);
            out._pad0 = 0;
            out._pad1 = 0;
        }
    }
    return total;
}

// Write a routing decision for the MT identified by `mt_token`. Returns 1 on
// success, 0 if the MT is not present in the table. The route takes effect
// on the next slow-path Alloc that observes objects of this MT in its
// just-finished chunk (auto-routing) OR immediately for direct callers that
// also use simplegc_route_to_marksweep.
GC_EXPORT
uint32_t LOCALGC_CALLCONV
simplegc_set_route(uint64_t mt_token, uint8_t route)
{
    if (mt_token == 0) return 0;
    if (route > kRouteNoRefsPerm) return 0;
    MethodTable* mt = (MethodTable*)mt_token;
    size_t h = mt_hash(mt);
    for (size_t i = 0; i < kMtTableCapacity; ++i)
    {
        size_t idx = (h + i) & (kMtTableCapacity - 1);
        SimpleGCMTEntry& e = g_mtTable[idx];
        MethodTable* cur = e.mt.load(std::memory_order_acquire);
        if (cur == mt)
        {
            e.route.store(route, std::memory_order_release);
            return 1;
        }
        if (cur == nullptr)
        {
            return 0;
        }
    }
    return 0;
}

GC_EXPORT
uint8_t LOCALGC_CALLCONV
simplegc_get_route(uint64_t mt_token)
{
    if (mt_token == 0) return kRouteDefault;
    MethodTable* mt = (MethodTable*)mt_token;
    size_t h = mt_hash(mt);
    for (size_t i = 0; i < kMtTableCapacity; ++i)
    {
        size_t idx = (h + i) & (kMtTableCapacity - 1);
        SimpleGCMTEntry& e = g_mtTable[idx];
        MethodTable* cur = e.mt.load(std::memory_order_acquire);
        if (cur == mt)
        {
            return e.route.load(std::memory_order_acquire);
        }
        if (cur == nullptr)
        {
            return kRouteDefault;
        }
    }
    return kRouteDefault;
}

GC_EXPORT
void LOCALGC_CALLCONV
simplegc_enable_auto_routing(int32_t enable)
{
    t_autoRoute = (enable != 0);
}

GC_EXPORT
int32_t LOCALGC_CALLCONV
simplegc_is_auto_routing_enabled()
{
    return t_autoRoute ? 1 : 0;
}

// M1c: global default route. Setting this to a non-zero kRoute* value
// causes Alloc to route any allocation that has not been forced elsewhere
// (per-thread t_forceRoute, legacy t_useMarkSweep, request bracket) to
// the chosen region. Pass kRouteDefault (0) to clear.
GC_EXPORT
void LOCALGC_CALLCONV
simplegc_set_default_route(int32_t route)
{
    if (route < 0 || route > kRouteNoRefsPerm) return;
    g_defaultRoute.store(static_cast<uint8_t>(route), std::memory_order_release);
}

GC_EXPORT
int32_t LOCALGC_CALLCONV
simplegc_get_default_route()
{
    return static_cast<int32_t>(g_defaultRoute.load(std::memory_order_acquire));
}

// M1c: per-thread forced route. Highest-priority routing override (after
// the always-perm flags); used by policy-aware workloads to bracket a
// specific allocation site without affecting other threads. Pass
// kRouteDefault (0) to clear.
GC_EXPORT
void LOCALGC_CALLCONV
simplegc_set_thread_route(int32_t route)
{
    if (route < 0 || route > kRouteNoRefsPerm) return;
    t_forceRoute = static_cast<uint8_t>(route);
}

GC_EXPORT
int32_t LOCALGC_CALLCONV
simplegc_get_thread_route()
{
    return static_cast<int32_t>(t_forceRoute);
}

// Invoked at the end of simplegc_force_collect, AFTER RestartEE. The callback
// runs in normal cooperative mode and may allocate / log freely.
static void simplegc_invoke_routing_policy_post_collect()
{
    RoutingPolicyFn cb = g_routingPolicy.load(std::memory_order_acquire);
    if (cb == nullptr) return;
    cb();
}

// ---------------------------------------------------------------------------
// Symbols that gc_pal (gcenv.windows.cpp) expects from gcconfig.cpp.
// We don't pull in the regular GC's gcconfig.cpp, so define minimal stubs.
// ---------------------------------------------------------------------------

bool ParseIndexOrRange(const char** /*config_string*/, size_t* /*start_index*/, size_t* /*end_index*/)
{
    return false;
}

#include "gcconfig.h"

bool GCConfig::GetGCNumaAware()       { return false; }
bool GCConfig::GetGCCpuGroup(bool /*defaultValue*/) { return false; }
