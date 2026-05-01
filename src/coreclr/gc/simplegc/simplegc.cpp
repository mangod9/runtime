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
#include "objecthandle.h"

#include <atomic>
#include <vector>
#include <mutex>

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

static void simplegc_log(int level, const char* fmt, ...)
{
    if (level > SIMPLEGC_TRACE) return;
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

// ---------------------------------------------------------------------------
// Globals required by the standalone GC contract
// ---------------------------------------------------------------------------

namespace
{
    // Heap parameters
    constexpr size_t kHeapSize       = 256 * 1024 * 1024; // 256 MB reserved
    constexpr size_t kCommitGrain    = 16 * 1024 * 1024;  // commit in 16 MB chunks
    constexpr size_t kAllocCtxQuant  = 8 * 1024;          // 8 KB per per-thread alloc context refill

    // Bump-pointer arena state
    std::mutex         g_heapLock;
    uint8_t*           g_heapStart    = nullptr;
    uint8_t*           g_heapCommitted= nullptr; // first uncommitted byte
    uint8_t*           g_heapEnd      = nullptr; // first reserved-but-uncommitted byte (heapStart + kHeapSize)
    uint8_t*           g_bumpPtr      = nullptr; // first free byte

    std::atomic<uint64_t> g_totalAllocated{0};
    std::atomic<uint64_t> g_objectCount{0};
    std::atomic<uint64_t> g_gcCount{0};

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

    g_heapCommitted = g_heapStart;
    g_heapEnd       = g_heapStart + kHeapSize;
    // The runtime reads SIZEOF_OBJHEADER (8 bytes on x64) of zero memory immediately
    // *before* every Object pointer to inspect the SyncBlock index. Reserve a small
    // padding area at the start of the heap so the first object pointer we hand out
    // has a valid (committed, zero-initialized) sync-block prefix.
    constexpr size_t kStartPadding = 64;
    g_bumpPtr       = g_heapStart + kStartPadding;
    // Force a commit on the start padding so the first read of obj-8 is mapped.
    if (!GCToOSInterface::VirtualCommit(g_heapStart, kStartPadding))
    {
        LOG1("VirtualCommit(start padding) failed");
        return false;
    }
    g_heapCommitted = g_heapStart + ((kStartPadding + 4095) & ~static_cast<size_t>(4095));

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

    LOG1("heap reserved at %p .. %p (size=%zu MB)", g_heapStart, g_heapEnd, kHeapSize >> 20);
    return true;
}

static bool simplegc_commit(size_t bytesNeeded)
{
    // Caller holds g_heapLock.
    if (g_bumpPtr + bytesNeeded > g_heapCommitted)
    {
        size_t want = (size_t)(g_bumpPtr + bytesNeeded - g_heapCommitted);
        size_t grain = (want + kCommitGrain - 1) & ~(kCommitGrain - 1);
        if (g_heapCommitted + grain > g_heapEnd)
        {
            LOG1("OOM: cannot commit additional %zu bytes (used=%zu)",
                 grain, (size_t)(g_bumpPtr - g_heapStart));
            return false;
        }
        if (!GCToOSInterface::VirtualCommit(g_heapCommitted, grain))
        {
            LOG1("VirtualCommit(%zu) failed", grain);
            return false;
        }
        g_heapCommitted += grain;
    }
    return true;
}

// Bump-pointer raw allocate. Caller is responsible for setting MethodTable.
static uint8_t* simplegc_raw_alloc(size_t size)
{
    std::lock_guard<std::mutex> guard(g_heapLock);
    // Align to 8 (small object alignment).
    size = (size + 7) & ~static_cast<size_t>(7);
    if (!simplegc_commit(size))
        return nullptr;
    uint8_t* p = g_bumpPtr;
    g_bumpPtr += size;
    g_totalAllocated.fetch_add(size, std::memory_order_relaxed);
    g_objectCount.fetch_add(1, std::memory_order_relaxed);
    return p;
}

// ---------------------------------------------------------------------------
// SimpleHandleStore / SimpleHandleManager
// ---------------------------------------------------------------------------
//
// We back handles with a vector<Object*>. The OBJECTHANDLE is the address of
// the slot. This is safe as long as the vector never reallocates — we use a
// chunked storage strategy.

namespace simplegc_handles
{
    constexpr size_t kBucketSize = 4096;

    struct Bucket
    {
        Object* slots[kBucketSize];
        Bucket() { memset(slots, 0, sizeof(slots)); }
    };

    std::mutex               g_handleLock;
    std::vector<Bucket*>     g_buckets;
    size_t                   g_nextSlot = 0; // grows monotonically; we never reclaim

    static OBJECTHANDLE alloc_slot(Object* initial)
    {
        std::lock_guard<std::mutex> guard(g_handleLock);
        size_t bucketIdx = g_nextSlot / kBucketSize;
        size_t inIdx     = g_nextSlot % kBucketSize;
        if (bucketIdx >= g_buckets.size())
        {
            g_buckets.push_back(new Bucket());
        }
        Bucket* b = g_buckets[bucketIdx];
        b->slots[inIdx] = initial;
        OBJECTHANDLE h = reinterpret_cast<OBJECTHANDLE>(&b->slots[inIdx]);
        ++g_nextSlot;
        return h;
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
    OBJECTHANDLE CreateDependentHandle(Object* primary, Object* /*secondary*/) override
    { TRACE_METHOD(); return simplegc_handles::alloc_slot(primary); }
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
        if (handle != nullptr) *reinterpret_cast<Object**>(handle) = nullptr;
    }
    void DestroyHandleOfUnknownType(OBJECTHANDLE handle) override
    {
        TRACE_METHOD();
        if (handle != nullptr) *reinterpret_cast<Object**>(handle) = nullptr;
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
    void SetDependentHandleSecondary(OBJECTHANDLE, Object*) override { TRACE_METHOD(); }
    Object* GetDependentHandleSecondary(OBJECTHANDLE) override { TRACE_METHOD(); return nullptr; }

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
        if (totalCommittedBytes)           *totalCommittedBytes = (size_t)(g_heapCommitted - g_heapStart);
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
        // No-op: we don't actually collect. Just bump the counter.
        g_gcCount.fetch_add(1, std::memory_order_relaxed);
        return S_OK;
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
        // Reset the alloc context so subsequent allocations refill from the bump pointer.
        if (acontext != nullptr)
        {
            acontext->alloc_ptr   = nullptr;
            acontext->alloc_limit = nullptr;
        }
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
        // Refill the alloc context with a fresh chunk and place this object inside.
        // The runtime fast-path will then bump-allocate from the context until it's
        // exhausted again.
        size_t chunkSize = (size > kAllocCtxQuant) ? size : kAllocCtxQuant;
        uint8_t* chunk = simplegc_raw_alloc(chunkSize);
        if (chunk == nullptr)
        {
            LOG1("Alloc(size=%zu, flags=0x%x) FAILED", size, flags);
            return nullptr;
        }

        static int s_traceCount = 0;
        if (s_traceCount++ < 20)
        {
            LOG1("Alloc[%d] size=%zu flags=0x%x -> %p", s_traceCount, size, flags, chunk);
        }

        // Object goes at the beginning of the chunk.
        Object* obj = reinterpret_cast<Object*>(chunk);

        if (acontext != nullptr)
        {
            acontext->alloc_ptr   = chunk + size;
            acontext->alloc_limit = chunk + chunkSize;
            acontext->alloc_bytes += (int64_t)size;
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
        if (ppAllocated) *ppAllocated = g_bumpPtr;
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
