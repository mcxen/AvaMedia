using System.Reflection;
using Xunit;

// dotnet run --project tests/AvaMedia.PersonClipTests: backend editing only, no models/media/UI.
// dotnet test -p:PersonClipCoreOnly=true retains the project's xUnit entry point.
// The original media/UI harness is available with -p:PersonClipE2E=true -- core|ui|Balanced|Recall|Consensus.
internal static class BackendProgram
{
    public static async Task<int> Main()
    {
        var tests = new PersonClipEditTests();
        var count = 0;
        try
        {
            foreach (var method in typeof(PersonClipEditTests).GetMethods().Where(method => method.GetCustomAttribute<FactAttribute>() is not null).OrderBy(method => method.Name))
            {
                var result = method.Invoke(tests, null);
                if (result is Task task) await task;
                Console.WriteLine("PASS: " + method.Name); count++;
            }
            Console.WriteLine($"Passed {count} person-clip backend cases."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? error.InnerException : error); return 1; }
    }
}
