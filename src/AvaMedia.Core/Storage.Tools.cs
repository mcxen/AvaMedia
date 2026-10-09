namespace AvaMedia.Core;

public sealed partial class Storage
{
    public T? LoadToolOptions<T>(string tool) => Read<T>(ToolFile(tool));
    public void SaveToolOptions<T>(string tool, T options) => Write(ToolFile(tool), options);
    private static string ToolFile(string tool)
    {
        if (tool.Length == 0 || tool.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Invalid tool identifier.");
        return "tool-" + tool + ".json";
    }
}
