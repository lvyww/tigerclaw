using System;
using InputMethodKit;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine(typeof(IMKServer).FullName);
        Console.WriteLine(typeof(IMKInputController).FullName);
    }
}
