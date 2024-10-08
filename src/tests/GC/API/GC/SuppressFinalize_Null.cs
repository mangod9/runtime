// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Tests SuppressFinalize()

using System;
using Xunit;

public class Test_SuppressFinalize_Null
{
    public bool RunTest()
    {
        try
        {
            GC.SuppressFinalize(null);  // should not call the Finalizer() for obj1
        }
        catch (ArgumentNullException)
        {
            return true;
        }
        catch (Exception)
        {
            Console.WriteLine("Unexpected Exception!");
        }

        return false;
    }


    [Fact]
    public static int TestEntryPoint()
    {
        Test_SuppressFinalize_Null t = new Test_SuppressFinalize_Null();
        if (t.RunTest())
        {
            Console.WriteLine("Null test for SuppressFinalize() passed!");
            return 100;
        }

        Console.WriteLine("Null test for SuppressFinalize() failed!");
        return 1;
    }
}
