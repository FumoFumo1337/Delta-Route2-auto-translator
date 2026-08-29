using System;
using System.Runtime.InteropServices;

internal static class QuoteRoundTrip
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static int Main()
    {
        string[] values =
        {
            string.Empty,
            "plain",
            "two words",
            "tab\tseparated",
            "C:\\folder with spaces\\",
            "C:\\ends-in-two-slashes\\\\",
            "embedded\"quote",
            "slashes-before-quote\\\\\"tail",
            "русский путь\\файл.xlsx"
        };

        foreach (string expected in values)
        {
            string commandLine = "program.exe " + DeltaProject.QuoteArgument(expected);
            int count;
            IntPtr argv = CommandLineToArgvW(commandLine, out count);
            if (argv == IntPtr.Zero)
            {
                Console.Error.WriteLine("CommandLineToArgvW failed: " + Marshal.GetLastWin32Error());
                return 1;
            }
            try
            {
                if (count != 2)
                {
                    Console.Error.WriteLine("Unexpected argument count for " + expected + ": " + count);
                    return 1;
                }
                IntPtr valuePointer = Marshal.ReadIntPtr(argv, IntPtr.Size);
                string actual = Marshal.PtrToStringUni(valuePointer);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "Round trip mismatch. Expected <" + expected + ">, got <" + actual + ">.");
                    return 1;
                }
            }
            finally
            {
                LocalFree(argv);
            }
        }

        Console.WriteLine("Windows argument quote round trips: " + values.Length);
        return 0;
    }
}
