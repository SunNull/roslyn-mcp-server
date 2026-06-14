// Sample C# file with intentional errors — used to verify roslyn_diagnostics
// surfaces real compilation problems. Run the MCP server, call
// roslyn_diagnostics on this file, and you should see CS errors.

using System;

class Program
{
    static void Main(string[] args)
    {
        // Error CS0103: 'undefinedVariable' does not exist
        Console.WriteLine(undefinedVariable);

        // Error CS0029: cannot convert 'string' to 'int'
        int number = "not a number";

        // Warning CS0219: variable assigned but never used
        var unused = 42;

        // Missing closing brace — syntax error
}
