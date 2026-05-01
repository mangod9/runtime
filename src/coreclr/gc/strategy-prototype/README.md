# GC Strategy Prototype — Option C Architecture

> **Managed Strategy + Native Mechanics** for LLM-generated, app-specific garbage collection.

## Architecture

```
┌─────────────────────────────────────────────┐
│  Strategy Layer (C# — LLM-generated)         │
│  • When to collect (ShouldCollect)           │
│  • What to promote (ShouldPromote)           │
│  • React to app events (OnAppEvent)          │
├─────────────────────────────────────────────┤
│  Mechanics Layer (C++ in production)         │
│  • Memory page management                    │
│  • Object header layout                      │
│  • Write barriers                            │
│  • Mark/sweep/compact execution              │
│  • Thread suspension                         │
│  • Stack scanning                            │
└─────────────────────────────────────────────┘
```

## How It Works

1. The **mechanics layer** (simulated in `GCMechanics.cs`, would be C++ in production)
   handles all memory operations. It calls into the strategy for decisions.

2. The **strategy layer** (`IGCStrategy`) is a pure decision-maker. It receives
   telemetry and returns plans. It never touches raw memory.

3. An **LLM** analyzes an app's codebase and generates a custom `IGCStrategy`
   implementation optimized for that app's allocation patterns.

## Running the Demo

```bash
cd src/coreclr/gc/strategy-prototype
dotnet run --project GCStrategy.Demo
```

## Strategies Included

| Strategy | Optimized For | Key Insight |
|----------|---------------|-------------|
| `GenericStrategy` | Everything (poorly) | Threshold-based, age-promotion, no app knowledge |
| `WebApiStrategy` | HTTP request workloads | Bulk-free at request boundaries, skip marking |
| `GameLoopStrategy` | Real-time game loops | Never pause during frame, budget-limited work between frames |

## LLM Generation

See `LLM-PROMPT-TEMPLATE.md` for the prompt structure used to generate strategies.

## Production Integration Path

The standalone GC mechanism (`DOTNET_GCName` / `IGCHeap` interface) provides the real plug point:

1. Build a native GC DLL that implements `IGCHeap`
2. The native DLL delegates policy decisions to managed code via reverse P/Invoke
3. Bootstrap: start with default strategy, managed strategy registers once runtime is up
4. `DOTNET_GCName=CustomStrategyGC.dll` loads it at runtime startup

Key files in the runtime:
- `src/coreclr/gc/gcload.cpp` — GC DLL entry points (`GC_Initialize`, `GC_VersionInfo`)
- `src/coreclr/gc/gcinterface.h:657+` — `IGCHeap` interface
- `src/coreclr/vm/gcheaputilities.cpp:230+` — standalone GC loading
- `src/coreclr/gc/sample/` — minimal GC implementation example
