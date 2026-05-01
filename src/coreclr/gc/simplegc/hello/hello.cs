using System;

class Program
{
    static int Main()
    {
        Console.WriteLine("[hello] Process started under SimpleGC");

        // Force a few small allocations to exercise the bump allocator.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            sb.Append("alloc-" + i + ";");
        }
        Console.WriteLine("[hello] Built string: " + sb.ToString());

        // Allocate a small array
        var arr = new int[16];
        for (int i = 0; i < arr.Length; i++) arr[i] = i * i;
        Console.WriteLine("[hello] Sum of squares = " + Sum(arr));

        Console.WriteLine("[hello] Done.");
        return 0;
    }

    static int Sum(int[] a)
    {
        int s = 0;
        foreach (var v in a) s += v;
        return s;
    }
}
