// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Diagnostics
{
    public static partial class Debugger
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        [DebuggerHidden] // this helps VS appear to stop on the source line calling Debugger.Break() instead of inside it
        public static void Break()
        {
#if TARGET_WINDOWS
            // IsAttached is always true when IsDebuggerPresent is true, so no need to check for it
            if (Debugger.IsNativeDebuggerAttached())
                Debug.DebugBreak();
#else
            // UNIXTODO: Implement Debugger.Break
#endif
        }

        public static bool IsAttached
        {
            get
            {
                // Managed debugger is never attached because we don't have one
                return false;
            }
        }

        public static bool Launch()
        {
            throw new PlatformNotSupportedException();
        }

        public static void NotifyOfCrossThreadDependency()
        {
            // nothing to do...yet
        }

        /// <summary>
        /// Posts a message for the attached debugger.  If there is no
        /// debugger attached, has no effect.  The debugger may or may not
        /// report the message depending on its settings.
        /// </summary>
        public static void Log(int level, string category, string message)
        {
            if (IsLogging())
            {
                throw new NotImplementedException(); // TODO: Implement Debugger.Log, IsLogging
            }
        }

        /// <summary>
        /// Checks to see if an attached debugger has logging enabled
        /// </summary>
        public static bool IsLogging()
        {
            if (string.Empty.Length != 0)
            {
                throw new NotImplementedException(); // TODO: Implement Debugger.Log, IsLogging
            }
            return false;
        }

        internal static bool IsNativeDebuggerAttached() => IsNativeDebuggerAttachedInternal() != 0;

        [LibraryImport(RuntimeImports.RuntimeLibrary, EntryPoint = "DebugDebugger_IsNativeDebuggerAttached")]
        private static partial int IsNativeDebuggerAttachedInternal();
    }
}
