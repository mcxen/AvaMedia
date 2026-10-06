using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Win32;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public static class SystemContextMenu
{
    private const string RegistryPath = @"Software\Classes\*\shell\AvaMedia.Convert";
    private const string ServiceId = "org.avamedia.convert-service";
    public static string WindowsCommand(string executable) => "\"" + Path.GetFullPath(executable) + "\" --convert \"%1\"";
    public static void Set(bool enabled, string executable)
    {
        if (enabled && !File.Exists(executable)) throw new FileNotFoundException("请先安装" + AppIdentity.ChineseName + "，再启用系统菜单。", executable);
        if (OperatingSystem.IsWindows())
        {
            if (!enabled) { Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false); return; }
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("", AppIdentity.ConvertMenuLabel); key.SetValue("Icon", Path.GetFullPath(executable));
            key.SetValue("MultiSelectModel", "Single");
            using var command = key.CreateSubKey("command"); command.SetValue("", WindowsCommand(executable));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var services = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Services");
            WriteMacService(services, executable, enabled);
            const string refresh = "/System/Library/CoreServices/pbs";
            if (File.Exists(refresh)) Process.Start(new ProcessStartInfo(refresh) { ArgumentList = { "-update" }, UseShellExecute = false, CreateNoWindow = true });
        }
        else if (enabled) throw new PlatformNotSupportedException("系统菜单支持 Windows 和 macOS。");
    }

    public static void WriteMacService(string servicesFolder, string executable, bool enabled)
    {
        var bundle = Path.Combine(Path.GetFullPath(servicesFolder), "AvaMedia Convert.workflow");
        var contents = Path.Combine(bundle, "Contents");
        var info = Path.Combine(contents, "Info.plist"); var workflow = Path.Combine(contents, "document.wflow");
        if (Directory.Exists(bundle) && (!File.Exists(info) || !File.ReadAllText(info).Contains(ServiceId, StringComparison.Ordinal)))
            throw new IOException("同名 Finder 服务已存在，请先重命名该服务。");
        if (!enabled)
        {
            if (!Directory.Exists(bundle)) return;
            File.Delete(workflow); File.Delete(info);
            // Delete only the two files owned by this feature; preserve any unexpected user files.
            if (!Directory.EnumerateFileSystemEntries(contents).Any()) Directory.Delete(contents);
            if (!Directory.EnumerateFileSystemEntries(bundle).Any()) Directory.Delete(bundle);
            return;
        }
        Directory.CreateDirectory(contents);
        Write(info, MacInfo()); Write(workflow, MacWorkflow(executable));
    }
    private static void Write(string path, string content)
    { File.WriteAllText(path + ".tmp", content); File.Move(path + ".tmp", path, true); }
    private static XElement Dict(params object[] entries) => new("dict", entries);
    private static object[] Pair(string key, object value) => [new XElement("key", key), value];
    private static XElement String(string value) => new("string", value);
    private static XElement Array(params object[] items) => new("array", items);
    private static string Plist(XElement dict) => new XDocument(new XDeclaration("1.0", "UTF-8", null),
        new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
        new XElement("plist", new XAttribute("version", "1.0"), dict)).ToString();
    public static string MacInfo()
    {
        var service = Dict(Pair("NSMenuItem", Dict(Pair("default", String(AppIdentity.ConvertMenuLabel)))),
            Pair("NSMessage", String("runWorkflowAsService")),
            Pair("NSRequiredContext", Dict(Pair("NSApplicationIdentifier", String("com.apple.finder")))),
            Pair("NSSendFileTypes", Array(String("public.item"))), Pair("NSSendTypes", Array(String("NSFilenamesPboardType"))));
        return Plist(Dict(Pair("CFBundleIdentifier", String(ServiceId)), Pair("CFBundleName", String(AppIdentity.ChineseName)), Pair("CFBundleDisplayName", String(AppIdentity.ChineseName)), Pair("NSServices", Array(service))));
    }
    public static string MacScript(string executable)
    {
        var full=Path.GetFullPath(executable);
        for(var directory=new DirectoryInfo(Path.GetDirectoryName(full)!);directory is not null;directory=directory.Parent)
            if(directory.Name.EndsWith(".app",StringComparison.OrdinalIgnoreCase))
                return "/usr/bin/open -n " + Posix(directory.FullName) + " --args --convert \"$@\"";
        return Posix(full) + " --convert \"$@\" >/dev/null 2>&1 &";
    }
    private static string Posix(string value) => "'" + value.Replace("'", "'\\''") + "'";
    public static string MacWorkflow(string executable)
    {
        var action = Dict(
            Pair("AMAccepts", Dict(Pair("Container", String("List")), Pair("Optional", new XElement("false")), Pair("Types", Array(String("com.apple.cocoa.path"))))),
            Pair("AMProvides", Dict(Pair("Container", String("List")), Pair("Types", Array(String("com.apple.cocoa.string"))))),
            Pair("AMActionVersion", String("2.0.3")), Pair("AMApplication", Array(String("Automator"))),
            Pair("AMParameterProperties", Dict(Pair("COMMAND_STRING",Dict()),Pair("inputMethod",Dict()),Pair("shell",Dict()))),
            Pair("CFBundleVersion",String("2.0.3")),Pair("Class Name",String("RunShellScriptAction")),
            Pair("InputUUID",String(Guid.NewGuid().ToString())),Pair("OutputUUID",String(Guid.NewGuid().ToString())),Pair("UUID",String(Guid.NewGuid().ToString())),
            Pair("ActionBundlePath", String("/System/Library/Automator/Run Shell Script.action")),
            Pair("ActionName", String("Run Shell Script")), Pair("BundleIdentifier", String("com.apple.RunShellScript")),
            Pair("ActionParameters", Dict(Pair("COMMAND_STRING", String(MacScript(executable))), Pair("inputMethod", new XElement("integer", 1)), Pair("shell", String("/bin/zsh")))));
        var entry = Dict(Pair("action", action), Pair("isViewVisible", new XElement("true")));
        var metadata = Dict(Pair("serviceApplicationBundleID", String("com.apple.finder")),
            Pair("serviceApplicationPath", String("/System/Library/CoreServices/Finder.app")),
            Pair("serviceInputTypeIdentifier", String("com.apple.Automator.fileSystemObject")),
            Pair("serviceOutputTypeIdentifier", String("com.apple.Automator.nothing")),
            Pair("workflowTypeIdentifier", String("com.apple.Automator.servicesMenu")));
        return Plist(Dict(Pair("AMDocumentVersion", String("2")), Pair("connectors", Array()), Pair("actions", Array(entry)), Pair("workflowMetaData", metadata)));
    }
}
