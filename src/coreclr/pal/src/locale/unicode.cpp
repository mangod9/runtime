// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*++
Module Name:
unicode.cpp

Abstract:
Implementation of all functions related to Unicode support
--*/

#include "pal/thread.hpp"

#include "pal/palinternal.h"
#include "pal/dbgmsg.h"
#include "pal/file.h"
#include <minipal/strings.h>
#include <minipal/utf8.h>
#include "pal/cruntime.h"
#include "pal/stackstring.hpp"

#include <pthread.h>
#include <locale.h>
#include <errno.h>

#include <debugmacrosext.h>

using namespace CorUnix;

SET_DEFAULT_DEBUG_CHANNEL(UNICODE);

/*++
Function:
GetConsoleOutputCP

See MSDN doc.
--*/
UINT
PALAPI
GetConsoleOutputCP(
       VOID)
{
    return CP_UTF8;
}

/*++
Function:
MultiByteToWideChar

See MSDN doc.

--*/
int
PALAPI
MultiByteToWideChar(
        IN UINT CodePage,
        IN DWORD dwFlags,
        IN LPCSTR lpMultiByteStr,
        IN int cbMultiByte,
        OUT LPWSTR lpWideCharStr,
        IN int cchWideChar)
{
    INT retval =0;

    PERF_ENTRY(MultiByteToWideChar);
    ENTRY("MultiByteToWideChar(CodePage=%u, dwFlags=%#x, lpMultiByteStr=%p (%s),"
    " cbMultiByte=%d, lpWideCharStr=%p, cchWideChar=%d)\n",
    CodePage, dwFlags, lpMultiByteStr?lpMultiByteStr:"NULL", lpMultiByteStr?lpMultiByteStr:"NULL",
    cbMultiByte, lpWideCharStr, cchWideChar);

    if (dwFlags & ~(MB_ERR_INVALID_CHARS | MB_PRECOMPOSED))
    {
        ASSERT("Error dwFlags(0x%x) parameter is invalid\n", dwFlags);
        SetLastError(ERROR_INVALID_FLAGS);
        goto EXIT;
    }

    if ( (cbMultiByte == 0) || (cchWideChar < 0) ||
        (lpMultiByteStr == NULL) ||
        ((cchWideChar != 0) &&
        ((lpWideCharStr == NULL) ||
        (lpMultiByteStr == (LPSTR)lpWideCharStr))) )
    {
        ERROR("Error lpMultiByteStr parameters are invalid\n");
        SetLastError(ERROR_INVALID_PARAMETER);
        goto EXIT;
    }

    if (CodePage == CP_UTF8 || CodePage == CP_ACP)
    {
        if (cbMultiByte < 0)
            cbMultiByte = strlen(lpMultiByteStr) + 1;

        if (!lpWideCharStr || cchWideChar == 0)
            retval = minipal_get_length_utf8_to_utf16(lpMultiByteStr, cbMultiByte, dwFlags);

        if (lpWideCharStr)
        {
            if (cchWideChar == 0) cchWideChar = retval;
            retval = minipal_convert_utf8_to_utf16(lpMultiByteStr, cbMultiByte, (CHAR16_T*)lpWideCharStr, cchWideChar, dwFlags);
        }

        goto EXIT;
    }

    ERROR( "This code page is not in the system.\n" );
    SetLastError( ERROR_INVALID_PARAMETER );
    goto EXIT;

EXIT:

    LOGEXIT("MultiByteToWideChar returns %d.\n",retval);
    PERF_EXIT(MultiByteToWideChar);
    return retval;
}


/*++
Function:
WideCharToMultiByte

See MSDN doc.

--*/
int
PALAPI
WideCharToMultiByte(
        IN UINT CodePage,
        IN DWORD dwFlags,
        IN LPCWSTR lpWideCharStr,
        IN int cchWideChar,
        OUT LPSTR lpMultiByteStr,
        IN int cbMultiByte,
        IN LPCSTR lpDefaultChar,
        OUT LPBOOL lpUsedDefaultChar)
{
    INT retval =0;
    char defaultChar = '?';
    BOOL usedDefaultChar = FALSE;

    PERF_ENTRY(WideCharToMultiByte);
    ENTRY("WideCharToMultiByte(CodePage=%u, dwFlags=%#x, lpWideCharStr=%p (%S), "
          "cchWideChar=%d, lpMultiByteStr=%p, cbMultiByte=%d, "
          "lpDefaultChar=%p, lpUsedDefaultChar=%p)\n",
          CodePage, dwFlags, lpWideCharStr?lpWideCharStr:W16_NULLSTRING, lpWideCharStr?lpWideCharStr:W16_NULLSTRING,
          cchWideChar, lpMultiByteStr, cbMultiByte,
          lpDefaultChar, lpUsedDefaultChar);

    if (dwFlags & ~WC_NO_BEST_FIT_CHARS)
    {
        ERROR("dwFlags %d invalid\n", dwFlags);
        SetLastError(ERROR_INVALID_FLAGS);
        goto EXIT;
    }

    // No special action is needed for WC_NO_BEST_FIT_CHARS. The default
    // behavior of this API on Unix is not to find the best fit for a unicode
    // character that does not map directly into a code point in the given
    // code page. The best fit functionality is not available in wctomb on Unix
    // and is better left unimplemented for security reasons anyway.

    if ((cchWideChar < -1) || (cbMultiByte < 0) ||
        (lpWideCharStr == NULL) ||
        ((cbMultiByte != 0) &&
        ((lpMultiByteStr == NULL) ||
        (lpWideCharStr == (LPWSTR)lpMultiByteStr))) )
    {
        ERROR("Error lpWideCharStr parameters are invalid\n");
        SetLastError(ERROR_INVALID_PARAMETER);
        goto EXIT;
    }

    if (lpDefaultChar != NULL)
    {
        defaultChar = *lpDefaultChar;
    }

    if (CodePage == CP_UTF8 || CodePage == CP_ACP)
    {
        if (cchWideChar < 0)
            cchWideChar = minipal_u16_strlen((CHAR16_T*)lpWideCharStr) + 1;

        if (!lpMultiByteStr || cbMultiByte == 0)
            retval = minipal_get_length_utf16_to_utf8((CHAR16_T*)lpWideCharStr, cchWideChar, dwFlags);

        if (lpMultiByteStr)
        {
            if (cbMultiByte == 0) cbMultiByte = retval;
            retval = minipal_convert_utf16_to_utf8((CHAR16_T*)lpWideCharStr, cchWideChar, lpMultiByteStr, cbMultiByte, dwFlags);
        }

        goto EXIT;
    }

    ERROR( "This code page is not in the system.\n" );
    SetLastError( ERROR_INVALID_PARAMETER );
    goto EXIT;

EXIT:

    if ( lpUsedDefaultChar != NULL )
    {
        *lpUsedDefaultChar = usedDefaultChar;
    }

    /* Flag the cases when WC_NO_BEST_FIT_CHARS was not specified
     * but we found characters that had to be replaced with default
     * characters. Note that Windows would have attempted to find
     * best fit characters under these conditions and that could pose
     * a security risk.
     */
    _ASSERT_MSG((dwFlags & WC_NO_BEST_FIT_CHARS) || !usedDefaultChar,
          "WideCharToMultiByte found a string which doesn't round trip: (%p)%S "
          "and WC_NO_BEST_FIT_CHARS was not specified\n",
          lpWideCharStr, lpWideCharStr);

    LOGEXIT("WideCharToMultiByte returns INT %d\n", retval);
    PERF_EXIT(WideCharToMultiByte);
    return retval;
}

extern char * g_szCoreCLRPath;
