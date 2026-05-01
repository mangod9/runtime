# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
#
# simplegc.dll — minimal "Hello World" standalone GC for experimentation.
#
# This deliberately does NOT pull in the standard GC sources (gc.cpp, gccommon.cpp,
# handletable*.cpp, etc.). It is a from-scratch implementation that:
#   * exports GC_VersionInfo and GC_Initialize
#   * implements all IGCHeap / IGCHandleManager methods (mostly as no-op stubs)
#   * uses a single bump-pointer arena (no collection)
#
# Load with: setx DOTNET_GCName simplegc.dll
project(simplegc)

set(CMAKE_INCLUDE_CURRENT_DIR ON)

# Headers from the regular GC tree provide IGCHeap, gc_alloc_context, etc.
include_directories(..)
include_directories(../env)

set(SIMPLEGC_SOURCES
    simplegc.cpp
)

# Standalone-GC defines. We need BUILD_AS_STANDALONE so gcenv.ee.standalone.inl
# wires GCToEEInterface::* through g_theGCToCLR.
add_definitions(-DBUILD_AS_STANDALONE)
add_definitions(-DFEATURE_STANDALONE_GC)
# Note: FX_VER_INTERNALNAME_STR is already defined as clrgc.dll by the parent
# CMakeLists. We deliberately leave it as-is so the resource compiler doesn't
# choke on a duplicate definition. The output filename is what matters anyway.

if(CLR_CMAKE_HOST_WIN32)
    add_definitions(-DUNICODE)
    add_compile_definitions(NOMINMAX)
endif()

add_library_clr(simplegc SHARED ${SIMPLEGC_SOURCES})

# We need the GC's PAL (gc_pal) for GCToOSInterface (VirtualReserve/Commit etc.)
# and minipal for low-level helpers.
target_link_libraries(simplegc PRIVATE gc_pal minipal)

if(CLR_CMAKE_HOST_WIN32)
    target_link_libraries(simplegc PRIVATE
        ${STATIC_MT_CRT_LIB}
        ${STATIC_MT_VCRT_LIB}
        kernel32.lib
        advapi32.lib
    )
endif()

install_clr(TARGETS simplegc DESTINATIONS . COMPONENT runtime)
install_clr(TARGETS simplegc DESTINATIONS . sharedFramework COMPONENT runtime)
