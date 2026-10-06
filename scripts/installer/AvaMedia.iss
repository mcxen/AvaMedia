#ifndef AppVersion
  #define AppVersion "1.0.5"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{957F8776-D499-4687-B2CE-36041338AD68}
AppName=AvaMedia
AppVersion={#AppVersion}
AppPublisher=AvaMedia contributors
AppPublisherURL=https://github.com/mcxen/AvaMedia
AppSupportURL=https://github.com/mcxen/AvaMedia/issues
AppUpdatesURL=https://github.com/mcxen/AvaMedia/releases
DefaultDirName={localappdata}\Programs\AvaMedia
DefaultGroupName=AvaMedia
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#PublishDir}\LICENSE
SetupIconFile=..\..\src\AvaMedia.Desktop\Assets\AppIcon\v2\app.ico
UninstallDisplayIcon={app}\AvaMedia.Desktop.exe
OutputDir={#OutputDir}
OutputBaseFilename=AvaMedia-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\tools\deno.exe"
Type: files; Name: "{app}\licenses\download-tools\deno-*"
Type: files; Name: "{app}\scripts\Collect-DenoLicenses.js"

[Icons]
Name: "{group}\AvaMedia"; Filename: "{app}\AvaMedia.Desktop.exe"
Name: "{group}\Install media tools"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\Install-MediaTools.ps1"" -Destination ""{app}\tools"""; WorkingDir: "{app}"
Name: "{group}\Uninstall AvaMedia"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AvaMedia"; Filename: "{app}\AvaMedia.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AvaMedia.Desktop.exe"; Description: "Launch AvaMedia"; Flags: nowait postinstall skipifsilent
